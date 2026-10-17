package net.morthen.core;

import com.google.gson.Gson;
import com.google.gson.reflect.TypeToken;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Optional;
import java.util.Set;
import java.util.function.Consumer;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Detects the run tasks (runClient, runServer, testmod and gametest runs, data generation …) of a Gradle project
 * and groups them by loader subproject. Results are cached on disk until a build file changes.
 */
public final class GradleTasks {
    /**
     * @param path task path, always absolute (":runClient", ":fabric:runClient") so only that project's task runs
     */
    public record RunTask(String path, String description) {
        /** Subproject name ("" for the root project). */
        public String project() {
            int last = path.lastIndexOf(':');
            return last <= 0 ? "" : path.substring(1, last);
        }

        public String name() {
            return path.substring(path.lastIndexOf(':') + 1);
        }

        /** "runTestmodClient" → "Testmod client". */
        public String label() {
            String rest = name().startsWith("run") ? name().substring(3) : name();
            String words = rest.replaceAll("([a-z0-9])([A-Z])", "$1 $2").replaceAll("([A-Z]+)([A-Z][a-z])", "$1 $2");
            if (words.isBlank()) return name();
            String lower = words.toLowerCase(Locale.ROOT);
            return Character.toUpperCase(lower.charAt(0)) + lower.substring(1);
        }

        /** Sort order inside a group: client, server, testmod, gametest, data, rest. */
        int rank() {
            String n = name().toLowerCase(Locale.ROOT);
            if (n.contains("testmod")) return n.contains("server") ? 3 : 2;
            if (n.contains("gametest")) return 4;
            if (n.contains("data")) return 5;
            if (n.equals("runclient")) return 0;
            if (n.equals("runserver")) return 1;
            if (n.contains("client")) return 0;
            if (n.contains("server")) return 1;
            return 6;
        }
    }

    public record Group(String name, List<RunTask> tasks) {
    }

    private record CacheEntry(String signature, List<RunTask> tasks, long detected) {
    }

    private static final Pattern TASK_LINE = Pattern.compile("^([A-Za-z0-9_.\\-]+(?::[A-Za-z0-9_.\\-]+)*)(?:\\s+-\\s+(.*))?$");
    /** IDE helper tasks that start with "run" but are not run configurations. */
    private static final Pattern IDE_SYNC = Pattern.compile("run(Ide|Idea|Eclipse|VsCode)\\w*");
    private static final List<String> LOADER_ORDER = List.of("common", "fabric", "quilt", "forge", "neoforge");
    private static final Path CACHE_FILE = Path.of(System.getProperty("user.home"), ".mc-project-manager", "gradle-tasks.json");
    private static final Gson GSON = new Gson();
    private static Map<String, CacheEntry> cache;

    private GradleTasks() {
    }

    // ---------------------------------------------------------------- detection

    /** Runs "gradlew tasks --all" and stores the run tasks in the cache. */
    public static List<RunTask> detect(GradleRunner runner, Path project, Consumer<String> log) throws IOException {
        StringBuilder out = new StringBuilder();
        int exit = runner.run(project, "tasks --all", line -> {
            out.append(line).append('\n');
            log.accept(line);
        });
        if (exit != 0) throw new IOException("gradlew tasks failed (exit code " + exit + ") – see the log");
        List<RunTask> tasks = parse(out.toString());
        store(project, tasks);
        return tasks;
    }

    /** Extracts the run tasks from the output of "gradlew tasks --all". */
    static List<RunTask> parse(String output) {
        Set<String> seen = new LinkedHashSet<>();
        List<RunTask> tasks = new ArrayList<>();
        boolean inTaskList = false;
        String previous = "";
        for (String raw : output.lines().toList()) {
            String line = raw.strip();
            if (line.matches("-{3,}")) { // underline of a group header ("Application tasks")
                inTaskList = previous.endsWith("tasks");
                previous = line;
                continue;
            }
            previous = line;
            if (!inTaskList || line.isEmpty()) continue;
            Matcher m = TASK_LINE.matcher(line);
            if (!m.matches()) continue;
            String path = m.group(1);
            String name = path.substring(path.lastIndexOf(':') + 1);
            if (!isRunTask(name)) continue;
            String absolute = ":" + path;
            if (seen.add(absolute)) tasks.add(new RunTask(absolute, m.group(2) == null ? "" : m.group(2).strip()));
        }
        return tasks;
    }

