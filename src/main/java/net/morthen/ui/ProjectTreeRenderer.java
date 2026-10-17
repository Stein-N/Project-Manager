package net.morthen.ui;

import net.morthen.core.ModProject;

import javax.swing.JTree;
import javax.swing.UIManager;
import javax.swing.tree.DefaultMutableTreeNode;
import javax.swing.tree.DefaultTreeCellRenderer;
import java.awt.Component;

final class ProjectTreeRenderer extends DefaultTreeCellRenderer {
    @Override
    public Component getTreeCellRendererComponent(JTree tree, Object value, boolean selected, boolean expanded,
                                                  boolean leaf, int row, boolean hasFocus) {
        super.getTreeCellRendererComponent(tree, value, selected, expanded, leaf, row, hasFocus);
        Object obj = value instanceof DefaultMutableTreeNode node ? node.getUserObject() : value;
        String muted = Ui.hex(UIManager.getColor("Label.disabledForeground"));

        if (obj instanceof ModProject p) {
            StringBuilder sb = new StringBuilder("<html>").append(Ui.html(p.name()));
            if (p.git()) {
                sb.append("&nbsp;&nbsp;<font color='").append(selected ? "#d0e4ff" : "#5b9bd5").append("'>⎇ ")
                        .append(Ui.html(p.branch() == null ? "?" : p.branch())).append("</font>");
                if (p.changes() > 0) {
                    sb.append("&nbsp;<font color='#e0a030'>●").append(p.changes()).append("</font>");
                }
                if (p.ahead() > 0) {
                    sb.append("&nbsp;<font color='#4caf50'>↑").append(p.ahead()).append("</font>");
                }
                if (p.behind() > 0) {
                    sb.append("&nbsp;<font color='#e57373'>↓").append(p.behind()).append("</font>");
                }
            } else {
                sb.append("&nbsp;&nbsp;<font color='").append(muted).append("'>(no Git)</font>");
            }
            if (p.versionMismatch()) {
                sb.append("&nbsp;<font color='#e57373'>⚠</font>");
            }
            setText(sb.append("</html>").toString());
            setIcon(UIManager.getIcon("Tree.leafIcon"));
        } else if (obj instanceof VersionEntry v) {
            setText("<html><b>" + Ui.html(v.version()) + "</b>&nbsp;&nbsp;<font color='" + muted + "'>"
                    + v.count() + "</font></html>");
        }
        return this;
    }
}
