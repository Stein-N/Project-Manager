package net.morthen.ui;

import net.morthen.core.ModProject;

import javax.swing.JDialog;
import javax.swing.JFrame;
import javax.swing.JMenu;
import javax.swing.JMenuItem;
import javax.swing.JPopupMenu;
import javax.swing.SwingUtilities;
import javax.swing.event.PopupMenuEvent;
import javax.swing.event.PopupMenuListener;
import java.awt.AWTException;
import java.awt.Frame;
import java.awt.GraphicsConfiguration;
import java.awt.GraphicsDevice;
import java.awt.GraphicsEnvironment;
import java.awt.Insets;
import java.awt.Point;
import java.awt.Rectangle;
import java.awt.SystemTray;
import java.awt.Toolkit;
import java.awt.TrayIcon;
import java.awt.Window;
import java.awt.event.MouseAdapter;
import java.awt.event.MouseEvent;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.function.Consumer;

/**
 * System tray icon: restores the window and offers quick access to all projects.
 */
final class TrayManager {
    private final JFrame frame;
    private final Consumer<ModProject> openInIde;
    private final Runnable exit;
    private TrayIcon icon;
    private List<ModProject> projects = List.of();
    private boolean hintShown;
    /** Invisible 1x1 window that owns the Swing popup (a tray icon has no Swing parent). */
    private JDialog anchor;

    TrayManager(JFrame frame, Consumer<ModProject> openInIde, Runnable exit) {
        this.frame = frame;
        this.openInIde = openInIde;
        this.exit = exit;
    }

    static boolean supported() {
        return SystemTray.isSupported();
    }

    boolean installed() {
        return icon != null;
    }

    void install() {
        if (icon != null || !supported()) return;
        icon = new TrayIcon(AppIcon.image(64), "MC Mod Project Manager");
        icon.setImageAutoSize(true);
        icon.addActionListener(e -> restore()); // double-click
        // Swing popup instead of the native AWT one, so the menu follows the FlatLaf theme
        icon.addMouseListener(new MouseAdapter() {
            @Override
            public void mousePressed(MouseEvent e) {
                if (e.isPopupTrigger()) showMenu(e.getPoint());
            }

            @Override
            public void mouseReleased(MouseEvent e) {
                if (e.isPopupTrigger()) showMenu(e.getPoint());
            }
        });
        try {
            SystemTray.getSystemTray().add(icon);
        } catch (AWTException e) {
            icon = null;
        }
    }

    void uninstall() {
        if (icon != null) {
            SystemTray.getSystemTray().remove(icon);
            icon = null;
        }
    }

    void updateProjects(List<ModProject> projects) {
        this.projects = projects;
    }

    void hideToTray() {
        install();
        if (icon == null) return; // tray not available – stay minimized normally
        frame.setVisible(false);
        if (!hintShown) {
            hintShown = true;
            icon.displayMessage("MC Mod Project Manager", "Still running in the system tray.", TrayIcon.MessageType.INFO);
        }
    }

    void restore() {
        SwingUtilities.invokeLater(() -> {
            frame.setVisible(true);
            frame.setExtendedState(Frame.NORMAL);
            frame.toFront();
            frame.requestFocus();
        });
    }

    private void showMenu(Point screen) {
        SwingUtilities.invokeLater(() -> {
            JPopupMenu menu = menu();
            if (anchor == null) {
                anchor = new JDialog();
                anchor.setUndecorated(true);
                anchor.setType(Window.Type.UTILITY);
                anchor.setAlwaysOnTop(true);
                anchor.setSize(1, 1);
            }
            Rectangle area = usableArea(screen);
            java.awt.Dimension size = menu.getPreferredSize();
            int x = Math.max(area.x, Math.min(screen.x, area.x + area.width - size.width));
            int y = screen.y + size.height > area.y + area.height ? screen.y - size.height : screen.y;
            y = Math.max(area.y, y);
            anchor.setLocation(x, y);
            anchor.setVisible(true);
            anchor.toFront();
            menu.addPopupMenuListener(new PopupMenuListener() {
                @Override
                public void popupMenuWillBecomeVisible(PopupMenuEvent e) {
                }

                @Override
                public void popupMenuWillBecomeInvisible(PopupMenuEvent e) {
                    SwingUtilities.invokeLater(() -> anchor.setVisible(false));
                }

                @Override
                public void popupMenuCanceled(PopupMenuEvent e) {
                    SwingUtilities.invokeLater(() -> anchor.setVisible(false));
                }
            });
            menu.show(anchor.getContentPane(), 0, 0);
        });
    }

    /** Screen area (without taskbar) of the monitor the click happened on. */
    private static Rectangle usableArea(Point p) {
        for (GraphicsDevice device : GraphicsEnvironment.getLocalGraphicsEnvironment().getScreenDevices()) {
            GraphicsConfiguration gc = device.getDefaultConfiguration();
            Rectangle bounds = gc.getBounds();
            if (bounds.contains(p)) {
                Insets in = Toolkit.getDefaultToolkit().getScreenInsets(gc);
                return new Rectangle(bounds.x + in.left, bounds.y + in.top,
                        bounds.width - in.left - in.right, bounds.height - in.top - in.bottom);
            }
        }
        return new Rectangle(0, 0, Integer.MAX_VALUE / 2, Integer.MAX_VALUE / 2);
    }

    private JPopupMenu menu() {
        JPopupMenu menu = new JPopupMenu();
        JMenuItem open = new JMenuItem("Open Project Manager");
        open.addActionListener(e -> restore());
        menu.add(open);
        menu.addSeparator();

        Map<String, JMenu> byVersion = new LinkedHashMap<>();
        for (ModProject p : projects) {
            JMenu versionMenu = byVersion.computeIfAbsent(p.mcVersion(), v -> new JMenu("Minecraft " + v));
            JMenuItem item = new JMenuItem(p.name() + (p.branch() != null ? "  [" + p.branch() + "]" : ""));
            item.addActionListener(e -> SwingUtilities.invokeLater(() -> openInIde.accept(p)));
            versionMenu.add(item);
        }
        if (byVersion.isEmpty()) {
            JMenuItem none = new JMenuItem("No projects");
            none.setEnabled(false);
            menu.add(none);
        } else {
            JMenuItem label = new JMenuItem("Open in IntelliJ:");
            label.setEnabled(false);
            menu.add(label);
            byVersion.values().forEach(menu::add);
        }
        menu.addSeparator();
        JMenuItem quit = new JMenuItem("Exit");
        quit.addActionListener(e -> SwingUtilities.invokeLater(() -> {
            restore();
            exit.run();
        }));
        menu.add(quit);
        return menu;
    }
}
