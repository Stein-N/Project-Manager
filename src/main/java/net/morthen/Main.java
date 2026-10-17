package net.morthen;

import com.formdev.flatlaf.FlatDarkLaf;
import com.formdev.flatlaf.FlatLightLaf;
import net.morthen.core.Config;
import net.morthen.ui.MainFrame;

import javax.swing.SwingUtilities;
import javax.swing.UIManager;

public class Main {
    public static void main(String[] args) {
        Config config = Config.load();
        if (config.isDarkMode()) {
            FlatDarkLaf.setup();
        } else {
            FlatLightLaf.setup();
        }
        UIManager.put("Tree.rowHeight", 26);
        UIManager.put("Button.arc", 8);
        UIManager.put("Component.arc", 8);
        SwingUtilities.invokeLater(() -> new MainFrame(config).setVisible(true));
    }
}
