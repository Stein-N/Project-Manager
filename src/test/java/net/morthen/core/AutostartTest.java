package net.morthen.core;

import org.junit.jupiter.api.Test;

import java.nio.file.Path;
import java.util.List;
import java.util.Map;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

class AutostartTest {
    @Test
    void desktopFileFollowsXdgConfigHome() {
        Path home = Path.of("home");
        assertEquals(home.resolve(".config").resolve("autostart").resolve("mc-project-manager.desktop"),
                Autostart.desktopFile(Map.of(), home));
        assertEquals(Path.of("cfg").resolve("autostart").resolve("mc-project-manager.desktop"),
                Autostart.desktopFile(Map.of("XDG_CONFIG_HOME", "cfg"), home));
    }

    @Test
    void desktopEntryQuotesArgumentsWithSpaces() {
        String entry = Autostart.desktopEntry(List.of("/usr/bin/java", "-jar", "/opt/my apps/pm.jar", "--minimized"));
        assertTrue(entry.contains("Exec=/usr/bin/java -jar \"/opt/my apps/pm.jar\" --minimized\n"));
        assertTrue(entry.startsWith("[Desktop Entry]\n"));
    }
}
