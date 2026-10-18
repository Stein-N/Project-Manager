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
        return codeSource().filter(jar -> Files.isRegularFile(jar) && jar.getFileName().toString().endsWith(".jar"));
    }

    /** Where this code was loaded from, whatever the file is called (the pending update is {@code *.jar.new}). */
    private static Optional<Path> codeSource() {
        try {
            return Optional.of(Path.of(UpdateChecker.class.getProtectionDomain().getCodeSource().getLocation().toURI()));
        } catch (Exception e) {
            return Optional.empty();
        }
    }

    /** Argument of the new jar: replace {@code <target>} with myself, then start it. */
    public static final String APPLY_FLAG = "--apply-update";
    private static final String PENDING_SUFFIX = ".new";

    /** Downloads the release jar next to the running one as {@code <name>.new} (the running file is locked on Windows). */
    public static Path download(Release release, Path runningJar) throws IOException {
        if (release.downloadUrl() == null) throw new IOException("The release has no jar file.");
        Path pending = pendingFile(runningJar);
        Path part = pending.resolveSibling(pending.getFileName() + ".part");
        HttpRequest request = HttpRequest.newBuilder(URI.create(release.downloadUrl()))
                .timeout(Duration.ofMinutes(5)).header("User-Agent", "mc-project-manager").build();
        try {
            HttpResponse<InputStream> response = HTTP.send(request, HttpResponse.BodyHandlers.ofInputStream());
            if (response.statusCode() != 200) throw new IOException("Download failed: HTTP " + response.statusCode());
            try (InputStream in = response.body()) {
                Files.copy(in, part, StandardCopyOption.REPLACE_EXISTING);
            }
            Files.move(part, pending, StandardCopyOption.REPLACE_EXISTING);
            return pending;
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new IOException("interrupted");
        } finally {
            Files.deleteIfExists(part);
        }
    }

    static Path pendingFile(Path jar) {
        return jar.toAbsolutePath().resolveSibling(jar.getFileName() + PENDING_SUFFIX);
    }

    /**
     * Starts the downloaded jar in "apply" mode and returns; the caller then exits. The new jar waits until the old
     * one is free, copies itself over it (same path, so autostart and shortcuts stay valid) and starts it.
     */
    public static void startAndReplace(Path pending, Path oldJar) throws IOException {
        new ProcessBuilder(javaExecutable(), "-jar", pending.toString(), APPLY_FLAG, oldJar.toAbsolutePath().toString())
                .start();
    }

    private static String javaExecutable() {
        return Path.of(System.getProperty("java.home"), "bin", SystemActions.isWindows() ? "javaw.exe" : "java").toString();
    }

    /** Runs in the new jar ({@link #APPLY_FLAG}): replaces {@code target} with this jar and starts it. */
    public static void applyPending(Path target) throws IOException {
        Path self = codeSource().filter(Files::isRegularFile).orElseThrow(() -> new IOException("Not running from a jar."));
        IOException last = null;
        for (int i = 0; i < 40; i++) { // the old process needs a moment to end and release the file
            try {
                Files.copy(self, target, StandardCopyOption.REPLACE_EXISTING);
                last = null;
                break;
            } catch (IOException e) {
                last = e;
                try {
                    Thread.sleep(500);
                } catch (InterruptedException ie) {
                    Thread.currentThread().interrupt();
                    break;
                }
            }
        }
        if (last != null) throw last;
        new ProcessBuilder(javaExecutable(), "-jar", target.toString()).start();
    }

    /**
     * Removes a leftover {@code <name>.new} from an update. The process that applied it may still hold the file for a
     * moment, so this retries in the background.
     */
    public static void cleanUpPending() {
        runningJar().ifPresent(jar -> {
            Path pending = pendingFile(jar);
            if (!Files.exists(pending)) return;
            Thread t = new Thread(() -> {
                for (int i = 0; i < 20; i++) {
                    try {
                        Files.deleteIfExists(pending);
                        return;
                    } catch (IOException e) {
                        try {
                            Thread.sleep(1000);
                        } catch (InterruptedException ie) {
                            return;
                        }
                    }
                }
            }, "update-cleanup");
            t.setDaemon(true);
            t.start();
        });
    }
}
