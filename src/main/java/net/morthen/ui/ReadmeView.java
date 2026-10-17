package net.morthen.ui;

import net.morthen.core.Readme;

import javax.swing.BorderFactory;
import javax.swing.JButton;
import javax.swing.JComponent;
import javax.swing.JEditorPane;
import javax.swing.JPanel;
import javax.swing.JScrollPane;
import javax.swing.SwingUtilities;
import javax.swing.SwingWorker;
import javax.swing.UIManager;
import javax.swing.event.HyperlinkEvent;
import javax.swing.text.html.HTMLDocument;
import javax.swing.text.html.HTMLEditorKit;
import javax.swing.text.html.StyleSheet;
import java.awt.BorderLayout;
import java.awt.Desktop;
import java.awt.Font;
import java.net.URI;
import java.nio.file.Path;
import java.util.function.Consumer;

/** Rendered README of a project. */
final class ReadmeView {
    private ReadmeView() {
    }

    static JComponent create(Path readme, Consumer<String> onError) {
        JEditorPane pane = new JEditorPane();
        pane.setEditable(false);
        pane.setBorder(BorderFactory.createEmptyBorder(8, 12, 12, 12));
        pane.setBackground(UIManager.getColor("TextArea.background"));
        HTMLEditorKit kit = new StyledKit();
        pane.setEditorKit(kit);
        HTMLDocument doc = (HTMLDocument) kit.createDefaultDocument();
        try {
            doc.setBase(readme.getParent().toUri().toURL()); // relative images and links
        } catch (Exception ignored) {
            // images just won't load
        }
        pane.setDocument(doc);
        pane.setText("<p style='color:#808080'>Loading README…</p>");
        pane.addHyperlinkListener(e -> {
            if (e.getEventType() != HyperlinkEvent.EventType.ACTIVATED) return;
            String description = e.getDescription();
            if (description != null && description.startsWith("#")) {
                pane.scrollToReference(description.substring(1));
                return;
            }
            try {
                URI uri = e.getURL() != null ? e.getURL().toURI() : URI.create(description);
                if ("file".equals(uri.getScheme())) Desktop.getDesktop().open(Path.of(uri).toFile());
                else Desktop.getDesktop().browse(uri);
            } catch (Exception ex) {
                onError.accept("Could not open link: " + description);
            }
        });

        new SwingWorker<String, Void>() {
            @Override
            protected String doInBackground() throws Exception {
                return Readme.toHtml(readme);
            }

            @Override
            protected void done() {
                try {
                    pane.setText(get());
                } catch (Exception ex) {
                    pane.setText("<p>Could not read " + readme.getFileName() + ".</p>");
                }
                pane.setCaretPosition(0);
            }
        }.execute();

        JScrollPane scroll = new JScrollPane(pane);
        scroll.setBorder(null);
        scroll.getVerticalScrollBar().setUnitIncrement(16);

        JPanel bar = new JPanel(new BorderLayout());
        bar.setBorder(BorderFactory.createEmptyBorder(4, 12, 4, 8));
        bar.add(Ui.muted(readme.getFileName().toString()), BorderLayout.WEST);
        JButton open = new JButton("Open file");
        open.putClientProperty("JButton.buttonType", "toolBarButton");
        open.addActionListener(e -> {
            try {
                Desktop.getDesktop().open(readme.toFile());
            } catch (Exception ex) {
                onError.accept("Could not open " + readme);
            }
        });
        bar.add(open, BorderLayout.EAST);

        JPanel panel = new JPanel(new BorderLayout());
        panel.add(bar, BorderLayout.NORTH);
        panel.add(scroll, BorderLayout.CENTER);
        SwingUtilities.invokeLater(() -> scroll.getVerticalScrollBar().setValue(0));
        return panel;
    }

    /** HTMLEditorKit with its own style sheet (the default one is shared by all editor kits). */
    private static final class StyledKit extends HTMLEditorKit {
        private final StyleSheet styles = new StyleSheet();

        StyledKit() {
            styles.addStyleSheet(super.getStyleSheet());
            Font font = UIManager.getFont("Label.font");
            String family = font != null ? font.getFamily() : "SansSerif";
            int size = font != null ? font.getSize() : 13;
            styles.addRule("body { font-family: '" + family + "'; font-size: " + size + "pt; color: #1f1f1f; }");
            styles.addRule("h1 { font-size: " + (size + 9) + "pt; margin-top: 4px; margin-bottom: 6px; }");
            styles.addRule("h2 { font-size: " + (size + 5) + "pt; margin-top: 12px; margin-bottom: 4px; }");
            styles.addRule("h3 { font-size: " + (size + 2) + "pt; margin-top: 10px; margin-bottom: 4px; }");
            styles.addRule("p { margin-top: 4px; margin-bottom: 6px; }");
            styles.addRule("code { font-family: Monospaced; font-size: " + (size - 1) + "pt; background-color: #f0f0f0; }");
            styles.addRule("pre { font-family: Monospaced; font-size: " + (size - 1) + "pt; background-color: #f5f5f5; padding: 6px; }");
            styles.addRule("blockquote { color: #666666; margin-left: 10px; padding-left: 8px; border-left: 3px solid #d0d0d0; }");
            styles.addRule("a { color: #2675bf; }");
            styles.addRule("th { background-color: #f0f0f0; }");
        }

        @Override
        public StyleSheet getStyleSheet() {
            return styles;
        }
    }
}
