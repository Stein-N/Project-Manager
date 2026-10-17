package net.morthen.core;

import com.google.gson.JsonElement;
import com.google.gson.JsonParser;

import java.io.IOException;
import java.io.Reader;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.Properties;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Dependency versions from gradle.properties (and the Gradle wrapper) plus lookup of the latest versions.
 */
public final class Dependencies {
    public enum Kind { FABRIC_LOADER, FABRIC_API, YARN, NEOFORGE, FORGE, PARCHMENT, GRADLE, OTHER }

    /**
     * @param key property key in gradle.properties, or null for the Gradle wrapper
     */
    public record Entry(String label, String key, String value, Kind kind) {
        /** Whether a "latest version" lookup is available for this entry. */
        public boolean checkable() {
            return kind != Kind.OTHER;
        }

        /** Whether the value can be updated in gradle.properties by the tool. */
        public boolean updatable() {
            return key != null && kind != Kind.OTHER && kind != Kind.GRADLE;
        }
    }

    private static final Set<String> SKIP = Set.of("mod_version", "version", "minecraft_version", "mc_version",
            "minecraftVersion", "parchment_minecraft_version", "parchment_minecraft", "java_version");
    private static final Pattern MAVEN_VERSION = Pattern.compile("<version>([^<]+)</version>");
    private static final Pattern MAVEN_RELEASE = Pattern.compile("<release>([^<]+)</release>");
    private static final Map<String, String> CACHE = new ConcurrentHashMap<>();
    private static final HttpClient HTTP = HttpClient.newBuilder()
            .connectTimeout(Duration.ofSeconds(10))
            .followRedirects(HttpClient.Redirect.NORMAL)
            .build();

    private Dependencies() {
    }

    public static List<Entry> read(Path project) {
        List<Entry> entries = new ArrayList<>();
        Properties props = new Properties();
        Path file = project.resolve("gradle.properties");
        if (Files.isRegularFile(file)) {
            try (Reader reader = Files.newBufferedReader(file)) {
                props.load(reader);
            } catch (IOException | IllegalArgumentException ignored) {
                // unreadable -> no entries
            }
        }
        for (String key : props.stringPropertyNames().stream().sorted().toList()) {
            String value = props.getProperty(key).trim();
            Kind kind = kind(key, value);
            boolean versionLike = key.endsWith("_version") || key.endsWith("Version") || key.equals("yarn_mappings");
            if (kind == null && (!versionLike || SKIP.contains(key))) continue;
            if (value.isEmpty() || value.contains("[") || value.contains(",") || value.contains("${")) continue; // ranges/templates
            entries.add(new Entry(label(key, kind), key, value, kind == null ? Kind.OTHER : kind));
        }
        wrapperVersion(project).ifPresent(v -> entries.add(new Entry("Gradle wrapper", null, v, Kind.GRADLE)));
        // known kinds first, then the rest
        entries.sort((a, b) -> Integer.compare(a.kind().ordinal(), b.kind().ordinal()));
        return entries;
    }

    private static Kind kind(String key, String value) {
        return switch (key) {
            case "loader_version", "fabric_loader_version" -> value.startsWith("0.") ? Kind.FABRIC_LOADER : null;
            case "fabric_version", "fabric_api_version" -> Kind.FABRIC_API;
            case "yarn_mappings" -> Kind.YARN;
            case "neo_version", "neoforge_version" -> Kind.NEOFORGE;
            case "forge_version" -> Kind.FORGE;
            case "parchment_mappings_version", "parchment_version" -> Kind.PARCHMENT;
            default -> null;
        };
    }

    private static String label(String key, Kind kind) {
        if (kind == null) return key;
        return switch (kind) {
            case FABRIC_LOADER -> "Fabric Loader";
            case FABRIC_API -> "Fabric API";
            case YARN -> "Yarn mappings";
            case NEOFORGE -> "NeoForge";
            case FORGE -> "Forge";
            case PARCHMENT -> "Parchment";
            default -> key;
        };
    }

    public static Optional<String> wrapperVersion(Path project) {
        Path file = project.resolve("gradle/wrapper/gradle-wrapper.properties");
        if (!Files.isRegularFile(file)) return Optional.empty();
        Properties props = new Properties();
        try (Reader reader = Files.newBufferedReader(file)) {
            props.load(reader);
        } catch (IOException | IllegalArgumentException e) {
            return Optional.empty();
        }
        Matcher m = Pattern.compile("gradle-([^-/]+(?:-rc-\\d+|-milestone-\\d+)?)-(bin|all)\\.zip")
                .matcher(props.getProperty("distributionUrl", ""));
        return m.find() ? Optional.of(m.group(1)) : Optional.empty();
    }

    // ---------------------------------------------------------------- latest versions

