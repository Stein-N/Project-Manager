package net.morthen.core;

import org.commonmark.ext.gfm.tables.TableBlock;
import org.commonmark.ext.gfm.tables.TablesExtension;
import org.commonmark.node.AbstractVisitor;
import org.commonmark.node.Image;
import org.commonmark.node.Node;
import org.commonmark.node.Text;
import org.commonmark.parser.Parser;
import org.commonmark.renderer.html.HtmlRenderer;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.Optional;
import java.util.stream.Stream;

/**
 * Finds a project's README and converts Markdown to HTML that Swing's HTML renderer can display.
 */
public final class Readme {
    private static final List<String> NAMES = List.of("readme.md", "readme.markdown", "readme.txt", "readme");
    private static final List<org.commonmark.Extension> EXTENSIONS = List.of(TablesExtension.create());
    private static final Parser PARSER = Parser.builder().extensions(EXTENSIONS).build();
    private static final HtmlRenderer RENDERER = HtmlRenderer.builder()
            .extensions(EXTENSIONS)
            .attributeProviderFactory(context -> (node, tag, attributes) -> {
                // Swing's renderer ignores CSS borders on tables – use HTML attributes instead
                if (node instanceof TableBlock) {
                    attributes.put("border", "1");
                    attributes.put("cellpadding", "5");
                    attributes.put("cellspacing", "0");
                }
            })
            .build();

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

    public static String toHtml(Path file) throws IOException {
        String text = Files.readString(file);
        String name = file.getFileName().toString().toLowerCase(Locale.ROOT);
        if (!name.endsWith(".md") && !name.endsWith(".markdown")) {
            return "<pre>" + escape(text) + "</pre>";
        }
        return markdownToHtml(text);
    }

    static String markdownToHtml(String markdown) {
        Node document = PARSER.parse(markdown);
        replaceRemoteImages(document);
        return RENDERER.render(document);
    }

    /**
     * Swing cannot render SVG (most badges) and would block while downloading remote images,
     * so remote and SVG images are replaced by their alt text.
     */
    private static void replaceRemoteImages(Node document) {
        List<Image> images = new ArrayList<>();
        document.accept(new AbstractVisitor() {
            @Override
            public void visit(Image image) {
                images.add(image);
            }
        });
        for (Image image : images) {
            String dest = image.getDestination().toLowerCase(Locale.ROOT);
            boolean remote = dest.startsWith("http://") || dest.startsWith("https://") || dest.startsWith("//");
            if (remote || dest.endsWith(".svg") || dest.contains(".svg?")) {
                StringBuilder alt = new StringBuilder();
                for (Node child = image.getFirstChild(); child != null; child = child.getNext()) {
                    if (child instanceof Text t) alt.append(t.getLiteral());
                }
                if (!alt.isEmpty()) image.insertBefore(new Text("[" + alt + "]"));
                image.unlink();
            }
        }
    }

    private static String escape(String text) {
        return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;");
    }
}
