package net.morthen.ui;

import net.morthen.core.GradleCache;
import net.morthen.core.GradleRunner;
import net.morthen.core.ModProject;
import net.morthen.core.SystemActions;

import javax.swing.BorderFactory;
import javax.swing.JButton;
import javax.swing.JCheckBox;
import javax.swing.JDialog;
import javax.swing.JLabel;
import javax.swing.JOptionPane;
import javax.swing.JPanel;
import javax.swing.JScrollPane;
import javax.swing.JTable;
import javax.swing.SwingConstants;
import javax.swing.SwingWorker;
import javax.swing.table.AbstractTableModel;
import javax.swing.table.DefaultTableCellRenderer;
import java.awt.BorderLayout;
import java.awt.Color;
import java.awt.Component;
import java.awt.Dimension;
import java.awt.FlowLayout;
import java.awt.Window;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.function.Consumer;

/**
 * Dialog: Gradle caches and build output with sizes; data no project uses is marked and preselected.
 */
final class CleanupDialog extends JDialog {
    private final List<ModProject> projects;
    private final Consumer<String> log;
    private final GradleRunner gradle;
    private final Path gradleHome = GradleCache.gradleHome();
    private final ItemModel model = new ItemModel();
    private final JTable table = new JTable(model);
    private final JLabel summary = Ui.muted("Scanning…");
    private final JCheckBox unusedOnly = new JCheckBox("Show only unused");
    private final JButton deleteButton = Ui.primary("Delete selected");
    private final JButton stopDaemons = new JButton("Stop Gradle daemons");
    private SwingWorker<?, ?> sizer;

    private CleanupDialog(Window owner, List<ModProject> projects, GradleRunner gradle, Consumer<String> log) {
        super(owner, "Clean up Gradle caches", ModalityType.APPLICATION_MODAL);
        this.projects = projects;
        this.gradle = gradle;
        this.log = log;

        table.setRowHeight(24);
        table.setShowGrid(false);
        table.setAutoCreateRowSorter(false);
        table.getColumnModel().getColumn(0).setMaxWidth(32);
        table.getColumnModel().getColumn(1).setPreferredWidth(170);
        table.getColumnModel().getColumn(2).setPreferredWidth(230);
        table.getColumnModel().getColumn(3).setPreferredWidth(80);
        table.getColumnModel().getColumn(3).setMaxWidth(100);
        table.getColumnModel().getColumn(4).setPreferredWidth(260);
        DefaultTableCellRenderer right = new DefaultTableCellRenderer();
        right.setHorizontalAlignment(SwingConstants.RIGHT);
        table.getColumnModel().getColumn(3).setCellRenderer(right);
        table.getColumnModel().getColumn(4).setCellRenderer(new StatusRenderer());
        model.addTableModelListener(e -> updateSummary());

        JLabel info = new JLabel("<html><b>Unused</b> = not used by any project in the master folder (Gradle version or "
                + "Minecraft version). Other projects on this PC may still need them – deleting only means they are "
                + "downloaded again.<br>Gradle home: " + Ui.html(gradleHome.toString()) + "</html>");
        info.setBorder(BorderFactory.createEmptyBorder(0, 0, 8, 0));

        JPanel top = new JPanel(new BorderLayout());
        top.add(info, BorderLayout.CENTER);
        top.add(unusedOnly, BorderLayout.SOUTH);

        JButton close = new JButton("Close");
        JPanel buttons = new JPanel(new FlowLayout(FlowLayout.RIGHT, 8, 0));
        buttons.add(stopDaemons);
        buttons.add(close);
        buttons.add(deleteButton);
        JPanel bottom = new JPanel(new BorderLayout());
        bottom.setBorder(BorderFactory.createEmptyBorder(8, 0, 0, 0));
        bottom.add(summary, BorderLayout.WEST);
        bottom.add(buttons, BorderLayout.EAST);

        JPanel content = new JPanel(new BorderLayout());
        content.setBorder(BorderFactory.createEmptyBorder(12, 12, 12, 12));
        content.add(top, BorderLayout.NORTH);
        content.add(new JScrollPane(table), BorderLayout.CENTER);
        content.add(bottom, BorderLayout.SOUTH);
        setContentPane(content);

        unusedOnly.addActionListener(e -> model.setFilter(unusedOnly.isSelected()));
        close.addActionListener(e -> dispose());
        deleteButton.addActionListener(e -> deleteSelected());
        stopDaemons.addActionListener(e -> stopDaemons());

        setSize(new Dimension(900, 560));
        setLocationRelativeTo(owner);
        scan();
    }

