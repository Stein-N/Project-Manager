package net.morthen.core;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;

import static org.junit.jupiter.api.Assertions.assertEquals;
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
    void rendersGithubFlavoredMarkdown() {
        String html = Readme.markdownToHtml("""
                # Title
                [![Downloads](https://img.shields.io/badge/x.svg)](https://modrinth.com/mod/x)

                | Loader | Version |
                |--------|---------|
                | Fabric | 1.21.1  |

                - [x] done
                - [ ] open

                ~~old~~ and https://example.com and a note[^1]

                [^1]: The footnote.
                """);
        assertTrue(html.contains("<h1 id=\"title\">Title</h1>"), "heading anchor");
        assertTrue(html.contains("src=\"https://img.shields.io/badge/x.svg\""), "badges are kept");
        assertTrue(html.contains("<table>"));
        assertTrue(html.contains("type=\"checkbox\""), "task list");
        assertTrue(html.contains("<del>old</del>"), "strikethrough");
        assertTrue(html.contains("href=\"https://example.com\""), "autolink");
        assertTrue(html.contains("The footnote."), "footnote");
    }

    @Test
    void pageContainsBaseUrlStylesAndScript() throws IOException {
        Path readme = Files.writeString(project.resolve("README.md"), "![logo](docs/logo.png)");
        String page = Readme.toPage(readme);
        assertTrue(page.contains("<base href=\"" + project.toUri()));
        assertTrue(page.contains("max-width: 100%"));
        assertTrue(page.contains("javaLink:"));
    }
}
