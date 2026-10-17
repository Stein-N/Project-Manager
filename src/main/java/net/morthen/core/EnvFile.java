package net.morthen.core;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Environment variables in {@code <master>/.env}, passed to the publish tasks (tokens for Modrinth, CurseForge …).
 * Supports {@code KEY=value}, {@code export KEY=value}, comments and single/double quoted values.
 */
public final class EnvFile {
    public static final String FILE_NAME = ".env";
    private static final Pattern NAME = Pattern.compile("[A-Za-z_][A-Za-z0-9_]*");
    private static final Pattern ENTRY = Pattern.compile("^\\s*(?:export\\s+)?([A-Za-z_][A-Za-z0-9_]*)\\s*=(.*)$");

    private EnvFile() {
    }

    public static Path path(Path master) {
        return master.resolve(FILE_NAME);
    }

    public static boolean validName(String name) {
        return name != null && NAME.matcher(name).matches();
    }

    /** Variables in file order; empty if the file does not exist. */
    public static Map<String, String> load(Path master) throws IOException {
        Path file = path(master);
        return Files.isRegularFile(file) ? parse(Files.readString(file)) : new LinkedHashMap<>();
    }

    static Map<String, String> parse(String text) {
        Map<String, String> vars = new LinkedHashMap<>();
        for (String line : text.lines().toList()) {
            Matcher m = ENTRY.matcher(line);
            if (m.matches()) vars.put(m.group(1), parseValue(m.group(2).strip()));
        }
        return vars;
    }

    private static String parseValue(String raw) {
        if (raw.startsWith("\"")) {
            StringBuilder sb = new StringBuilder();
            for (int i = 1; i < raw.length(); i++) {
                char c = raw.charAt(i);
                if (c == '"') break;
                if (c == '\\' && i + 1 < raw.length()) {
                    char next = raw.charAt(++i);
                    sb.append(switch (next) {
                        case 'n' -> '\n';
                        case 't' -> '\t';
                        default -> next; // \" \\ …
                    });
                } else {
                    sb.append(c);
                }
            }
            return sb.toString();
        }
        if (raw.startsWith("'")) {
            int end = raw.indexOf('\'', 1);
            return end > 0 ? raw.substring(1, end) : raw.substring(1);
        }
        int comment = raw.indexOf(" #");
        return (comment >= 0 ? raw.substring(0, comment) : raw).strip();
    }

    /**
     * Writes the variables. Existing lines (comments, order) are kept: changed keys are updated in place,
     * removed keys are dropped and new keys are appended.
     */
    public static void save(Path master, Map<String, String> vars) throws IOException {
        Path file = path(master);
        List<String> lines = Files.isRegularFile(file) ? Files.readAllLines(file) : new ArrayList<>();
        if (lines.isEmpty()) {
            lines.add("# Environment variables for the publish tasks of MC Mod Project Manager.");
            lines.add("# Keep this file private - it usually contains API tokens.");
        }
        Set<String> written = new LinkedHashSet<>();
        List<String> out = new ArrayList<>();
        for (String line : lines) {
            Matcher m = ENTRY.matcher(line);
            if (!m.matches()) {
                out.add(line);
                continue;
            }
            String key = m.group(1);
            if (vars.containsKey(key) && written.add(key)) out.add(key + "=" + format(vars.get(key)));
        }
        for (Map.Entry<String, String> e : vars.entrySet()) {
            if (written.add(e.getKey())) out.add(e.getKey() + "=" + format(e.getValue()));
        }
        Files.write(file, out);
    }

    /** Quotes values that would otherwise not survive a round trip. */
    static String format(String value) {
        if (value == null) return "";
        if (value.isEmpty() || value.matches("[A-Za-z0-9_./:@+\\-]+")) return value;
        return "\"" + value.replace("\\", "\\\\").replace("\"", "\\\"").replace("\n", "\\n").replace("\t", "\\t") + "\"";
    }
}
