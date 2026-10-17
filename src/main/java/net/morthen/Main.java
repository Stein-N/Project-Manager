package net.morthen;

import com.formdev.flatlaf.FlatIntelliJLaf;
import net.morthen.core.Autostart;
import net.morthen.core.Config;
import net.morthen.ui.MainFrame;

import javax.swing.SwingUtilities;
import javax.swing.UIManager;
import java.io.IOException;
import java.util.Arrays;

public class Main {
    public static void main(String[] args) {
        Config config = Config.load();
        boolean minimized = Arrays.asList(args).contains(Autostart.MINIMIZED_FLAG);
        syncAutostart(config);
        FlatIntelliJLaf.setup();
        UIManager.put("Tree.rowHeight", 26);
        UIManager.put("Button.arc", 8);
        UIManager.put("Component.arc", 8);
        SwingUtilities.invokeLater(() -> {
            MainFrame frame = new MainFrame(config);
            if (minimized) frame.showMinimized();
            else frame.setVisible(true);
        });
    }

    /** Re-registers the autostart entry so it follows the jar if that was moved or updated. */
    private static void syncAutostart(Config config) {
        if (!config.autostart() || !Autostart.supported()) return;
        try {
            Autostart.apply(true);
        } catch (IOException e) {
            System.err.println("Could not update autostart: " + e.getMessage());
        }
    }
}
