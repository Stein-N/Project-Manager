package net.morthen.ui;

import javafx.application.Platform;
import javafx.embed.swing.JFXPanel;
import javafx.scene.Scene;
import javafx.scene.paint.Color;
import javafx.scene.web.WebEngine;
import javafx.scene.web.WebView;
import net.morthen.core.Readme;

import javax.swing.BorderFactory;
import javax.swing.JButton;
import javax.swing.JComponent;
import javax.swing.JPanel;
import javax.swing.SwingUtilities;
import java.awt.BorderLayout;
import java.awt.Desktop;
import java.net.URI;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.function.Consumer;

/**
 * README preview rendered by JavaFX WebView (full HTML/CSS, SVG badges, remote images).
 * One WebView is shared and only reloaded when the README changes.
 */
final class ReadmeView {
    private static JFXPanel fxPanel;
    private static WebEngine engine;
    private static Path shownFile;
    private static long shownModified;
    private static Path pageFile;
    private static Consumer<String> errorHandler = msg -> { };

    private ReadmeView() {
    }

    static JComponent create(Path readme, Consumer<String> onError) {
        errorHandler = onError;
        ensureBrowser();
        load(readme);

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
        panel.add(fxPanel, BorderLayout.CENTER); // moves the shared panel into this tab
        return panel;
    }

    private static void ensureBrowser() {
        if (fxPanel != null) return;
        fxPanel = new JFXPanel(); // starts the JavaFX runtime
        // details are rebuilt often – the runtime must survive when the panel is temporarily detached
        Platform.setImplicitExit(false);
        Platform.runLater(() -> {
            WebView view = new WebView();
            view.setContextMenuEnabled(false);
            engine = view.getEngine();
            // readme.js reports clicked links as alert("javaLink:<url>")
            engine.setOnAlert(event -> {
                String data = event.getData();
                if (data != null && data.startsWith("javaLink:")) openLink(data.substring("javaLink:".length()));
            });
            fxPanel.setScene(new Scene(view, Color.WHITE));
            if (shownFile != null) reload(shownFile);
        });
    }

    private static void load(Path readme) {
        long modified = lastModified(readme);
        if (readme.equals(shownFile) && modified == shownModified) return; // already shown
        shownFile = readme;
        shownModified = modified;
        Platform.runLater(() -> {
            if (engine != null) reload(readme);
        });
    }

    /** FX thread. */
    private static void reload(Path readme) {
        try {
            // Loaded from a file (not loadContent): only file pages may load local images of the project
            if (pageFile == null) {
                pageFile = Files.createTempFile("mcpm-readme", ".html");
                pageFile.toFile().deleteOnExit();
            }
            Files.writeString(pageFile, Readme.toPage(readme));
            engine.load(pageFile.toUri() + "?t=" + System.nanoTime()); // query forces a reload
        } catch (Exception e) {
            engine.loadContent("<p style='font-family:sans-serif'>Could not read " + readme.getFileName() + ": " + e.getMessage() + "</p>");
        }
    }

    private static long lastModified(Path file) {
        try {
            return Files.getLastModifiedTime(file).toMillis();
        } catch (Exception e) {
            return 0;
        }
    }

    /** Links open in the browser, local files with their default application. */
    private static void openLink(String href) {
        SwingUtilities.invokeLater(() -> {
            try {
                URI uri = URI.create(href);
                if ("file".equals(uri.getScheme())) Desktop.getDesktop().open(Path.of(uri).toFile());
                else Desktop.getDesktop().browse(uri);
            } catch (Exception e) {
                errorHandler.accept("Could not open link: " + href);
            }
        });
    }
}
