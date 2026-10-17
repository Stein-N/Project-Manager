package net.morthen.core;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.Map;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

/** Platform specific lookups, exercised with a simulated Linux home and PATH. */
class SystemActionsTest {
    @TempDir
    Path tmp;

    @Test
    void findsIntelliJOnLinuxInToolboxAndPath() throws IOException {
        Path home = tmp.resolve("home");
        Path bin = Files.createDirectories(tmp.resolve("usr-bin"));
        Path toolboxApp = touch(home.resolve(".local/share/JetBrains/Toolbox/apps/intellij-idea-ultimate/bin/idea.sh"));
        Path oldToolbox = touch(home.resolve(".local/share/JetBrains/Toolbox/apps/IDEA-C/ch-0/241.1/bin/idea.sh"));
        Path onPath = touch(bin.resolve("idea"));

        List<Path> existing = SystemActions.intellijCandidates(SystemActions.Os.LINUX, Map.of(), home, List.of(bin))
                .stream().filter(Files::isRegularFile).toList();
        assertEquals(List.of(toolboxApp, oldToolbox, onPath), existing, "Ultimate before Community, PATH last");

        Path script = touch(home.resolve(".local/share/JetBrains/Toolbox/scripts/idea"));
        assertEquals(script, SystemActions.intellijCandidates(SystemActions.Os.LINUX, Map.of(), home, List.of(bin))
                .stream().filter(Files::isRegularFile).findFirst().orElseThrow(), "Toolbox script is preferred");
    }

    @Test
    void windowsCandidatesStillWork() throws IOException {
        Path local = tmp.resolve("LocalAppData");
        Path community = touch(local.resolve("Programs/IntelliJ IDEA Community Edition/bin/idea64.exe"));
        Path ultimate = touch(local.resolve("Programs/IntelliJ IDEA/bin/idea64.exe"));
        List<Path> existing = SystemActions.intellijCandidates(SystemActions.Os.WINDOWS,
                        Map.of("LOCALAPPDATA", local.toString()), tmp, List.of())
                .stream().filter(Files::isRegularFile).toList();
        assertEquals(List.of(ultimate, community), existing);
    }

    @Test
    void picksALinuxTerminal() throws IOException {
        Path bin = Files.createDirectories(tmp.resolve("bin"));
        Path dir = tmp.resolve("project");
        assertTrue(SystemActions.terminalCommand(SystemActions.Os.LINUX, Map.of(), dir, List.of(bin)).isEmpty());

        touch(bin.resolve("xterm"));
        touch(bin.resolve("konsole"));
        assertEquals(List.of("konsole", "--workdir", dir.toString()),
                SystemActions.terminalCommand(SystemActions.Os.LINUX, Map.of(), dir, List.of(bin)).orElseThrow());

        touch(bin.resolve("kitty"));
        assertEquals(List.of("kitty"), SystemActions.terminalCommand(SystemActions.Os.LINUX,
                Map.of("TERMINAL", "kitty"), dir, List.of(bin)).orElseThrow(), "$TERMINAL wins");
    }

    private static Path touch(Path file) throws IOException {
        Files.createDirectories(file.getParent());
        return Files.writeString(file, "");
    }
}
