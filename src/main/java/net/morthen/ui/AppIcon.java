package net.morthen.ui;

import javax.imageio.ImageIO;
import java.awt.Graphics2D;
import java.awt.Image;
import java.awt.RenderingHints;
import java.awt.image.BufferedImage;
import java.io.IOException;
import java.io.InputStream;
import java.io.UncheckedIOException;
import java.util.ArrayList;
import java.util.List;

/** Application icon (a grass block) from {@code /icons/icon.png}, scaled to the sizes window managers ask for. */
final class AppIcon {
    private static final BufferedImage SOURCE = load();

    private AppIcon() {
    }

    static List<Image> images() {
        List<Image> images = new ArrayList<>();
        for (int size : new int[]{16, 24, 32, 48, 64}) images.add(image(size));
        images.add(SOURCE);
        return images;
    }

    static BufferedImage image(int size) {
        if (size == SOURCE.getWidth()) return SOURCE;
        BufferedImage img = new BufferedImage(size, size, BufferedImage.TYPE_INT_ARGB);
        Graphics2D g = img.createGraphics();
        g.setRenderingHint(RenderingHints.KEY_INTERPOLATION, RenderingHints.VALUE_INTERPOLATION_BICUBIC);
        g.setRenderingHint(RenderingHints.KEY_RENDERING, RenderingHints.VALUE_RENDER_QUALITY);
        g.drawImage(SOURCE.getScaledInstance(size, size, Image.SCALE_SMOOTH), 0, 0, null);
        g.dispose();
        return img;
    }

    private static BufferedImage load() {
        try (InputStream in = AppIcon.class.getResourceAsStream("/icons/icon.png")) {
            if (in == null) throw new IOException("/icons/icon.png is missing");
            return ImageIO.read(in);
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
    }
}
