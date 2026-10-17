package net.morthen.ui;

import javax.swing.BorderFactory;
import javax.swing.JButton;
import javax.swing.JFrame;
import javax.swing.JPanel;
import javax.swing.JScrollPane;
import javax.swing.JTextArea;
import java.awt.BorderLayout;
import java.awt.Dimension;
import java.awt.FlowLayout;
import java.awt.GraphicsConfiguration;
import java.awt.Rectangle;
import java.awt.Toolkit;
import java.awt.Window;
import java.awt.datatransfer.StringSelection;

/**
 * Separate window with the log of git, Gradle and Claude. It only hides when closed, so the log keeps
 * collecting lines in the background and shows them again on the next open.
 */
final class LogWindow extends JFrame {
    private static final Dimension SIZE = new Dimension(760, 460);

    private final Window owner;
    private boolean placed;

    LogWindow(Window owner, JTextArea logArea) {
        super("Log – MC Mod Project Manager");
        this.owner = owner;
        setIconImages(AppIcon.images());
        setDefaultCloseOperation(HIDE_ON_CLOSE);

        JButton copy = new JButton("Copy all");
        copy.putClientProperty("JButton.buttonType", "toolBarButton");
        copy.addActionListener(e -> Toolkit.getDefaultToolkit().getSystemClipboard()
                .setContents(new StringSelection(logArea.getText()), null));
        JButton clear = new JButton("Clear", Icons.get(Icons.CLEANUP));
        clear.putClientProperty("JButton.buttonType", "toolBarButton");
        clear.addActionListener(e -> logArea.setText(""));
        JPanel buttons = new JPanel(new FlowLayout(FlowLayout.RIGHT, 4, 0));
        buttons.add(copy);
        buttons.add(clear);

        JPanel header = new JPanel(new BorderLayout());
        header.add(Ui.heading("Log", "h4"), BorderLayout.WEST);
        header.add(buttons, BorderLayout.EAST);

        JPanel content = new JPanel(new BorderLayout(0, 4));
        content.setBorder(BorderFactory.createEmptyBorder(6, 8, 8, 8));
        content.add(header, BorderLayout.NORTH);
        content.add(new JScrollPane(logArea), BorderLayout.CENTER);
        setContentPane(content);
        setSize(SIZE);
    }

    /** Shows the window (next to the main window the first time) and brings it to the front. */
    void open() {
        if (!placed) {
            place();
            placed = true;
        }
        setExtendedState(getExtendedState() & ~ICONIFIED);
        setVisible(true);
        toFront();
    }

    /** Right of the main window if it fits on its screen, otherwise centered on it. */
    private void place() {
        GraphicsConfiguration gc = owner.getGraphicsConfiguration();
        Rectangle screen = gc.getBounds();
        java.awt.Insets insets = Toolkit.getDefaultToolkit().getScreenInsets(gc);
        int right = screen.x + screen.width - insets.right;
        Rectangle main = owner.getBounds();
        if (main.x + main.width + 8 + getWidth() <= right) {
            setLocation(main.x + main.width + 8, main.y);
        } else {
            setLocationRelativeTo(owner);
        }
    }
}
