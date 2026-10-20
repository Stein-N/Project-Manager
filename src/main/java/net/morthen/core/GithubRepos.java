package net.morthen.core;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import java.io.IOException;
import java.io.StringReader;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.Properties;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.function.BiConsumer;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Lists every repository the signed-in GitHub account can see – its own, those of its organizations and the ones it
 * collaborates on – and recognizes which of them are Minecraft mods: their {@code gradle.properties} has {@value #MOD_PROPERTY}.
 */
public final class GithubRepos {
    /** Variable with the personal access token (.env in the master folder, then the system environment). */
    public static final String VARIABLE = "GITHUB_TOKEN";
    /** A repository whose gradle.properties defines this is a Minecraft mod. */
    public static final String MOD_PROPERTY = "minecraft_version";

    /** @param owner user or organization */
    public record Repo(String fullName, String owner, String name, String description, String cloneUrl, String pageUrl,
                       boolean isPrivate, boolean fork, boolean archived, String pushedAt) {
    }

    private static final HttpClient HTTP = HttpClient.newBuilder()
            .connectTimeout(Duration.ofSeconds(10))
            .followRedirects(HttpClient.Redirect.NORMAL)
            .build();
    private static final Pattern NEXT = Pattern.compile("<([^>]+)>;\\s*rel=\"next\"");
    private static final int MAX_PAGES = 50;
    private static final int THREADS = 8;
    /** Answers per repository and last push, so a reload only checks what changed. */
    private static final Map<String, Boolean> MOD_CACHE = new ConcurrentHashMap<>();

    private GithubRepos() {
    }

    /** The token from {@code vars} (the .env), else the system environment; empty if there is none. */
    public static String token(Map<String, String> vars, Map<String, String> system) {
        for (String source : new String[]{vars.get(VARIABLE), system.get(VARIABLE), system.get("GH_TOKEN")}) {
            if (source != null && !source.isBlank()) return source.strip();
        }
        return "";
    }

    /** All repositories of the account behind {@code token}, most recently pushed first. */
    public static List<Repo> fetchAll(String token) throws IOException {
        if (token == null || token.isBlank()) throw new IOException("No GitHub token – set " + VARIABLE + " in the .env.");
        List<Repo> repos = new ArrayList<>();
        String url = "https://api.github.com/user/repos?per_page=100&sort=pushed"
                + "&affiliation=owner,collaborator,organization_member";
        for (int page = 0; url != null && page < MAX_PAGES; page++) {
            HttpResponse<String> response = get(url, token, "application/vnd.github+json");
            check(response);
            repos.addAll(parse(response.body()));
            url = next(response.headers().firstValue("Link").orElse(null));
        }
        return repos;
    }

    /**
     * Checks the repositories' {@code gradle.properties} in parallel and reports each mod as soon as it is known.
     *
     * @param progress called with (repositories checked so far, repository if it is a mod else null); from worker threads
     */
    public static void detectMods(String token, List<Repo> repos, BiConsumer<Integer, Repo> progress) throws IOException {
        ExecutorService pool = Executors.newFixedThreadPool(THREADS);
        try {
            List<Future<Boolean>> futures = new ArrayList<>();
            for (Repo repo : repos) futures.add(pool.submit(() -> isMod(token, repo)));
            for (int i = 0; i < repos.size(); i++) {
                boolean mod = futures.get(i).get();
                progress.accept(i + 1, mod ? repos.get(i) : null);
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new IOException("interrupted");
        } catch (java.util.concurrent.ExecutionException e) {
            throw e.getCause() instanceof IOException io ? io : new IOException(e.getCause());
        } finally {
            pool.shutdownNow();
        }
    }

    private static boolean isMod(String token, Repo repo) throws IOException {
        String key = repo.fullName() + "@" + repo.pushedAt();
        Boolean cached = MOD_CACHE.get(key);
        if (cached != null) return cached;
        HttpResponse<String> response = get("https://api.github.com/repos/" + repo.fullName() + "/contents/gradle.properties",
                token, "application/vnd.github.raw+json");
        boolean mod;
        if (response.statusCode() == 200) {
            mod = definesModProperty(response.body());
        } else if (response.statusCode() == 404) { // no gradle.properties (or an empty repository)
            mod = false;
        } else {
            check(response);
            mod = false;
        }
        MOD_CACHE.put(key, mod);
        return mod;
    }

    /** Whether the gradle.properties text defines {@value #MOD_PROPERTY}. */
    static boolean definesModProperty(String text) {
        try {
            Properties props = new Properties();
            props.load(new StringReader(text));
            return props.containsKey(MOD_PROPERTY);
        } catch (IOException | IllegalArgumentException e) {
            return false;
        }
    }

    private static HttpResponse<String> get(String url, String token, String accept) throws IOException {
        HttpRequest request = HttpRequest.newBuilder(URI.create(url))
                .timeout(Duration.ofSeconds(30))
                .header("Accept", accept)
                .header("Authorization", "Bearer " + token.strip())
                .header("X-GitHub-Api-Version", "2022-11-28")
                .header("User-Agent", "mc-project-manager")
                .build();
        try {
            return HTTP.send(request, HttpResponse.BodyHandlers.ofString());
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new IOException("interrupted");
        }
    }

    private static void check(HttpResponse<?> response) throws IOException {
        int status = response.statusCode();
        if (status == 401) throw new IOException("GitHub rejected the token (401).");
        if (status == 403 || status == 429) throw new IOException("GitHub refused the request (" + status + ") – rate limit reached?");
        if (status != 200) throw new IOException("GitHub answered " + status);
    }

    static String next(String linkHeader) {
        if (linkHeader == null) return null;
        Matcher m = NEXT.matcher(linkHeader);
        return m.find() ? m.group(1) : null;
    }

    static List<Repo> parse(String json) throws IOException {
        try {
            List<Repo> repos = new ArrayList<>();
            JsonArray array = JsonParser.parseString(json).getAsJsonArray();
            for (JsonElement e : array) {
                JsonObject o = e.getAsJsonObject();
                JsonObject owner = o.has("owner") && o.get("owner").isJsonObject() ? o.getAsJsonObject("owner") : new JsonObject();
                repos.add(new Repo(string(o, "full_name"), string(owner, "login"), string(o, "name"), string(o, "description"),
                        string(o, "clone_url"), string(o, "html_url"), bool(o, "private"), bool(o, "fork"),
                        bool(o, "archived"), string(o, "pushed_at")));
            }
            return repos;
        } catch (RuntimeException e) {
            throw new IOException("Unexpected answer from GitHub: " + e.getMessage());
        }
    }

    private static String string(JsonObject o, String key) {
        JsonElement e = o.get(key);
        return e == null || e.isJsonNull() ? null : e.getAsString();
    }

    private static boolean bool(JsonObject o, String key) {
        JsonElement e = o.get(key);
        return e != null && !e.isJsonNull() && e.getAsBoolean();
    }
}
