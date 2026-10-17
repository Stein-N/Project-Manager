package net.morthen.core;

import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.Properties;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Mod metadata from fabric.mod.json, quilt.mod.json, neoforge.mods.toml or mods.toml.
 * Placeholders like {@code ${mod_id}} are resolved from gradle.properties.
 *
 * @param icon absolute path of the mod icon, or null
 * @param file metadata file relative to the project
 */
public record ModMetadata(String id, String name, String version, String description, List<String> authors,
                          Path icon, List<Dependency> dependencies, String file) {

    public record Dependency(String modId, String version, boolean required) {
    }

    private static final List<String> SUBPROJECTS = List.of("", "fabric", "neoforge", "forge", "quilt", "common");
    private static final Pattern PLACEHOLDER = Pattern.compile("\\$\\{([A-Za-z0-9_.]+)}");

    public static Optional<ModMetadata> read(Path project, Properties gradle) {
        for (String sub : SUBPROJECTS) {
            Path resources = (sub.isEmpty() ? project : project.resolve(sub)).resolve("src/main/resources");
            if (!Files.isDirectory(resources)) continue;
            try {
                Path fabric = resources.resolve("fabric.mod.json");
                if (Files.isRegularFile(fabric)) return Optional.of(fabric(project, resources, fabric, gradle));
                Path quilt = resources.resolve("quilt.mod.json");
                if (Files.isRegularFile(quilt)) return Optional.of(quilt(project, resources, quilt, gradle));
                for (String toml : List.of("META-INF/neoforge.mods.toml", "META-INF/mods.toml")) {
                    Path file = resources.resolve(toml);
                    if (Files.isRegularFile(file)) return Optional.of(toml(project, resources, file, gradle));
                }
            } catch (Exception e) {
                // broken or templated beyond recognition – try the next location
            }
        }
        return Optional.empty();
    }

    // ---------------------------------------------------------------- Fabric / Quilt

    private static ModMetadata fabric(Path project, Path resources, Path file, Properties gradle) throws IOException {
        JsonObject json = JsonParser.parseString(expand(Files.readString(file), gradle)).getAsJsonObject();
        List<String> authors = new ArrayList<>();
        if (json.has("authors") && json.get("authors").isJsonArray()) {
            for (JsonElement a : json.getAsJsonArray("authors")) {
                authors.add(a.isJsonObject() ? str(a.getAsJsonObject(), "name") : a.getAsString());
            }
        }
        List<Dependency> deps = new ArrayList<>();
        addFabricDeps(json, "depends", true, deps);
        addFabricDeps(json, "recommends", false, deps);
        return new ModMetadata(str(json, "id"), str(json, "name"), str(json, "version"), str(json, "description"),
                authors, icon(resources, iconPath(json.get("icon"))), deps, project.relativize(file).toString());
    }

    private static void addFabricDeps(JsonObject json, String key, boolean required, List<Dependency> out) {
        if (!json.has(key) || !json.get(key).isJsonObject()) return;
        for (Map.Entry<String, JsonElement> e : json.getAsJsonObject(key).entrySet()) {
            String version = e.getValue().isJsonArray()
                    ? String.join(" || ", e.getValue().getAsJsonArray().asList().stream().map(JsonElement::getAsString).toList())
                    : e.getValue().getAsString();
            out.add(new Dependency(e.getKey(), version, required));
        }
    }

    private static ModMetadata quilt(Path project, Path resources, Path file, Properties gradle) throws IOException {
        JsonObject root = JsonParser.parseString(expand(Files.readString(file), gradle)).getAsJsonObject();
        JsonObject loader = root.getAsJsonObject("quilt_loader");
        JsonObject meta = loader.has("metadata") ? loader.getAsJsonObject("metadata") : new JsonObject();
        List<Dependency> deps = new ArrayList<>();
        if (loader.has("depends")) {
            for (JsonElement d : loader.getAsJsonArray("depends")) {
                if (d.isJsonObject()) {
                    JsonObject o = d.getAsJsonObject();
                    boolean optional = o.has("optional") && o.get("optional").getAsBoolean();
                    deps.add(new Dependency(str(o, "id"), o.has("versions") ? o.get("versions").toString().replace("\"", "") : "*", !optional));
                } else {
                    deps.add(new Dependency(d.getAsString(), "*", true));
                }
            }
        }
        List<String> authors = new ArrayList<>();
        if (meta.has("contributors") && meta.get("contributors").isJsonObject()) {
            authors.addAll(meta.getAsJsonObject("contributors").keySet());
        }
        return new ModMetadata(str(loader, "id"), str(meta, "name"), str(loader, "version"), str(meta, "description"),
                authors, icon(resources, iconPath(meta.get("icon"))), deps, project.relativize(file).toString());
    }

    /** "icon" is either a path or an object {"16": "...", "128": "..."} – take the largest. */
    private static String iconPath(JsonElement icon) {
        if (icon == null || icon.isJsonNull()) return null;
        if (icon.isJsonPrimitive()) return icon.getAsString();
        if (icon.isJsonObject()) {
            String best = null;
            int bestSize = -1;
            for (Map.Entry<String, JsonElement> e : icon.getAsJsonObject().entrySet()) {
                int size = parseInt(e.getKey());
                if (size > bestSize) {
                    bestSize = size;
                    best = e.getValue().getAsString();
                }
            }
            return best;
        }
        return null;
    }

    // ---------------------------------------------------------------- (Neo)Forge TOML

    private static ModMetadata toml(Path project, Path resources, Path file, Properties gradle) throws IOException {
        List<Map<String, String>> mods = new ArrayList<>();
        Map<String, List<Map<String, String>>> deps = new LinkedHashMap<>();
        Map<String, String> current = new LinkedHashMap<>(); // top level
        List<String> lines = expand(Files.readString(file), gradle).lines().toList();
        for (int i = 0; i < lines.size(); i++) {
            String line = stripComment(lines.get(i)).trim();
            if (line.isEmpty()) continue;
            if (line.startsWith("[[") && line.endsWith("]]")) {
                String table = line.substring(2, line.length() - 2).trim();
                current = new LinkedHashMap<>();
                if (table.equals("mods")) {
                    mods.add(current);
                } else if (table.startsWith("dependencies.")) {
                    deps.computeIfAbsent(table.substring("dependencies.".length()).replace("\"", ""), k -> new ArrayList<>()).add(current);
                }
                continue;
            }
            if (line.startsWith("[")) {
                current = new LinkedHashMap<>(); // other tables are ignored
                continue;
            }
            int eq = line.indexOf('=');
            if (eq <= 0) continue;
            String key = line.substring(0, eq).trim();
            String value = line.substring(eq + 1).trim();
            if (value.startsWith("'''") || value.startsWith("\"\"\"")) {
                String quote = value.substring(0, 3);
                StringBuilder sb = new StringBuilder(value.substring(3));
                while (!sb.toString().contains(quote) && i + 1 < lines.size()) {
                    sb.append('\n').append(lines.get(++i));
                }
                String text = sb.toString();
                value = text.contains(quote) ? text.substring(0, text.indexOf(quote)) : text;
            } else {
                value = unquote(value);
            }
            current.put(key, value.strip());
        }
        Map<String, String> mod = mods.isEmpty() ? Map.of() : mods.getFirst();
        String id = mod.get("modId");
        List<Dependency> dependencies = new ArrayList<>();
        for (Map<String, String> d : deps.getOrDefault(id, List.of())) {
            boolean required = d.containsKey("type")
                    ? d.get("type").equalsIgnoreCase("required")
                    : Boolean.parseBoolean(d.getOrDefault("mandatory", "true"));
            dependencies.add(new Dependency(d.get("modId"), d.getOrDefault("versionRange", "*"), required));
        }
        List<String> authors = mod.containsKey("authors") ? List.of(mod.get("authors")) : List.of();
        return new ModMetadata(id, mod.get("displayName"), mod.get("version"), mod.get("description"),
                authors, icon(resources, mod.get("logoFile")), dependencies, project.relativize(file).toString());
    }

    private static String stripComment(String line) {
        boolean inString = false;
        char quote = 0;
        for (int i = 0; i < line.length(); i++) {
            char c = line.charAt(i);
            if (inString) {
                if (c == quote) inString = false;
            } else if (c == '"' || c == '\'') {
                inString = true;
                quote = c;
            } else if (c == '#') {
                return line.substring(0, i);
            }
        }
        return line;
    }

    private static String unquote(String value) {
        if (value.length() >= 2 && (value.startsWith("\"") && value.endsWith("\"") || value.startsWith("'") && value.endsWith("'"))) {
            return value.substring(1, value.length() - 1);
        }
        return value;
    }

    // ---------------------------------------------------------------- helpers

    /** Replaces ${key} with values from gradle.properties ("version" falls back to mod_version). */
    static String expand(String text, Properties gradle) {
        Matcher m = PLACEHOLDER.matcher(text);
        StringBuilder sb = new StringBuilder();
        while (m.find()) {
            String key = m.group(1);
            String value = gradle.getProperty(key);
            if (value == null && key.equals("version")) value = gradle.getProperty("mod_version");
            if (value == null && key.equals("file.jarVersion")) value = gradle.getProperty("mod_version");
            m.appendReplacement(sb, Matcher.quoteReplacement(value != null ? value : m.group()));
        }
        m.appendTail(sb);
        return sb.toString();
    }

    private static Path icon(Path resources, String relative) {
        if (relative == null || relative.isBlank() || relative.contains("${")) return null;
        Path icon = resources.resolve(relative.replace('\\', '/')).normalize();
        return Files.isRegularFile(icon) ? icon : null;
    }

    private static String str(JsonObject o, String key) {
        return o.has(key) && o.get(key).isJsonPrimitive() ? o.get(key).getAsString() : null;
    }

    private static int parseInt(String s) {
        try {
            return Integer.parseInt(s.trim());
        } catch (NumberFormatException e) {
            return 0;
        }
    }
}
