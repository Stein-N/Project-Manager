package net.morthen.core;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

class ReadmeTest {
    @TempDir
    Path project;

    @Test
    void findsReadmeCaseInsensitive() throws IOException {
        assertTrue(Readme.find(project).isEmpty());
        Files.writeString(project.resolve("ReadMe.MD"), "# Hi");
        assertEquals("ReadMe.MD", Readme.find(project).orElseThrow().getFileName().toString());
    }

    @Test
    void rendersMarkdownTablesAndDropsRemoteImages() {
        String html = Readme.markdownToHtml("""
                # Title
                [![Downloads](https://img.shields.io/badge/x.svg)](https://modrinth.com/mod/x)
                ![local](docs/shot.png)

                | Loader | Version |
                |--------|---------|
                | Fabric | 1.21.1  |
                """);
        assertTrue(html.contains("<h1>Title</h1>"));
        assertFalse(html.contains("shields.io"), "remote badge image removed");
        assertTrue(html.contains("[Downloads]"), "alt text kept");
        assertTrue(html.contains("href=\"https://modrinth.com/mod/x\""), "link kept");
        assertTrue(html.contains("src=\"docs/shot.png\""), "local image kept");
        assertTrue(html.contains("<table border=\"1\""));
    }
}
