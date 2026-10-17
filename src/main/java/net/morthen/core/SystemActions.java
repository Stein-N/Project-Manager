package net.morthen.core;

import java.awt.Desktop;
import java.io.File;
import java.io.IOException;
import java.net.URI;
import java.nio.file.AtomicMoveNotSupportedException;
import java.nio.file.DirectoryNotEmptyException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Comparator;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Optional;
import java.util.stream.Stream;

/**
 * Launch IntelliJ, open the file manager / a terminal / links, file operations – for Windows, Linux and macOS.
 */
public final class SystemActions {
    public enum Os { WINDOWS, LINUX, MAC }

    private SystemActions() {
    }

    public static Os os() {
        String name = System.getProperty("os.name", "").toLowerCase(Locale.ROOT);
        if (name.contains("win")) return Os.WINDOWS;
        if (name.contains("mac")) return Os.MAC;
        return Os.LINUX;
    }

    public static boolean isWindows() {
        return os() == Os.WINDOWS;
    }

    // ---------------------------------------------------------------- IntelliJ

    /** Looks for an IntelliJ installation of the current platform. */
    public static Optional<Path> detectIntelliJ() {
        return intellijCandidates(os(), System.getenv(), Path.of(System.getProperty("user.home")), pathDirectories())
                .stream().filter(Files::isRegularFile).findFirst();
    }

    /** Possible IntelliJ launchers in order of preference (Ultimate before Community, newest first). */
    static List<Path> intellijCandidates(Os os, Map<String, String> env, Path home, List<Path> pathDirs) {
        List<Path> out = new ArrayList<>();
        switch (os) {
            case WINDOWS -> {
                String localAppData = env.get("LOCALAPPDATA");
                String programFiles = env.get("ProgramFiles");
                if (localAppData != null) addInstallations(out, Path.of(localAppData, "Programs"), "IntelliJ IDEA", "bin/idea64.exe");
                if (programFiles != null) addInstallations(out, Path.of(programFiles, "JetBrains"), "IntelliJ IDEA", "bin/idea64.exe");
                if (localAppData != null) out.add(Path.of(localAppData, "JetBrains", "Toolbox", "scripts", "idea.cmd"));
            }
            case LINUX -> {
                // JetBrains Toolbox: shell scripts, then the app folders themselves
                Path toolbox = home.resolve(".local/share/JetBrains/Toolbox");
                for (String script : List.of("idea", "idea-ultimate", "intellij-idea-ultimate", "idea-community", "intellij-idea-community", "idea-ce")) {
                    out.add(toolbox.resolve("scripts").resolve(script));
                }
                addToolboxApps(out, toolbox.resolve("apps"));
                // tarball installs
                addInstallations(out, Path.of("/opt"), "idea", "bin/idea.sh");
                addInstallations(out, Path.of("/opt"), "intellij-idea", "bin/idea.sh");
                addInstallations(out, Path.of("/usr/local"), "idea", "bin/idea.sh");
                addInstallations(out, home.resolve(".local/opt"), "idea", "bin/idea.sh");
                // Snap and Flatpak
                out.add(Path.of("/snap/bin/intellij-idea-ultimate"));
                out.add(Path.of("/snap/bin/intellij-idea-community"));
                for (Path exports : List.of(Path.of("/var/lib/flatpak/exports/bin"), home.resolve(".local/share/flatpak/exports/bin"))) {
                    out.add(exports.resolve("com.jetbrains.IntelliJ-IDEA-Ultimate"));
                    out.add(exports.resolve("com.jetbrains.IntelliJ-IDEA-Community"));
                }
                // launchers on the PATH (e.g. created via "Tools > Create Command-line Launcher")
                for (Path dir : pathDirs) {
                    out.add(dir.resolve("idea"));
                    out.add(dir.resolve("idea.sh"));
                }
            }
            case MAC -> {
                for (Path apps : List.of(Path.of("/Applications"), home.resolve("Applications"))) {
                    out.add(apps.resolve("IntelliJ IDEA.app/Contents/MacOS/idea"));
                    out.add(apps.resolve("IntelliJ IDEA Ultimate.app/Contents/MacOS/idea"));
                    out.add(apps.resolve("IntelliJ IDEA CE.app/Contents/MacOS/idea"));
                }
                out.add(home.resolve("Library/Application Support/JetBrains/Toolbox/scripts/idea"));
                for (Path dir : pathDirs) out.add(dir.resolve("idea"));
            }
        }
        return out;
    }