    private static boolean isRunTask(String name) {
        return name.length() > 3 && name.startsWith("run") && Character.isUpperCase(name.charAt(3))
                && !IDE_SYNC.matcher(name).matches();
    }

    // ---------------------------------------------------------------- grouping

    /**
     * Groups by subproject: root ("General", or the loader name for single-loader projects), Common, Fabric,
     * Forge, NeoForge, then any other subprojects.
     */
    public static List<Group> group(List<RunTask> tasks, String rootLabel) {
        Map<String, List<RunTask>> byProject = new LinkedHashMap<>();
        for (RunTask t : tasks) byProject.computeIfAbsent(t.project(), k -> new ArrayList<>()).add(t);
        List<String> order = new ArrayList<>(byProject.keySet());
        order.sort(Comparator.comparingInt((String p) -> p.isEmpty() ? -1
                : LOADER_ORDER.contains(p.toLowerCase(Locale.ROOT)) ? LOADER_ORDER.indexOf(p.toLowerCase(Locale.ROOT))
                : LOADER_ORDER.size()).thenComparing(p -> p));
        List<Group> groups = new ArrayList<>();
        for (String p : order) {
            List<RunTask> list = new ArrayList<>(byProject.get(p));
            list.sort(Comparator.comparingInt(RunTask::rank).thenComparing(RunTask::name));
            groups.add(new Group(p.isEmpty() ? rootLabel : displayName(p), list));
        }
        return groups;
    }

    static String displayName(String project) {
        String last = project.substring(project.lastIndexOf(':') + 1);
        return switch (last.toLowerCase(Locale.ROOT)) {
            case "common" -> "Common";
            case "fabric" -> "Fabric";
            case "quilt" -> "Quilt";
            case "forge" -> "Forge";
            case "neoforge" -> "NeoForge";
            default -> Character.toUpperCase(last.charAt(0)) + last.substring(1);
        };
    }

    // ---------------------------------------------------------------- cache

    public record Cached(List<RunTask> tasks, boolean stale, long detected) {
    }

    /** Cached tasks of the project; {@code stale} when a build file changed since the detection. */
    public static synchronized Optional<Cached> cached(Path project) {
        CacheEntry entry = load().get(key(project));
        if (entry == null) return Optional.empty();
        return Optional.of(new Cached(entry.tasks(), !entry.signature().equals(signature(project)), entry.detected()));
    }

    private static synchronized void store(Path project, List<RunTask> tasks) {
        load().put(key(project), new CacheEntry(signature(project), tasks, System.currentTimeMillis()));
        try {
            Files.createDirectories(CACHE_FILE.getParent());
            Files.writeString(CACHE_FILE, GSON.toJson(cache));
        } catch (IOException ignored) {
            // cache is optional
        }
    }

    private static Map<String, CacheEntry> load() {
        if (cache == null) {
            cache = new HashMap<>();
            if (Files.isRegularFile(CACHE_FILE)) {
                try {
                    Map<String, CacheEntry> stored = GSON.fromJson(Files.readString(CACHE_FILE),
                            new TypeToken<Map<String, CacheEntry>>() { }.getType());
                    if (stored != null) cache.putAll(stored);
                } catch (Exception ignored) {
                    // corrupt cache → start fresh
                }
            }
        }
        return cache;
    }

    private static String key(Path project) {
        return project.toAbsolutePath().normalize().toString().toLowerCase(Locale.ROOT);
    }

    /** Modification times of all build scripts – changes when tasks could have changed. */
    static String signature(Path project) {
        StringBuilder sb = new StringBuilder();
        List<Path> dirs = new ArrayList<>(List.of(project));
        for (String sub : List.of("common", "fabric", "quilt", "forge", "neoforge")) dirs.add(project.resolve(sub));
        for (Path dir : dirs) {
            for (String file : List.of("build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts", "gradle.properties")) {
                Path f = dir.resolve(file);
                try {
                    if (Files.isRegularFile(f)) sb.append(f.getFileName()).append(Files.getLastModifiedTime(f).toMillis()).append(';');
                } catch (IOException ignored) {
                    // skip
                }
            }
        }
        return sb.toString();
    }
}
