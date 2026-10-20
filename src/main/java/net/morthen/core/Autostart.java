package net.morthen.core;

import java.io.IOException;
import java.net.URISyntaxException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Optional;
import java.util.concurrent.TimeUnit;

/**
 * Starts the manager (minimized) when the user logs in: a registry "Run" entry. Windows only, like the tray icon it
 * minimizes to.
 */
public final class Autostart {
    public static final String MINIMIZED_FLAG = "--minimized";

    private static final String NAME = "MC Mod Project Manager";
    private static final String RUN_KEY = "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run";

    private Autostart() {
    }

    /** Autostart needs Windows and the app running from its jar (not from the IDE). */
    public static boolean supported() {
        return SystemActions.isWindows() && launchCommand().isPresent();
    }

    /** Creates or removes the autostart entry. */
    public static void apply(boolean enabled) throws IOException {
        if (!SystemActions.isWindows()) throw new IOException("Autostart only works on Windows.");
        if (enabled) enable();
        else disable();
    }

    private static void enable() throws IOException {
        List<String> command = launchCommand()
                .orElseThrow(() -> new IOException("Autostart only works when started from the jar file."));
        String value = String.join(" ", command.stream().map(Autostart::quoteWindows).toList());
        // Java would escape the inner quotes in a way reg.exe rejects; an argument that is already
        // wrapped in quotes is passed through verbatim, so escape them the way reg's parser expects.
        String quoted = "\"" + value.replace("\"", "\\\"") + "\"";
        run("reg", "add", RUN_KEY, "/v", NAME, "/t", "REG_SZ", "/d", quoted, "/f");
    }

    private static void disable() throws IOException {
        // a missing entry makes reg fail, which is fine
        Process process = new ProcessBuilder("reg", "delete", RUN_KEY, "/v", NAME, "/f")
                .redirectErrorStream(true).start();
        process.getInputStream().readAllBytes();
    }

    /** javaw + jar of the running app plus the minimized flag; empty when not started from a jar. */
    static Optional<List<String>> launchCommand() {
        try {
            Path jar = Path.of(Autostart.class.getProtectionDomain().getCodeSource().getLocation().toURI());
            if (!Files.isRegularFile(jar) || !jar.getFileName().toString().endsWith(".jar")) {
                return Optional.empty();
            }
            Path java = Path.of(System.getProperty("java.home"), "bin", "javaw.exe");
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