    /** {@code parent/<name starting with prefix>/<relative>}, Ultimate before Community, newest first. */
    private static void addInstallations(List<Path> out, Path parent, String prefix, String relative) {
        for (Path dir : children(parent, prefix)) {
            out.add(relative.isEmpty() ? dir : dir.resolve(relative));
        }
    }

    /** Toolbox app folders: apps/intellij-idea-ultimate/bin/idea.sh (new) or apps/IDEA-U/ch-0/<build>/bin/idea.sh (old). */
    private static void addToolboxApps(List<Path> out, Path apps) {
        for (Path app : children(apps, "")) {
            String name = app.getFileName().toString().toLowerCase(Locale.ROOT);
            if (!name.contains("idea")) continue;
            out.add(app.resolve("bin/idea.sh"));
            for (Path channel : children(app, "ch-")) {
                for (Path build : children(channel, "")) out.add(build.resolve("bin/idea.sh"));
            }
        }
    }

    private static List<Path> children(Path parent, String prefix) {
        if (!Files.isDirectory(parent)) return List.of();
        try (Stream<Path> stream = Files.list(parent)) {
            return stream.filter(Files::isDirectory)
                    .filter(p -> p.getFileName().toString().toLowerCase(Locale.ROOT).startsWith(prefix.toLowerCase(Locale.ROOT)))
                    .sorted(Comparator.comparing((Path p) -> {
                                String n = p.getFileName().toString().toLowerCase(Locale.ROOT);
                                return n.contains("community") || n.contains("-ce") || n.endsWith("-c");
                            })
                            .thenComparing(Comparator.reverseOrder()))
                    .toList();
        } catch (IOException e) {
            return List.of();
        }
    }

    public static void openInIntelliJ(Path ide, Path project) throws IOException {
        String file = ide.getFileName().toString().toLowerCase(Locale.ROOT);
        List<String> command = new ArrayList<>();
        if (file.endsWith(".cmd") || file.endsWith(".bat")) {
            command.addAll(List.of("cmd.exe", "/c", ide.toString()));
        } else if (file.endsWith(".app")) {
            command.addAll(List.of("open", "-na", ide.toString(), "--args"));
        } else if (!isWindows() && !Files.isExecutable(ide)) {
            command.addAll(List.of("sh", ide.toString())); // script without the executable bit
        } else {
            command.add(ide.toString());
        }
        command.add(project.toString());
        new ProcessBuilder(command).redirectErrorStream(true).redirectOutput(ProcessBuilder.Redirect.DISCARD).start();
    }

    // ---------------------------------------------------------------- file manager / terminal / links

    public static void openExplorer(Path dir) throws IOException {
        switch (os()) {
            case WINDOWS -> new ProcessBuilder("explorer.exe", dir.toString()).start();
            case MAC -> new ProcessBuilder("open", dir.toString()).start();
            case LINUX -> openWithDesktop(dir.toUri(), () -> Desktop.getDesktop().open(dir.toFile()));
        }
    }

    /** Opens a file with its default application. */
    public static void openFile(Path file) throws IOException {
        if (os() == Os.WINDOWS) {
            Desktop.getDesktop().open(file.toFile());
        } else {
            openWithDesktop(file.toUri(), () -> Desktop.getDesktop().open(file.toFile()));
        }
    }

    /** Opens a link in the browser, or a file: URI with its default application. */
    public static void openUri(URI uri) throws IOException {
        if ("file".equals(uri.getScheme())) {
            openFile(Path.of(uri));
        } else if (os() == Os.WINDOWS) {
            Desktop.getDesktop().browse(uri);
        } else {
            openWithDesktop(uri, () -> Desktop.getDesktop().browse(uri));
        }
    }

    private interface DesktopAction {
        void run() throws IOException;
    }

    /** java.awt.Desktop does not work on every Linux desktop – fall back to xdg-open / open. */
    private static void openWithDesktop(URI uri, DesktopAction action) throws IOException {
        try {
            if (Desktop.isDesktopSupported()) {
                action.run();
                return;
            }
        } catch (IOException | UnsupportedOperationException e) {
            // fall through
        }
        new ProcessBuilder(os() == Os.MAC ? "open" : "xdg-open", uri.toString()).start();
    }

    public static void openTerminal(Path dir) throws IOException {
        List<String> command = terminalCommand(os(), System.getenv(), dir, pathDirectories())
                .orElseThrow(() -> new IOException("No terminal emulator found. Set the TERMINAL environment variable."));
        new ProcessBuilder(command).directory(dir.toFile()).start();
    }