    static void show(Window owner, List<ModProject> projects, GradleRunner gradle, Consumer<String> log) {
        new CleanupDialog(owner, projects, gradle, log).setVisible(true);
    }

    @Override
    public void dispose() {
        if (sizer != null) sizer.cancel(true);
        super.dispose();
    }

    private void scan() {
        deleteButton.setEnabled(false);
        new SwingWorker<List<GradleCache.Item>, Void>() {
            @Override
            protected List<GradleCache.Item> doInBackground() throws Exception {
                return GradleCache.scan(gradleHome, projects);
            }

            @Override
            protected void done() {
                try {
                    model.setItems(get());
                    computeSizes();
                } catch (Exception e) {
                    summary.setText("Error: " + e.getMessage());
                }
            }
        }.execute();
    }

    /** Sizes are computed one by one in the background and filled in as they arrive. */
    private void computeSizes() {
        List<Row> rows = new ArrayList<>(model.all);
        sizer = new SwingWorker<Void, Row>() {
            @Override
            protected Void doInBackground() {
                for (Row r : rows) {
                    if (isCancelled()) return null;
                    r.size = GradleCache.size(r.item.path());
                    publish(r);
                }
                return null;
            }

            @Override
            protected void process(List<Row> chunks) {
                model.fireTableDataChanged();
            }

            @Override
            protected void done() {
                model.fireTableDataChanged();
                updateSummary();
            }
        };
        sizer.execute();
    }

    private void updateSummary() {
        long selected = 0, unused = 0;
        int pending = 0, count = 0;
        for (Row r : model.all) {
            if (r.size < 0) pending++;
            if (r.checked) {
                count++;
                selected += Math.max(0, r.size);
            }
            if (r.item.status() == GradleCache.Status.UNUSED) unused += Math.max(0, r.size);
        }
        summary.setText("Selected: " + count + " item(s), " + GradleCache.formatSize(selected)
                + "   ·   Unused total: " + GradleCache.formatSize(unused)
                + (pending > 0 ? "   ·   calculating sizes (" + pending + " left)…" : ""));
        deleteButton.setEnabled(count > 0);
    }

    private void deleteSelected() {
        List<Row> selected = model.all.stream().filter(r -> r.checked).toList();
        if (selected.isEmpty()) return;
        long total = selected.stream().mapToLong(r -> Math.max(0, r.size)).sum();
        long used = selected.stream().filter(r -> r.item.status() == GradleCache.Status.USED).count();
        String warning = used > 0 ? "<br><br><font color='#c77800'>" + used + " selected item(s) are used by projects "
                + "and will be downloaded or rebuilt on their next build.</font>" : "";
        if (JOptionPane.showConfirmDialog(this, "<html>Delete " + selected.size() + " item(s) ("
                        + GradleCache.formatSize(total) + ")?" + warning + "</html>",
                "Delete", JOptionPane.YES_NO_OPTION, JOptionPane.WARNING_MESSAGE) != JOptionPane.YES_OPTION) {
            return;
        }
        if (sizer != null) sizer.cancel(true);
        deleteButton.setEnabled(false);
        summary.setText("Deleting…");
        new SwingWorker<List<String>, Void>() {
            @Override
            protected List<String> doInBackground() {
                List<String> failed = new ArrayList<>();
                for (Row r : selected) {
                    try {
                        SystemActions.deleteRecursively(r.item.path());
                        log.accept("Deleted " + r.item.path());
                    } catch (Exception e) {
                        failed.add(r.item.path() + ": " + e.getMessage());
                        log.accept("✖ Could not delete " + r.item.path() + " (" + e.getMessage() + ")");
                    }
                }
                return failed;
            }

            @Override
            protected void done() {
                try {
                    List<String> failed = get();
                    if (!failed.isEmpty()) {
                        JOptionPane.showMessageDialog(CleanupDialog.this, "<html>Some items could not be deleted "
                                        + "(files in use – stop the Gradle daemons and close IntelliJ, then try again):<br><br>"
                                        + Ui.html(String.join("\n", failed.subList(0, Math.min(8, failed.size())))).replace("\n", "<br>")
                                        + "</html>",
                                "Clean up", JOptionPane.WARNING_MESSAGE);
                    }
                } catch (Exception ignored) {
                    // reported via log
                }
                scan();
            }
        }.execute();
    }

