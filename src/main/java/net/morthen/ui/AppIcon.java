package net.morthen.ui;

import java.awt.Color;
import java.awt.Graphics2D;
import java.awt.Image;
import java.awt.RenderingHints;
import java.awt.geom.RoundRectangle2D;
import java.awt.image.BufferedImage;
import java.util.List;

/** Application icon (a small grass block), drawn in code so no image resources are needed. */
final class AppIcon {
    private AppIcon() {
    }

    static List<Image> images() {
        return List.of(image(16), image(24), image(32), image(48), image(64), image(128));
    }

    static BufferedImage image(int size) {
        BufferedImage img = new BufferedImage(size, size, BufferedImage.TYPE_INT_ARGB);
        Graphics2D g = img.createGraphics();
        g.setRenderingHint(RenderingHints.KEY_ANTIALIASING, RenderingHints.VALUE_ANTIALIAS_ON);
        float arc = size / 5f;
        RoundRectangle2D shape = new RoundRectangle2D.Float(0, 0, size, size, arc, arc);
        g.setClip(shape);
        // dirt
        g.setColor(new Color(0x8B5A2B));
        g.fillRect(0, 0, size, size);
        int px = Math.max(1, size / 8);
        g.setColor(new Color(0x6F4520));
        for (int y = size * 3 / 8; y < size; y += px * 2) {
            for (int x = ((y / px) % 3) * px; x < size; x += px * 3) {
                g.fillRect(x, y, px, px);
            }
        }
        // grass
        int grass = size * 3 / 8;
        g.setColor(new Color(0x5FAF3A));
        g.fillRect(0, 0, size, grass);
        g.setColor(new Color(0x4A9130));
        for (int x = 0; x < size; x += px * 2) {
            g.fillRect(x, grass, px, px);
        }
        g.dispose();
        return img;
    }
}
