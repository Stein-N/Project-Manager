package net.morthen.core;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import java.io.IOException;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Lists every repository the signed-in GitHub account can see – its own, those of its organizations and the ones it
 * collaborates on – and recognizes which of them are Minecraft mods.
 */
public final class GithubRepos {
    /** Variable with the personal access token (.env in the master folder, then the system environment). */
    public static final String VARIABLE = "GITHUB_TOKEN";

    /** @param owner user or organization */
    public record Repo(String fullName, String owner, String name, String description, String cloneUrl, String pageUrl,
                       boolean isPrivate, boolean fork, boolean archived, String pushedAt, boolean mod) {
    }

    private static final HttpClient HTTP = HttpClient.newBuilder()
            .connectTimeout(Duration.ofSeconds(10))
            .followRedirects(HttpClient.Redirect.NORMAL)
            .build();
    private static final Pattern NEXT = Pattern.compile("<([^>]+)>;\\s*rel=\"next\"");
    private static final int MAX_PAGES = 50;

    private static final Set<String> MOD_TOPICS = Set.of("minecraft", "minecraft-mod", "minecraft-mods", "minecraft-forge",
            "minecraft-fabric", "minecraft-neoforge", "fabric", "fabric-mod", "fabricmc", "fabric-api", "neoforge",
            "neoforged", "neoforge-mod", "forge", "forge-mod", "forge-mods", "minecraftforge", "quilt", "quilt-mod",
            "quiltmc", "modrinth", "curseforge");
    private static final Pattern MOD_TEXT = Pattern.compile("(?i)minecraft|fabric|neoforge|quilt(?:mc)?|\\bforge\\b");

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
            HttpRequest request = HttpRequest.newBuilder(URI.create(url))
                    .timeout(Duration.ofSeconds(30))
                    .header("Accept", "application/vnd.github+json")
                    .header("Authorization", "Bearer " + token.strip())
                    .header("X-GitHub-Api-Version", "2022-11-28")
                    .header("User-Agent", "mc-project-manager")
                    .build();
            try {
                HttpResponse<String> response = HTTP.send(request, HttpResponse.BodyHandlers.ofString());
                if (response.statusCode() == 401) throw new IOException("GitHub rejected the token (401).");
                if (response.statusCode() != 200) throw new IOException("GitHub answered " + response.statusCode());
                repos.addAll(parse(response.body()));
                url = next(response.headers().firstValue("Link").orElse(null));
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                throw new IOException("interrupted");
            }
        }
        return repos;
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
                String name = string(o, "name");
                String description = string(o, "description");
                JsonObject owner = o.has("owner") && o.get("owner").isJsonObject() ? o.getAsJsonObject("owner") : new JsonObject();
                List<String> topics = new ArrayList<>();
                if (o.get("topics") instanceof JsonArray t) t.forEach(x -> topics.add(x.getAsString()));
                repos.add(new Repo(string(o, "full_name"), string(owner, "login"), name, description,
                        string(o, "clone_url"), string(o, "html_url"), bool(o, "private"), bool(o, "fork"),
                        bool(o, "archived"), string(o, "pushed_at"), isMod(name, description, topics)));
            }
            return repos;
        } catch (RuntimeException e) {
            throw new IOException("Unexpected answer from GitHub: " + e.getMessage());
        }
    }

    /** A Minecraft mod if a topic says so or the name/description mentions Minecraft or a mod loader. */
    static boolean isMod(String name, String description, List<String> topics) {
        for (String topic : topics) {
            if (MOD_TOPICS.contains(topic.toLowerCase(Locale.ROOT))) return true;
        }
        return MOD_TEXT.matcher(name == null ? "" : name).find() || MOD_TEXT.matcher(description == null ? "" : description).find();
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
