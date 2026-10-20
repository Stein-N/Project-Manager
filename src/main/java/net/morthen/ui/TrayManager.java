package net.morthen.ui;

import net.morthen.core.ModProject;
import net.morthen.core.SystemActions;

import javax.swing.JFrame;
import javax.swing.SwingUtilities;
import java.awt.AWTException;
import java.awt.Frame;
import java.awt.Menu;
import java.awt.MenuItem;
import java.awt.PopupMenu;
import java.awt.SystemTray;
import java.awt.TrayIcon;
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

    TrayManager(JFrame frame, Consumer<ModProject> openInIde, Runnable exit) {
        this.frame = frame;
        this.openInIde = openInIde;
        this.exit = exit;
    }

    /**
     * Windows only: on Linux the AWT tray icon is an old XEmbed icon that e.g. KDE on Wayland only shows through a proxy,
     * and clicks on it never reach Java – the window could not be brought back.
     */
    static boolean supported() {
        return SystemActions.isWindows() && SystemTray.isSupported();
    }

    boolean installed() {
        return icon != null;
    }

    void install() {
        if (icon != null || !supported()) return;
        icon = new TrayIcon(AppIcon.image(64), "MC Mod Project Manager");
        icon.setImageAutoSize(true);
        icon.addActionListener(e -> restore()); // double-click
        icon.setPopupMenu(menu());
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
        if (icon != null) icon.setPopupMenu(menu());
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
            // the window was hidden while iconified: without NORMAL first it can come back as an icon only
            frame.setExtendedState(Frame.NORMAL);
            frame.setVisible(true);
            frame.setExtendedState(Frame.NORMAL);
            frame.toFront();
            frame.requestFocus();
        });
    }

    private PopupMenu menu() {
        PopupMenu menu = new PopupMenu();
        MenuItem open = new MenuItem("Open Project Manager");
        open.addActionListener(e -> restore());
        menu.add(open);
        menu.addSeparator();

        Map<String, Menu> byVersion = new LinkedHashMap<>();
        for (ModProject p : projects) {
            Menu versionMenu = byVersion.computeIfAbsent(p.mcVersion(), v -> new Menu("Minecraft " + v));
            MenuItem item = new MenuItem(p.name() + (p.branch() != null ? "  [" + p.branch() + "]" : ""));
            item.addActionListener(e -> SwingUtilities.invokeLater(() -> openInIde.accept(p)));
            versionMenu.add(item);
        }
        if (byVersion.isEmpty()) {
            MenuItem none = new MenuItem("No projects");
            none.setEnabled(false);
            menu.add(none);
        } else {
            MenuItem label = new MenuItem("Open in IntelliJ:");
            label.setEnabled(false);
            menu.add(label);
            byVersion.values().forEach(menu::add);
        }
        menu.addSeparator();
        MenuItem quit = new MenuItem("Exit");
        quit.addActionListener(e -> SwingUtilities.invokeLater(() -> {
            restore();
            exit.run();
        }));
        menu.add(quit);
        return menu;
    }
}
