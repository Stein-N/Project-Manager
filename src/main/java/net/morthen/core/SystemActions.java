package net.morthen.core;

import java.awt.Desktop;
import java.io.IOException;
import java.nio.file.AtomicMoveNotSupportedException;
import java.nio.file.DirectoryNotEmptyException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.Locale;
import java.util.Optional;
import java.util.stream.Stream;

/**
 * Launch IntelliJ, open Explorer/terminal, file operations.
 */
public final class SystemActions {
    private SystemActions() {
    }

    // ---------------------------------------------------------------- IntelliJ

    /** Looks for an IntelliJ installation (standard installer, Program Files, Toolbox). */
    public static Optional<Path> detectIntelliJ() {
        List<Path> candidates = new ArrayList<>();
        String localAppData = System.getenv("LOCALAPPDATA");
        String programFiles = System.getenv("ProgramFiles");
        if (localAppData != null) {
            addInstallations(candidates, Path.of(localAppData, "Programs"));
        }
        if (programFiles != null) {
            addInstallations(candidates, Path.of(programFiles, "JetBrains"));
        }
        if (localAppData != null) {
            candidates.add(Path.of(localAppData, "JetBrains", "Toolbox", "scripts", "idea.cmd"));
        }
        return candidates.stream().filter(Files::isRegularFile).findFirst();
    }

    private static void addInstallations(List<Path> out, Path parent) {
        if (!Files.isDirectory(parent)) {
            return;
        }
        try (Stream<Path> stream = Files.list(parent)) {
            stream.filter(p -> p.getFileName().toString().startsWith("IntelliJ IDEA"))
                    // "IntelliJ IDEA" (Ultimate) before "IntelliJ IDEA Community Edition", newest first
                    .sorted(Comparator.comparing((Path p) -> p.getFileName().toString().contains("Community"))
                            .thenComparing(Comparator.reverseOrder()))
                    .map(p -> p.resolve("bin").resolve("idea64.exe"))
                    .forEach(out::add);
        } catch (IOException ignored) {
            // not readable -> skip
        }
    }

    public static void openInIntelliJ(Path ide, Path project) throws IOException {
        String file = ide.getFileName().toString().toLowerCase(Locale.ROOT);
        ProcessBuilder pb = file.endsWith(".cmd") || file.endsWith(".bat")
                ? new ProcessBuilder("cmd.exe", "/c", ide.toString(), project.toString())
                : new ProcessBuilder(ide.toString(), project.toString());
        pb.redirectErrorStream(true).redirectOutput(ProcessBuilder.Redirect.DISCARD).start();
    }

    // ---------------------------------------------------------------- Explorer / Terminal

    public static void openExplorer(Path dir) throws IOException {
        if (isWindows()) {
            new ProcessBuilder("explorer.exe", dir.toString()).start();
        } else {
            Desktop.getDesktop().open(dir.toFile());
        }
    }

    public static void openTerminal(Path dir) throws IOException {
        if (!isWindows()) {
            Desktop.getDesktop().open(dir.toFile());
            return;
        }
        try {
            new ProcessBuilder("wt.exe", "-d", dir.toString()).start();
        } catch (IOException e) {
            new ProcessBuilder("cmd.exe", "/c", "start", "cmd.exe").directory(dir.toFile()).start();
        }
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

    public static boolean isWindows() {
        return System.getProperty("os.name", "").toLowerCase(Locale.ROOT).contains("win");
    }
}