    /** Runs "gradlew --stop" once per Gradle version used by the projects. */
    private void stopDaemons() {
        Map<String, ModProject> byVersion = new LinkedHashMap<>();
        for (ModProject p : projects) {
            if (GradleRunner.wrapper(p.path()).isEmpty()) continue;
            net.morthen.core.Dependencies.wrapperVersion(p.path()).ifPresent(v -> byVersion.putIfAbsent(v, p));
        }
        if (byVersion.isEmpty()) {
            JOptionPane.showMessageDialog(this, "No project with a Gradle wrapper found.");
            return;
        }
        stopDaemons.setEnabled(false);
        new SwingWorker<Void, Void>() {
            @Override
            protected Void doInBackground() {
                for (var e : byVersion.entrySet()) {
                    log.accept("> gradlew --stop  (Gradle " + e.getKey() + ")");
                    try {
                        gradle.run(e.getValue().path(), "--stop", log);
                    } catch (Exception ex) {
                        log.accept("✖ " + ex.getMessage());
                    }
                }
                return null;
            }

            @Override
            protected void done() {
                stopDaemons.setEnabled(true);
                summary.setText("Gradle daemons stopped (" + String.join(", ", byVersion.keySet()) + ").");
            }
        }.execute();
    }

    private static final class Row {
        final GradleCache.Item item;
        boolean checked;
        long size = -1;

        Row(GradleCache.Item item) {
            this.item = item;
            this.checked = item.status() == GradleCache.Status.UNUSED;
        }
    }

    private final class ItemModel extends AbstractTableModel {
        private final List<Row> all = new ArrayList<>();
        private List<Row> visible = new ArrayList<>();
        private boolean unusedOnlyFilter;

        void setItems(List<GradleCache.Item> items) {
            all.clear();
            items.forEach(i -> all.add(new Row(i)));
            applyFilter();
        }

        void setFilter(boolean unusedOnly) {
            unusedOnlyFilter = unusedOnly;
            applyFilter();
        }

        private void applyFilter() {
            visible = all.stream().filter(r -> !unusedOnlyFilter || r.item.status() == GradleCache.Status.UNUSED).toList();
            fireTableDataChanged();
        }

        Row row(int index) {
            return visible.get(index);
        }

        @Override
        public int getRowCount() {
            return visible.size();
        }

        @Override
        public int getColumnCount() {
            return 5;
        }

        @Override
        public String getColumnName(int column) {
            return switch (column) {
                case 0 -> "";
                case 1 -> "Category";
                case 2 -> "Item";
                case 3 -> "Size";
                default -> "Status";
            };
        }

        @Override
        public Class<?> getColumnClass(int column) {
            return column == 0 ? Boolean.class : String.class;
        }

        @Override
        public boolean isCellEditable(int row, int column) {
            return column == 0;
        }

        @Override
        public Object getValueAt(int index, int column) {
            Row r = visible.get(index);
            return switch (column) {
                case 0 -> r.checked;
                case 1 -> r.item.category();
                case 2 -> r.item.name();
                case 3 -> r.size < 0 ? "…" : GradleCache.formatSize(r.size);
                default -> switch (r.item.status()) {
                    case UNUSED -> "Unused";
                    case USED -> "Used by " + r.item.usedBy();
                    case SHARED -> "Shared – re-downloaded when needed";
                    case BUILD_OUTPUT -> "Build output – rebuilt by the next build";
                };
            };
        }

        @Override
        public void setValueAt(Object value, int index, int column) {
            if (column == 0) {
                visible.get(index).checked = Boolean.TRUE.equals(value);
                fireTableRowsUpdated(index, index);
            }
        }
    }

    private final class StatusRenderer extends DefaultTableCellRenderer {
        @Override
        public Component getTableCellRendererComponent(JTable t, Object value, boolean selected, boolean focus, int row, int column) {
            super.getTableCellRendererComponent(t, value, selected, focus, row, column);
            Row r = model.row(row);
            setToolTipText(r.item.path().toString());
            if (!selected) {
                setForeground(switch (r.item.status()) {
                    case UNUSED -> new Color(0xD32F2F);
                    case USED -> new Color(0x2E7D32);
                    default -> Color.GRAY;
                });
            }
            setFont(getFont().deriveFont(r.item.status() == GradleCache.Status.UNUSED ? java.awt.Font.BOLD : java.awt.Font.PLAIN));
            return this;
        }
    }
}
