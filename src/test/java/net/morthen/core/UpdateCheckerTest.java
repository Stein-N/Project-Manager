package net.morthen.core;

import org.junit.jupiter.api.Test;

import java.io.IOException;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

class UpdateCheckerTest {
    @Test
    void comparesVersions() {
        assertTrue(UpdateChecker.isNewer("0.2.10", "0.2.9"));
        assertTrue(UpdateChecker.isNewer("1.0.0", "0.9.9"));
        assertFalse(UpdateChecker.isNewer("0.2.3", "0.2.3"));
        assertFalse(UpdateChecker.isNewer("0.2.2", "0.2.3"));
    }

    @Test
    void parsesRelease() throws IOException {
        UpdateChecker.Release r = UpdateChecker.parse("""
                {"tag_name":"v0.2.4","name":"MC Mod Project Manager 0.2.4","html_url":"https://github.com/x/y/releases/tag/v0.2.4",
                 "body":null,"assets":[{"name":"notes.txt","browser_download_url":"u0"},
                 {"name":"Project-Manager-0.2.4.jar","browser_download_url":"https://example/p.jar"}]}""");
        assertEquals("0.2.4", r.version());
        assertEquals("Project-Manager-0.2.4.jar", r.fileName());
        assertEquals("https://example/p.jar", r.downloadUrl());
    }
}
