package net.morthen.ui;

import javax.swing.JButton;
import javax.swing.JComponent;
import javax.swing.JFileChooser;
import javax.swing.JLabel;
import javax.swing.JPanel;
import javax.swing.JTextField;
import javax.swing.Scrollable;
import javax.swing.UIManager;
import javax.swing.event.DocumentEvent;
import javax.swing.event.DocumentListener;
import javax.swing.text.JTextComponent;
import java.awt.Color;
import java.awt.Component;
import java.awt.Dimension;
import java.awt.Rectangle;
import java.awt.GridBagConstraints;
import java.awt.GridBagLayout;
import java.awt.Insets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Optional;

/** Kleine Swing-Helfer. */
final class Ui {
    private Ui() {
    }

    static JLabel heading(String text, String styleClass) {
        JLabel label = new JLabel(text);
        label.putClientProperty("FlatLaf.styleClass", styleClass);
        return label;
    }

    static JLabel muted(String text) {
        JLabel label = new JLabel(text);
        label.setForeground(UIManager.getColor("Label.disabledForeground"));
        return label;
    }

    /** Read-only Textfeld ohne Rahmen – sieht aus wie ein Label, ist aber kopierbar. */
    static JTextField copyable(String text) {
        JTextField field = new JTextField(text == null ? "" : text);
        field.setEditable(false);
        field.setBorder(null);
        field.setOpaque(false);
        field.setCaretPosition(0);
        field.setToolTipText(text == null || text.isEmpty() ? null : text);
        return field;
    }

    static JButton primary(String text) {
        JButton button = new JButton(text);
        Color bg = UIManager.getColor("Button.default.background");
        Color fg = UIManager.getColor("Button.default.foreground");
        if (bg != null) button.setBackground(bg);
        if (fg != null) button.setForeground(fg);
        button.putClientProperty("FlatLaf.styleClass", "large");
        return button;
    }

    static String hex(Color color) {
        return color == null ? "#888888" : String.format("#%02x%02x%02x", color.getRed(), color.getGreen(), color.getBlue());
    }

    static String html(String text) {
        if (text == null) {
            return "";
        }
        return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;");
    }

    static Optional<Path> chooseDirectory(Component parent, String title, Path start) {
        JFileChooser chooser = new JFileChooser();
        chooser.setDialogTitle(title);
        chooser.setFileSelectionMode(JFileChooser.DIRECTORIES_ONLY);
        if (start != null && Files.isDirectory(start)) {
            chooser.setCurrentDirectory(start.toFile());
        }
        return chooser.showOpenDialog(parent) == JFileChooser.APPROVE_OPTION
                ? Optional.of(chooser.getSelectedFile().toPath())
                : Optional.empty();
    }

    static void onChange(JTextComponent field, Runnable action) {
        field.getDocument().addDocumentListener(new DocumentListener() {
            @Override
            public void insertUpdate(DocumentEvent e) {
                action.run();
            }

            @Override
            public void removeUpdate(DocumentEvent e) {
                action.run();
            }

            @Override
            public void changedUpdate(DocumentEvent e) {
                action.run();
            }
        });
    }

    /** Formular mit Label-Spalte und Feld-Spalte. */
    static final class Form extends JPanel {
        private int row;

        Form() {
            super(new GridBagLayout());
        }

        Form add(String label, JComponent field) {
            return add(label, field, null);
        }

        Form add(String label, JComponent field, JComponent extra) {
            JLabel l = new JLabel(label);
            l.setForeground(UIManager.getColor("Label.disabledForeground"));
            return add(l, field, extra);
        }

        Form add(JLabel l, JComponent field, JComponent extra) {
            add(l, new GridBagConstraints(0, row, 1, 1, 0, 0, GridBagConstraints.WEST,
                    GridBagConstraints.NONE, new Insets(4, 0, 4, 12), 0, 0));
            add(field, new GridBagConstraints(1, row, extra == null ? 2 : 1, 1, 1, 0, GridBagConstraints.WEST,
                    GridBagConstraints.HORIZONTAL, new Insets(4, 0, 4, 0), 0, 0));
            if (extra != null) {
                add(extra, new GridBagConstraints(2, row, 1, 1, 0, 0, GridBagConstraints.WEST,
                        GridBagConstraints.NONE, new Insets(4, 8, 4, 0), 0, 0));
            }
            row++;
            return this;
        }

        Form addFull(JComponent component) {
            add(component, new GridBagConstraints(0, row++, 3, 1, 1, 0, GridBagConstraints.WEST,
                    GridBagConstraints.HORIZONTAL, new Insets(4, 0, 4, 0), 0, 0));
            return this;
        }
    }

    /** Vertikaler Stapel, Komponenten volle Breite, Rest leer. */
    static final class Stack extends JPanel implements Scrollable {
        private int row;

        Stack() {
            super(new GridBagLayout());
        }

        Stack push(JComponent component, int gapBelow) {
            add(component, new GridBagConstraints(0, row++, 1, 1, 1, 0, GridBagConstraints.NORTHWEST,
                    GridBagConstraints.HORIZONTAL, new Insets(0, 0, gapBelow, 0), 0, 0));
            return this;
        }

        // Breite folgt dem Viewport: lange Inhalte werden gekürzt statt horizontal zu scrollen
        @Override
        public Dimension getPreferredScrollableViewportSize() {
            return getPreferredSize();
        }

        @Override
        public int getScrollableUnitIncrement(Rectangle visible, int orientation, int direction) {
            return 16;
        }

        @Override
        public int getScrollableBlockIncrement(Rectangle visible, int orientation, int direction) {
            return visible.height - 32;
        }

        @Override
        public boolean getScrollableTracksViewportWidth() {
            return true;
        }

        @Override
        public boolean getScrollableTracksViewportHeight() {
            return false;
        }

        Stack finish() {
            add(new JPanel(), new GridBagConstraints(0, row++, 1, 1, 1, 1, GridBagConstraints.NORTHWEST,
                    GridBagConstraints.BOTH, new Insets(0, 0, 0, 0), 0, 0));
            for (Component c : getComponents()) {
                if (c instanceof JPanel p && p.getComponentCount() == 0) {
                    p.setOpaque(false);
                }
            }
            return this;
        }
    }
}
