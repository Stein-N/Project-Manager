package net.morthen;

import com.formdev.flatlaf.FlatIntelliJLaf;
import net.morthen.core.Autostart;
import net.morthen.core.Config;
import net.morthen.core.SingleInstance;
import net.morthen.core.UpdateChecker;
import net.morthen.ui.MainFrame;

import javax.swing.SwingUtilities;
import javax.swing.UIManager;
import java.io.IOException;
import java.nio.file.Path;
import java.util.Arrays;
import java.util.Optional;

public class Main {
    public static void main(String[] args) {
        int apply = Arrays.asList(args).indexOf(UpdateChecker.APPLY_FLAG);
        if (apply >= 0 && apply + 1 < args.length) { // update step: replace the old jar with this one, start it, end
            try {
                UpdateChecker.applyPending(Path.of(args[apply + 1]));
            } catch (IOException e) {
                System.err.println("Update failed: " + e.getMessage());
            }
            return;
        }
        boolean minimized = Arrays.asList(args).contains(Autostart.MINIMIZED_FLAG);
        // Already running: a normal start shows that window, a start with the system just ends
        Optional<SingleInstance> instance = SingleInstance.acquire(Config.dir(), !minimized);
        if (instance.isEmpty()) return;
        UpdateChecker.cleanUpPending();
        Config config = Config.load();
        syncAutostart(config);
        FlatIntelliJLaf.setup();
        UIManager.put("Tree.rowHeight", 26);
        UIManager.put("Button.arc", 8);
        UIManager.put("Component.arc", 8);
        SwingUtilities.invokeLater(() -> {
            MainFrame frame = new MainFrame(config, instance.get());
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
