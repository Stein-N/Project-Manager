package net.morthen.core;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Properties;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

class ModMetadataTest {
    @TempDir
    Path project;

    @Test
    void readsFabricModJsonWithPlaceholdersAndIconObject() throws IOException {
        Path res = Files.createDirectories(project.resolve("src/main/resources"));
        Files.createDirectories(res.resolve("assets/coolmod"));
        Files.write(res.resolve("assets/coolmod/icon.png"), new byte[]{1});
        Files.writeString(res.resolve("fabric.mod.json"), """
                {
                  "schemaVersion": 1,
                  "id": "${mod_id}",
                  "version": "${version}",
                  "name": "Cool Mod",
                  "authors": ["Nico", {"name": "Helper"}],
                  "icon": {"16": "assets/coolmod/small.png", "128": "assets/${mod_id}/icon.png"},
                  "depends": {"fabricloader": ">=0.16", "minecraft": ["~1.21", "1.21.1"]},
                  "recommends": {"modmenu": "*"}
                }
                """);
        Properties gradle = new Properties();
        gradle.setProperty("mod_id", "coolmod");
        gradle.setProperty("mod_version", "1.2.3");

        ModMetadata m = ModMetadata.read(project, gradle).orElseThrow();
        assertEquals("coolmod", m.id());
        assertEquals("1.2.3", m.version());
        assertEquals("Cool Mod", m.name());
        assertEquals(java.util.List.of("Nico", "Helper"), m.authors());
        assertNotNull(m.icon());
        assertEquals(3, m.dependencies().size());
        assertEquals("~1.21 || 1.21.1", m.dependencies().get(1).version());
        assertFalse(m.dependencies().get(2).required());
    }

    @Test
    void readsNeoForgeTomlInSubproject() throws IOException {
        Path res = Files.createDirectories(project.resolve("neoforge/src/main/resources/META-INF"));
        Files.writeString(res.resolve("neoforge.mods.toml"), """
                modLoader="javafml" # comment
                loaderVersion="${loader_version_range}"
                license="MIT"

                [[mods]]
                modId="${mod_id}"
                version="${mod_version}"
                displayName="Example # not a comment"
                authors="Nico"
                description='''
                First line
                second line'''

                [[dependencies.${mod_id}]]
                    modId="neoforge"
                    type="required"
                    versionRange="[21.1,)"

                [[dependencies.${mod_id}]]
                    modId="jei"
                    type="optional"
                    versionRange="*"
                """);
        Properties gradle = new Properties();
        gradle.setProperty("mod_id", "examplemod");
        gradle.setProperty("mod_version", "2.0");

        ModMetadata m = ModMetadata.read(project, gradle).orElseThrow();
        assertEquals("examplemod", m.id());
        assertEquals("Example # not a comment", m.name());
        assertEquals("First line\nsecond line", m.description());
        assertEquals(2, m.dependencies().size());
        assertTrue(m.dependencies().get(0).required());
        assertFalse(m.dependencies().get(1).required());
        assertTrue(m.file().replace('\\', '/').endsWith("neoforge/src/main/resources/META-INF/neoforge.mods.toml"));
    }

    @Test
    void forgeMandatoryFlag() throws IOException {
        Path res = Files.createDirectories(project.resolve("src/main/resources/META-INF"));
        Files.writeString(res.resolve("mods.toml"), """
                [[mods]]
                modId="old"
                [[dependencies.old]]
                    modId="forge"
                    mandatory=true
                [[dependencies.old]]
                    modId="curios"
                    mandatory=false
                """);
        ModMetadata m = ModMetadata.read(project, new Properties()).orElseThrow();
        assertTrue(m.dependencies().get(0).required());
        assertFalse(m.dependencies().get(1).required());
    }
}
