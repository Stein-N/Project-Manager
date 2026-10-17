package net.morthen.core;

import org.commonmark.Extension;
import org.commonmark.ext.autolink.AutolinkExtension;
import org.commonmark.ext.footnotes.FootnotesExtension;
import org.commonmark.ext.gfm.strikethrough.StrikethroughExtension;
import org.commonmark.ext.gfm.tables.TablesExtension;
import org.commonmark.ext.heading.anchor.HeadingAnchorExtension;
import org.commonmark.ext.task.list.items.TaskListItemsExtension;
import org.commonmark.parser.Parser;
import org.commonmark.renderer.html.HtmlRenderer;

import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.Locale;
import java.util.Optional;
import java.util.stream.Stream;

/**
 * Finds a project's README and renders it (GitHub flavored Markdown) into a complete HTML page.
 */
public final class Readme {
    private static final List<String> NAMES = List.of("readme.md", "readme.markdown", "readme.txt", "readme");
    private static final List<Extension> EXTENSIONS = List.of(
            TablesExtension.create(),
            StrikethroughExtension.create(),
            TaskListItemsExtension.create(),
            AutolinkExtension.create(),
            HeadingAnchorExtension.create(),
            FootnotesExtension.create());
    private static final Parser PARSER = Parser.builder().extensions(EXTENSIONS).build();
    private static final HtmlRenderer RENDERER = HtmlRenderer.builder().extensions(EXTENSIONS).build();

    private Readme() {
    }

    /** README in the project root (case-insensitive; Markdown preferred). */
    public static Optional<Path> find(Path project) {
        if (!Files.isDirectory(project)) return Optional.empty();
        try (Stream<Path> files = Files.list(project)) {
            List<Path> candidates = files.filter(Files::isRegularFile).toList();
            for (String name : NAMES) {
                for (Path file : candidates) {
                    if (file.getFileName().toString().toLowerCase(Locale.ROOT).equals(name)) return Optional.of(file);
                }
            }
        } catch (IOException ignored) {
            // no README
        }
        return Optional.empty();
    }

    /** Complete HTML page: rendered README, style sheet, link handling script and a base URL for relative paths. */
    public static String toPage(Path file) throws IOException {
        String text = Files.readString(file);
        String name = file.getFileName().toString().toLowerCase(Locale.ROOT);
        String body = name.endsWith(".md") || name.endsWith(".markdown")
                ? markdownToHtml(text)
                : "<pre>" + escape(text) + "</pre>";
        String base = file.toAbsolutePath().getParent().toUri().toString();
        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\">"
                + "<base href=\"" + escape(base) + "\">"
                + "<style>" + resource("readme.css") + "</style>"
                + "</head><body><article class=\"markdown-body\">" + body + "</article>"
                + "<script>" + resource("readme.js") + "</script>"
                + "</body></html>";
    }

    public static String markdownToHtml(String markdown) {
        return RENDERER.render(PARSER.parse(markdown));
    }

    private static String resource(String name) throws IOException {
        try (InputStream in = Readme.class.getResourceAsStream("/readme/" + name)) {
            if (in == null) throw new IOException("missing resource " + name);
            return new String(in.readAllBytes(), StandardCharsets.UTF_8);
        }
    }

    private static String escape(String text) {
        return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace("\"", "&quot;");
    }
}
