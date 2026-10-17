package net.morthen.ui;

import net.morthen.core.ModProject;

import javax.swing.BorderFactory;
import javax.swing.Icon;
import javax.swing.JLabel;
import javax.swing.JPanel;
import javax.swing.JTree;
import javax.swing.UIManager;
import javax.swing.tree.DefaultMutableTreeNode;
import javax.swing.tree.DefaultTreeCellRenderer;
import javax.swing.tree.TreeCellRenderer;
import java.awt.Color;
import java.awt.Component;
import java.awt.FlowLayout;
import java.nio.file.Path;
import java.util.function.Predicate;

/**
 * Project rows are built from separate labels so real (SVG) icons can be shown for the branch, a running
 * Gradle task and a version mismatch; other nodes use the default renderer.
 */
final class ProjectTreeRenderer extends JPanel implements TreeCellRenderer {
    private final Predicate<Path> gradleRunning;
    private final DefaultTreeCellRenderer fallback = new DefaultTreeCellRenderer();
    private final JLabel name = new JLabel();
    private final JLabel branch = new JLabel();
    private final JLabel counters = new JLabel();
    private final JLabel running = new JLabel(Icons.get(Icons.RUN));
    private final JLabel warning = new JLabel(Icons.get(Icons.WARNING));

    ProjectTreeRenderer(Predicate<Path> gradleRunning) {
        super(new FlowLayout(FlowLayout.LEFT, 0, 0));
        this.gradleRunning = gradleRunning;
        setBorder(BorderFactory.createEmptyBorder(2, 2, 2, 6));
        name.setIconTextGap(6);
        branch.setIconTextGap(3);
        branch.setBorder(BorderFactory.createEmptyBorder(0, 10, 0, 0));
        counters.setBorder(BorderFactory.createEmptyBorder(0, 6, 0, 0));
        running.setBorder(BorderFactory.createEmptyBorder(0, 6, 0, 0));
        warning.setBorder(BorderFactory.createEmptyBorder(0, 4, 0, 0));
        add(name);
        add(branch);
        add(counters);
        add(running);
        add(warning);
    }

    @Override
    public Component getTreeCellRendererComponent(JTree tree, Object value, boolean selected, boolean expanded,
                                                  boolean leaf, int row, boolean hasFocus) {
        Object obj = value instanceof DefaultMutableTreeNode node ? node.getUserObject() : value;
        if (!(obj instanceof ModProject p)) {
            fallback.getTreeCellRendererComponent(tree, value, selected, expanded, leaf, row, hasFocus);
            if (obj instanceof VersionEntry v) {
                fallback.setText("<html><b>" + Ui.html(v.version()) + "</b>&nbsp;&nbsp;<font color='"
                        + Ui.hex(UIManager.getColor("Label.disabledForeground")) + "'>" + v.count() + "</font></html>");
            }
            return fallback;
        }

        // on the blue (focused) selection everything is drawn in the selection text color
        boolean onAccent = selected && tree.hasFocus();
        Color text = onAccent ? UIManager.getColor("Tree.selectionForeground") : UIManager.getColor("Tree.textForeground");
        Color muted = onAccent ? new Color(0xDDE8F5) : UIManager.getColor("Label.disabledForeground");
        setOpaque(selected);
        setBackground(selected ? UIManager.getColor(onAccent ? "Tree.selectionBackground" : "Tree.selectionInactiveBackground")
                : tree.getBackground());

        Icon modIcon = p.metadata() != null ? Ui.modIcon(p.metadata().icon(), 16) : null;
        name.setIcon(modIcon != null ? modIcon : UIManager.getIcon("Tree.leafIcon"));
        name.setText(p.name());
        name.setForeground(text);

        if (p.git()) {
            branch.setIcon(Icons.get(Icons.BRANCH));
            branch.setText(p.branch() == null ? "?" : p.branch());
            branch.setForeground(onAccent ? new Color(0xDDE8F5) : new Color(0x3D7CC9));
        } else {
            branch.setIcon(null);
            branch.setText("(no Git)");
            branch.setForeground(muted);
        }

        String orange = onAccent ? "#ffffff" : "#c77800";
        String green = onAccent ? "#ffffff" : "#2e7d32";
        String red = onAccent ? "#ffffff" : "#d32f2f";
        StringBuilder sb = new StringBuilder();
        if (p.git() && p.changes() > 0) sb.append("<font color='").append(orange).append("'>●").append(p.changes()).append("</font>&nbsp;");
        if (p.git() && p.ahead() > 0) sb.append("<font color='").append(green).append("'>↑").append(p.ahead()).append("</font>&nbsp;");
        if (p.git() && p.behind() > 0) sb.append("<font color='").append(red).append("'>↓").append(p.behind()).append("</font>");
        counters.setVisible(!sb.isEmpty());
        counters.setText(sb.isEmpty() ? "" : "<html>" + sb + "</html>");

        running.setVisible(gradleRunning.test(p.path()));
        warning.setVisible(p.versionMismatch());
        return this;
    }
}
