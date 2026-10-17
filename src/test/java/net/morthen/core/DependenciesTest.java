package net.morthen.core;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

class DependenciesTest {
    @TempDir
    Path project;

    @Test
    void readsKnownAndOtherVersionsAndSkipsRanges() throws IOException {
        Files.writeString(project.resolve("gradle.properties"), """
                # Fabric
                minecraft_version=1.21.1
                yarn_mappings=1.21.1+build.3
                loader_version=0.16.5
                fabric_version=0.104.0+1.21.1
                mod_version=1.0.0
                modmenu_version=11.0.2
                loader_version_range=[4,)
                org.gradle.jvmargs=-Xmx2G
                """);
        Files.createDirectories(project.resolve("gradle/wrapper"));
        Files.writeString(project.resolve("gradle/wrapper/gradle-wrapper.properties"),
                "distributionUrl=https\\://services.gradle.org/distributions/gradle-8.10.2-bin.zip\n");

        List<Dependencies.Entry> entries = Dependencies.read(project);
        List<String> labels = entries.stream().map(Dependencies.Entry::label).toList();
        assertEquals(List.of("Fabric Loader", "Fabric API", "Yarn mappings", "Gradle wrapper", "modmenu_version"), labels);
        assertEquals("8.10.2", entries.get(3).value());
        assertFalse(entries.get(4).checkable());
    }

    @Test
    void updateKeepsFormattingAndLineEndings() throws IOException {
        Path file = project.resolve("gradle.properties");
        Files.writeString(file, "# comment\r\nfabric_version = 0.1.0+1.21.1\r\nloader_version=0.16.0\r\n");
        Dependencies.update(project, "fabric_version", "0.2.0+1.21.1");
        assertEquals("# comment\r\nfabric_version = 0.2.0+1.21.1\r\nloader_version=0.16.0\r\n", Files.readString(file));
    }

    @Test
    void neoForgePrefixAndNewerCheck() {
        assertEquals("21.1.", Dependencies.neoForgePrefix("1.21.1"));
        assertEquals("21.0.", Dependencies.neoForgePrefix("1.21"));
        assertEquals("26.1.", Dependencies.neoForgePrefix("26.1"));
        assertTrue(Dependencies.isNewer("0.105.0+1.21.1", "0.104.0+1.21.1"));
        assertFalse(Dependencies.isNewer("0.16.5", "0.16.5"));
        assertFalse(Dependencies.isNewer("0.16.4", "0.16.5"));
    }
}
