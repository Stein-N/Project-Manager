package net.morthen.core;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

class EnvFileTest {
    @TempDir
    Path master;

    @Test
    void parsesCommonDotenvSyntax() {
        Map<String, String> vars = EnvFile.parse("""
                # tokens
                MODRINTH_TOKEN=mrp_abc123
                export CURSEFORGE_TOKEN = cf-xyz   # inline comment
                QUOTED="a value with # hash and \\"quotes\\""
                SINGLE='raw $value'
                EMPTY=
                not a variable line
                """);
        assertEquals(List.of("MODRINTH_TOKEN", "CURSEFORGE_TOKEN", "QUOTED", "SINGLE", "EMPTY"), List.copyOf(vars.keySet()));
        assertEquals("mrp_abc123", vars.get("MODRINTH_TOKEN"));
        assertEquals("cf-xyz", vars.get("CURSEFORGE_TOKEN"));
        assertEquals("a value with # hash and \"quotes\"", vars.get("QUOTED"));
        assertEquals("raw $value", vars.get("SINGLE"));
        assertEquals("", vars.get("EMPTY"));
    }

    @Test
    void saveKeepsCommentsAndOrderAndRoundTrips() throws IOException {
        Files.writeString(master.resolve(".env"), "# my tokens\nA=1\n\n# second\nB=2\nC=3\n");
        Map<String, String> vars = new LinkedHashMap<>(EnvFile.load(master));
        vars.put("B", "two words");
        vars.remove("C");
        vars.put("D", "new\"value");
        EnvFile.save(master, vars);

        assertEquals("# my tokens\nA=1\n\n# second\nB=\"two words\"\nD=\"new\\\"value\"\n",
                Files.readString(master.resolve(".env")).replace("\r\n", "\n"));
        assertEquals(vars, EnvFile.load(master));
    }

    @Test
    void newFileGetsHeaderAndNamesAreValidated() throws IOException {
        EnvFile.save(master, Map.of("GITHUB_TOKEN", "ghp_1"));
        assertTrue(Files.readString(master.resolve(".env")).startsWith("# Environment variables"));
        assertTrue(EnvFile.validName("MODRINTH_TOKEN"));
        assertFalse(EnvFile.validName("1BAD"));
        assertFalse(EnvFile.validName("WITH-DASH"));
    }
}
