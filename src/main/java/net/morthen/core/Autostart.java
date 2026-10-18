package net.morthen.core;

import java.io.IOException;
import java.net.URISyntaxException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.concurrent.TimeUnit;

/**
 * Starts the manager (minimized) when the user logs in: a registry "Run" entry on Windows,
 * an XDG autostart {@code .desktop} file on Linux.
 */
public final class Autostart {
    public static final String MINIMIZED_FLAG = "--minimized";

    private static final String NAME = "MC Mod Project Manager";
    private static final String RUN_KEY = "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private static final String DESKTOP_FILE = "mc-project-manager.desktop";

    private Autostart() {
    }

    /** Autostart needs the app to run from its jar (not from the IDE) on Windows or Linux. */
    public static boolean supported() {
        return SystemActions.os() != SystemActions.Os.MAC && launchCommand().isPresent();
    }

    /** Creates or removes the autostart entry. */
    public static void apply(boolean enabled) throws IOException {
        if (enabled) enable();
        else disable();
    }

    private static void enable() throws IOException {
        List<String> command = launchCommand()
                .orElseThrow(() -> new IOException("Autostart only works when started from the jar file."));
        switch (SystemActions.os()) {
            case WINDOWS -> {
                String value = String.join(" ", command.stream().map(Autostart::quoteWindows).toList());
                // Java would escape the inner quotes in a way reg.exe rejects; an argument that is already
                // wrapped in quotes is passed through verbatim, so escape them the way reg's parser expects.
                String quoted = "\"" + value.replace("\"", "\\\"") + "\"";
                run("reg", "add", RUN_KEY, "/v", NAME, "/t", "REG_SZ", "/d", quoted, "/f");
            }
            case LINUX -> {
                Path file = desktopFile(System.getenv(), Path.of(System.getProperty("user.home")));
                Files.createDirectories(file.getParent());
                Files.writeString(file, desktopEntry(command), StandardCharsets.UTF_8);
            }
            default -> throw new IOException("Autostart is not supported on this system.");
        }
    }

    private static void disable() throws IOException {
        switch (SystemActions.os()) {
            case WINDOWS -> {
                // a missing entry makes reg fail, which is fine
                Process process = new ProcessBuilder("reg", "delete", RUN_KEY, "/v", NAME, "/f")
                        .redirectErrorStream(true).start();
                process.getInputStream().readAllBytes();
            }
            case LINUX -> Files.deleteIfExists(desktopFile(System.getenv(), Path.of(System.getProperty("user.home"))));
            default -> {
            }
        }
    }

    /** Java + jar of the running app plus the minimized flag; empty when not started from a jar. */
    static Optional<List<String>> launchCommand() {
        try {
            Path jar = Path.of(Autostart.class.getProtectionDomain().getCodeSource().getLocation().toURI());
            if (!Files.isRegularFile(jar) || !jar.getFileName().toString().endsWith(".jar")) {
                return Optional.empty();
            }
            Path java = Path.of(System.getProperty("java.home"), "bin", SystemActions.isWindows() ? "javaw.exe" : "java");
            List<String> command = new ArrayList<>();
            command.add(java.toString());
            command.add("--enable-native-access=ALL-UNNAMED");
            command.add("-jar");
            command.add(jar.toAbsolutePath().toString());
            command.add(MINIMIZED_FLAG);
            return Optional.of(command);
        } catch (URISyntaxException | RuntimeException e) {
            return Optional.empty();
        }
    }

    static Path desktopFile(Map<String, String> env, Path home) {
        String xdg = env.get("XDG_CONFIG_HOME");
        Path config = xdg != null && !xdg.isBlank() ? Path.of(xdg) : home.resolve(".config");
        return config.resolve("autostart").resolve(DESKTOP_FILE);
    }

    static String desktopEntry(List<String> command) {
        String exec = String.join(" ", command.stream().map(Autostart::quoteDesktop).toList());
        return "[Desktop Entry]\n"
                + "Type=Application\n"
                + "Name=" + NAME + "\n"
                + "Exec=" + exec + "\n"
                + "Terminal=false\n"
                + "X-GNOME-Autostart-enabled=true\n";
    }

    /** Quoting of the Exec key: arguments with special characters in double quotes, {@code \ " ` $} escaped. */
    static String quoteDesktop(String arg) {
        if (arg.matches("[A-Za-z0-9_@+=:,./-]+")) return arg;
        String escaped = arg.replace("\\", "\\\\").replace("\"", "\\\"").replace("`", "\\`")
                .replace("$", "\\$").replace("%", "%%");
        return "\"" + escaped + "\"";
    }

    private static String quoteWindows(String arg) {
        return arg.contains(" ") ? "\"" + arg + "\"" : arg;
    }

    private static void run(String... command) throws IOException {
        Process process = new ProcessBuilder(command).redirectErrorStream(true).start();
        String output = new String(process.getInputStream().readAllBytes(), StandardCharsets.UTF_8).trim();
        try {
            if (!process.waitFor(15, TimeUnit.SECONDS) || process.exitValue() != 0) {
                throw new IOException("Could not register autostart: " + output);
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new IOException("Interrupted", e);
        }
    }
}
