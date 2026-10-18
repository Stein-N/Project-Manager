package net.morthen.core;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import java.io.IOException;
import java.io.InputStream;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.time.Duration;
import java.util.List;
import java.util.Optional;

/**
 * Looks for a newer GitHub release of the tool, downloads its jar next to the running one and restarts with it.
 */
public final class UpdateChecker {
    public static final String REPO = "Stein-N/Project-Manager";

    /** @param downloadUrl the release's jar (null if it has none) */
    public record Release(String version, String name, String pageUrl, String downloadUrl, String fileName, String notes) {
    }

    private static final HttpClient HTTP = HttpClient.newBuilder()
            .connectTimeout(Duration.ofSeconds(10))
            .followRedirects(HttpClient.Redirect.NORMAL)
            .build();

    private UpdateChecker() {
    }

    /** Version from the jar manifest; null when started from the IDE. */
    public static String currentVersion() {
        return UpdateChecker.class.getPackage().getImplementationVersion();
    }

    /** The latest release if it is newer than {@code current}; empty if up to date. */
    public static Optional<Release> check(String current) throws IOException {
        Release latest = latest();
        return isNewer(latest.version(), current) ? Optional.of(latest) : Optional.empty();
    }

    /** The latest (non-draft, non-prerelease) release. */
    public static Release latest() throws IOException {
        HttpRequest request = HttpRequest.newBuilder(URI.create("https://api.github.com/repos/" + REPO + "/releases/latest"))
                .timeout(Duration.ofSeconds(15))
                .header("Accept", "application/vnd.github+json")
                .header("User-Agent", "mc-project-manager")
                .build();
        try {
            HttpResponse<String> response = HTTP.send(request, HttpResponse.BodyHandlers.ofString());
            if (response.statusCode() != 200) throw new IOException("GitHub answered " + response.statusCode());
            return parse(response.body());
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new IOException("interrupted");
        }
    }

    static Release parse(String json) throws IOException {
        try {
            JsonObject o = JsonParser.parseString(json).getAsJsonObject();
            String tag = string(o, "tag_name");
            if (tag == null) throw new IOException("Release without a tag");
            String url = null;
            String file = null;
            JsonElement assets = o.get("assets");
            if (assets != null && assets.isJsonArray()) {
                JsonArray array = assets.getAsJsonArray();
                for (JsonElement e : array) {
                    String n = string(e.getAsJsonObject(), "name");
                    if (n != null && n.endsWith(".jar")) {
                        file = n;
                        url = string(e.getAsJsonObject(), "browser_download_url");
                        break;
                    }
                }
            }
            String name = string(o, "name");
            return new Release(tag.replaceFirst("^[vV]", ""), name == null ? tag : name, string(o, "html_url"), url, file,
                    string(o, "body"));
        } catch (RuntimeException e) {
            throw new IOException("Unexpected answer from GitHub: " + e.getMessage());
        }
    }

    private static String string(JsonObject o, String key) {
        JsonElement e = o.get(key);
        return e == null || e.isJsonNull() ? null : e.getAsString();
    }

    public static boolean isNewer(String candidate, String current) {
        if (candidate == null || current == null) return false;
        return VersionComparator.INSTANCE.compare(candidate, current) > 0;
    }

    /** The running jar, empty when not started from a jar file (IDE). */
    public static Optional<Path> runningJar() {
        try {
            Path jar = Path.of(UpdateChecker.class.getProtectionDomain().getCodeSource().getLocation().toURI());
            return Files.isRegularFile(jar) && jar.getFileName().toString().endsWith(".jar") ? Optional.of(jar) : Optional.empty();
        } catch (Exception e) {
            return Optional.empty();
        }
    }

    /** Downloads the release jar into the folder of the running jar. */
    public static Path download(Release release, Path runningJar) throws IOException {
        if (release.downloadUrl() == null) throw new IOException("The release has no jar file.");
        Path target = runningJar.toAbsolutePath().resolveSibling(release.fileName());
        Path part = target.resolveSibling(release.fileName() + ".part");
        HttpRequest request = HttpRequest.newBuilder(URI.create(release.downloadUrl()))
                .timeout(Duration.ofMinutes(5)).header("User-Agent", "mc-project-manager").build();
        try {
            HttpResponse<InputStream> response = HTTP.send(request, HttpResponse.BodyHandlers.ofInputStream());
            if (response.statusCode() != 200) throw new IOException("Download failed: HTTP " + response.statusCode());
            try (InputStream in = response.body()) {
                Files.copy(in, part, StandardCopyOption.REPLACE_EXISTING);
            }
            Files.move(part, target, StandardCopyOption.REPLACE_EXISTING);
            return target;
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new IOException("interrupted");
        } finally {
            Files.deleteIfExists(part);
        }
    }

    /** Starts the new jar and removes the old one once this process has ended. */
    public static void startAndReplace(Path newJar, Path oldJar) throws IOException {
        Path java = Path.of(System.getProperty("java.home"), "bin", SystemActions.isWindows() ? "javaw.exe" : "java");
        new ProcessBuilder(java.toString(), "-jar", newJar.toString()).start();
        if (newJar.equals(oldJar.toAbsolutePath())) return;
        List<String> cleanup = SystemActions.isWindows()
                ? List.of("cmd", "/c", "ping -n 6 127.0.0.1 >nul & del /f /q \"" + oldJar + "\"")
                : List.of("sh", "-c", "sleep 5; rm -f '" + oldJar + "'");
        try {
            new ProcessBuilder(cleanup).start();
        } catch (IOException ignored) {
            // the old jar stays, harmless
        }
    }
}