    /** Linux terminals in order of preference: $TERMINAL, the Debian alternative, then common emulators. */
    private static final List<List<String>> LINUX_TERMINALS = List.of(
            List.of("x-terminal-emulator"),
            List.of("gnome-terminal", "--working-directory=%d"),
            List.of("ptyxis", "--new-window", "--working-directory=%d"),
            List.of("kgx", "--working-directory=%d"),
            List.of("konsole", "--workdir", "%d"),
            List.of("xfce4-terminal", "--working-directory=%d"),
            List.of("mate-terminal", "--working-directory=%d"),
            List.of("tilix", "--working-directory=%d"),
            List.of("kitty", "--directory", "%d"),
            List.of("alacritty", "--working-directory", "%d"),
            List.of("wezterm", "start", "--cwd", "%d"),
            List.of("foot", "--working-directory=%d"),
            List.of("xterm"));

    /** Command that opens a terminal in {@code dir} (the process is also started with {@code dir} as working directory). */
    static Optional<List<String>> terminalCommand(Os os, Map<String, String> env, Path dir, List<Path> pathDirs) {
        return switch (os) {
            case WINDOWS -> Optional.of(findOnPath("wt.exe", pathDirs).isPresent() || findOnPath("wt", pathDirs).isPresent()
                    ? List.of("wt.exe", "-d", dir.toString())
                    : List.of("cmd.exe", "/c", "start", "cmd.exe"));
            case MAC -> Optional.of(List.of("open", "-a", "Terminal", dir.toString()));
            case LINUX -> {
                String preferred = env.get("TERMINAL");
                if (preferred != null && !preferred.isBlank() && findOnPath(preferred.trim(), pathDirs).isPresent()) {
                    yield Optional.of(List.of(preferred.trim()));
                }
                for (List<String> candidate : LINUX_TERMINALS) {
                    if (findOnPath(candidate.getFirst(), pathDirs).isPresent()) {
                        yield Optional.of(candidate.stream().map(arg -> arg.replace("%d", dir.toString())).toList());
                    }
                }
                yield Optional.empty();
            }
        };
    }

    static Optional<Path> findOnPath(String program, List<Path> pathDirs) {
        if (program.contains("/") || program.contains("\\")) {
            Path p = Path.of(program);
            return Files.isRegularFile(p) ? Optional.of(p) : Optional.empty();
        }
        return pathDirs.stream().map(d -> d.resolve(program)).filter(Files::isRegularFile).findFirst();
    }

    static List<Path> pathDirectories() {
        String path = System.getenv("PATH");
        if (path == null) return List.of();
        return Arrays.stream(path.split(File.pathSeparator))
                .filter(s -> !s.isBlank())
                .map(s -> {
                    try {
                        return Path.of(s);
                    } catch (Exception e) {
                        return null;
                    }
                })
                .filter(java.util.Objects::nonNull)
                .toList();
    }

    // ---------------------------------------------------------------- Files

    /** Deletes recursively; clears the read-only attribute first (git objects are read-only on Windows). */
    public static void deleteRecursively(Path root) throws IOException {
        if (!Files.exists(root)) {
            return;
        }
        List<Path> paths;
        try (Stream<Path> walk = Files.walk(root)) {
            paths = walk.sorted(Comparator.reverseOrder()).toList();
        }
        for (Path path : paths) {
            path.toFile().setWritable(true);
            Files.delete(path);
        }
    }

    public static void move(Path source, Path target) throws IOException {
        try {
            Files.move(source, target);
        } catch (AtomicMoveNotSupportedException | DirectoryNotEmptyException e) {
            copyRecursively(source, target);
            deleteRecursively(source);
        } catch (IOException e) {
            // e.g. a different drive
            if (Files.exists(target)) {
                throw e;
            }
            copyRecursively(source, target);
            deleteRecursively(source);
        }
    }

    public static void copyRecursively(Path source, Path target) throws IOException {
        try (Stream<Path> walk = Files.walk(source)) {
            for (Path path : (Iterable<Path>) walk::iterator) {
                Path dest = target.resolve(source.relativize(path).toString());
                if (Files.isDirectory(path)) {
                    Files.createDirectories(dest);
                } else {
                    Files.copy(path, dest, StandardCopyOption.COPY_ATTRIBUTES);
                }
            }
        }
    }

    public static boolean isEmptyDirectory(Path dir) throws IOException {
        try (Stream<Path> stream = Files.list(dir)) {
            return stream.findAny().isEmpty();
        }
    }
}