    /**
     * Looks up the latest version for {@code entry} (network, cached per session).
     *
     * @param mcVersion      Minecraft version of the project
     * @param parchmentMc    Minecraft version used for Parchment (may differ), or null
     */
    public static Optional<String> latest(Entry entry, String mcVersion, String parchmentMc) throws IOException {
        return switch (entry.kind()) {
            case FABRIC_LOADER -> firstJson("https://meta.fabricmc.net/v2/versions/loader", true);
            case YARN -> firstJson("https://meta.fabricmc.net/v2/versions/yarn/" + mcVersion, false);
            case FABRIC_API -> maxMaven("https://maven.fabricmc.net/net/fabricmc/fabric-api/fabric-api/maven-metadata.xml",
                    v -> v.endsWith("+" + mcVersion));
            case NEOFORGE -> {
                String prefix = neoForgePrefix(mcVersion);
                Optional<String> stable = maxMaven("https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml",
                        v -> v.startsWith(prefix) && !v.contains("beta"));
                yield stable.isPresent() ? stable
                        : maxMaven("https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml", v -> v.startsWith(prefix));
            }
            case FORGE -> {
                JsonElement promos = JsonParser.parseString(fetch("https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json"))
                        .getAsJsonObject().get("promos");
                JsonElement latest = promos.getAsJsonObject().get(mcVersion + "-latest");
                if (latest == null) yield Optional.empty();
                // keep the format of the current value ("47.3.0" or "1.20.1-47.3.0")
                yield Optional.of(entry.value().startsWith(mcVersion + "-") ? mcVersion + "-" + latest.getAsString() : latest.getAsString());
            }
            case PARCHMENT -> {
                String mc = parchmentMc != null ? parchmentMc : mcVersion;
                Matcher m = MAVEN_RELEASE.matcher(fetch("https://maven.parchmentmc.org/org/parchmentmc/data/parchment-" + mc + "/maven-metadata.xml"));
                yield m.find() ? Optional.of(m.group(1).trim()) : Optional.empty();
            }
            case GRADLE -> Optional.of(JsonParser.parseString(fetch("https://services.gradle.org/versions/current"))
                    .getAsJsonObject().get("version").getAsString());
            case OTHER -> Optional.empty();
        };
    }

    /** NeoForge versions encode the MC version: 1.21.1 → "21.1.", 1.21 → "21.0.", 26.1 → "26.1.". */
    static String neoForgePrefix(String mc) {
        String[] parts = mc.split("\\.");
        if (parts.length >= 2 && parts[0].equals("1")) {
            return parts[1] + "." + (parts.length > 2 ? parts[2] : "0") + ".";
        }
        return mc + ".";
    }

    /** Minecraft version Parchment mappings are published for (may lag behind the game version), or null. */
    public static String parchmentMinecraft(Path project) {
        Properties props = new Properties();
        try (Reader reader = Files.newBufferedReader(project.resolve("gradle.properties"))) {
            props.load(reader);
        } catch (IOException | IllegalArgumentException e) {
            return null;
        }
        String mc = props.getProperty("parchment_minecraft_version", props.getProperty("parchment_minecraft"));
        return mc == null || mc.isBlank() ? null : mc.trim();
    }

    public static boolean isNewer(String latest, String current) {
        return latest != null && !latest.equals(current) && VersionComparator.INSTANCE.compare(latest, current) > 0;
    }

    private static Optional<String> firstJson(String url, boolean stableOnly) throws IOException {
        for (JsonElement e : JsonParser.parseString(fetch(url)).getAsJsonArray()) {
            var o = e.getAsJsonObject();
            if (!stableOnly || !o.has("stable") || o.get("stable").getAsBoolean()) {
                return Optional.of(o.get("version").getAsString());
            }
        }
        return Optional.empty();
    }

    private static Optional<String> maxMaven(String url, java.util.function.Predicate<String> filter) throws IOException {
        Matcher m = MAVEN_VERSION.matcher(fetch(url));
        String best = null;
        while (m.find()) {
            String v = m.group(1).trim();
            if (filter.test(v) && (best == null || VersionComparator.INSTANCE.compare(v, best) > 0)) best = v;
        }
        return Optional.ofNullable(best);
    }

    private static String fetch(String url) throws IOException {
        String cached = CACHE.get(url);
        if (cached != null) return cached;
        HttpRequest request = HttpRequest.newBuilder(URI.create(url))
                .timeout(Duration.ofSeconds(15))
                .header("User-Agent", "MC-Mod-Project-Manager")
                .build();
        try {
            HttpResponse<String> response = HTTP.send(request, HttpResponse.BodyHandlers.ofString());
            if (response.statusCode() == 404) throw new IOException("not found (" + url + ")");
            if (response.statusCode() != 200) throw new IOException("HTTP " + response.statusCode() + " (" + url + ")");
            CACHE.put(url, response.body());
            return response.body();
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new IOException("interrupted");
        }
    }

    // ---------------------------------------------------------------- update

    /** Replaces the value of {@code key} in gradle.properties, keeping formatting, comments and line endings. */
    public static void update(Path project, String key, String newValue) throws IOException {
        Path file = project.resolve("gradle.properties");
        String text = Files.readString(file);
        Pattern line = Pattern.compile("(?m)^(\\s*" + Pattern.quote(key) + "\\s*[=:]\\s*)(.*?)(\\s*)$");
        Matcher m = line.matcher(text);
        if (!m.find()) throw new IOException(key + " not found in gradle.properties");
        String updated = text.substring(0, m.start(2)) + newValue + text.substring(m.end(2));
        Files.writeString(file, updated);
    }
}
