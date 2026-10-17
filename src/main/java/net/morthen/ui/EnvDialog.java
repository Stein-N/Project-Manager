package net.morthen.ui;

import net.morthen.core.ClaudeKeys;
import net.morthen.core.EnvFile;

import javax.swing.BorderFactory;
import javax.swing.DefaultCellEditor;
import javax.swing.JButton;
import javax.swing.JCheckBox;
import javax.swing.JDialog;
import javax.swing.JLabel;
import javax.swing.JMenuItem;
import javax.swing.JOptionPane;
import javax.swing.JPanel;
import javax.swing.JPasswordField;
import javax.swing.JPopupMenu;
import javax.swing.JScrollPane;
import javax.swing.JTable;
import javax.swing.JTextField;
import javax.swing.ListSelectionModel;
import javax.swing.table.AbstractTableModel;
import javax.swing.table.DefaultTableCellRenderer;
import java.awt.BorderLayout;
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
 * Editor for {@code <master>/.env}. Values are hidden unless "Show values" is checked.
 */
final class EnvDialog extends JDialog {
    private static final List<String> PRESETS = java.util.stream.Stream.concat(EnvFile.EXPECTED.stream(),
            java.util.stream.Stream.of(ClaudeKeys.VARIABLE)).toList();

    private final Path master;
    private final Consumer<String> log;
    private final VarModel model = new VarModel();
    private final JTable table = new JTable(model);
    private final JCheckBox showValues = new JCheckBox("Show values");
    private final JTextField plainEditor = new JTextField();
    private final JPasswordField hiddenEditor = new JPasswordField();

    private EnvDialog(Window owner, Path master, Map<String, String> vars, Consumer<String> log) {
        super(owner, "Environment variables (.env)", ModalityType.APPLICATION_MODAL);
        this.master = master;
        this.log = log;
        vars.forEach((k, v) -> model.rows.add(new String[]{k, v}));

        table.setRowHeight(26);
        table.setSelectionMode(ListSelectionModel.SINGLE_SELECTION);
        table.putClientProperty("terminateEditOnFocusLost", true);
        table.getColumnModel().getColumn(0).setPreferredWidth(220);
        table.getColumnModel().getColumn(1).setPreferredWidth(380);
        table.getColumnModel().getColumn(1).setCellRenderer(new DefaultTableCellRenderer() {
            @Override
            public Component getTableCellRendererComponent(JTable t, Object value, boolean selected, boolean focus, int row, int column) {
                String text = value == null ? "" : value.toString();
                String shown = showValues.isSelected() || text.isEmpty() ? text : "•".repeat(Math.min(24, Math.max(8, text.length())));
                return super.getTableCellRendererComponent(t, shown, selected, focus, row, column);
            }
        });
        updateValueEditor();
        showValues.addActionListener(e -> {
            if (table.isEditing()) table.getCellEditor().stopCellEditing();
            updateValueEditor();
            table.repaint();
        });

        JLabel info = new JLabel("<html><body style='width:540px'>Stored as <tt>.env</tt> in the master folder and passed to the "
                + "<b>publish</b> and <b>publishMods</b> tasks – read them in the build script with "
                + "<tt>providers.environmentVariable(\"MODRINTH_API\")</tt>. "
                + "<tt>CLAUDE_API</tt> holds Claude tokens from <tt>claude setup-token</tt> (Pro/Max; API keys <tt>sk-ant-api…</tt> work too), several separated by <tt>;</tt> – the next one is "
                + "used when a token fails. It is not passed to Gradle.</body></html>");
        info.setToolTipText(EnvFile.path(master).toString());
        info.setBorder(BorderFactory.createEmptyBorder(0, 0, 10, 0));

        JButton add = new JButton("Add");
        add.addActionListener(e -> addRow(""));
        JButton presets = new JButton("Add common ▾");
        presets.addActionListener(e -> {
            JPopupMenu menu = new JPopupMenu();
            List<String> missing = PRESETS.stream().filter(p -> model.rows.stream().noneMatch(r -> r[0].equals(p))).toList();
            JMenuItem all = new JMenuItem("Add all missing (" + missing.size() + ")");
            all.setEnabled(!missing.isEmpty());
            all.addActionListener(ev -> missing.forEach(this::addRow));
            menu.add(all);
            menu.addSeparator();
            for (String preset : PRESETS) {
                JMenuItem item = new JMenuItem(preset);
                item.setEnabled(model.rows.stream().noneMatch(r -> r[0].equals(preset)));
                item.addActionListener(ev -> addRow(preset));
                menu.add(item);
            }
            menu.show(presets, 0, presets.getHeight());
        });
        JButton remove = new JButton("Remove");
        remove.addActionListener(e -> {
            int row = table.getSelectedRow();
            if (row < 0) return;
            if (table.isEditing()) table.getCellEditor().cancelCellEditing();
            model.rows.remove(row);
            model.fireTableDataChanged();
        });
        JPanel left = new JPanel(new FlowLayout(FlowLayout.LEFT, 6, 0));
        left.add(add);
        left.add(presets);
        left.add(remove);
        left.add(showValues);

        JButton cancel = new JButton("Cancel");
        cancel.addActionListener(e -> dispose());
        JButton save = Ui.primary("Save");
        save.addActionListener(e -> save());
        JPanel right = new JPanel(new FlowLayout(FlowLayout.RIGHT, 8, 0));
        right.add(cancel);
        right.add(save);

        JPanel bottom = new JPanel(new BorderLayout());
        bottom.setBorder(BorderFactory.createEmptyBorder(10, 0, 0, 0));
        bottom.add(left, BorderLayout.WEST);
        bottom.add(right, BorderLayout.EAST);

        JPanel content = new JPanel(new BorderLayout());
        content.setBorder(BorderFactory.createEmptyBorder(12, 12, 12, 12));
        content.add(info, BorderLayout.NORTH);
        content.add(new JScrollPane(table), BorderLayout.CENTER);
        content.add(bottom, BorderLayout.SOUTH);
        setContentPane(content);
        getRootPane().setDefaultButton(save);
        setSize(new Dimension(680, 420));
        setLocationRelativeTo(owner);
    }

    static void show(Window owner, Path master, Consumer<String> log) {
        Map<String, String> vars;
        try {
            vars = EnvFile.load(master);
        } catch (Exception e) {
            JOptionPane.showMessageDialog(owner, "Could not read .env: " + e.getMessage(), "Environment variables",
                    JOptionPane.ERROR_MESSAGE);
            return;
        }
        new EnvDialog(owner, master, vars, log).setVisible(true);
    }

    private void updateValueEditor() {
        table.getColumnModel().getColumn(1).setCellEditor(new DefaultCellEditor(showValues.isSelected() ? plainEditor : hiddenEditor));
    }

    private void addRow(String name) {
        if (table.isEditing()) table.getCellEditor().stopCellEditing();
        model.rows.add(new String[]{name, ""});
        int row = model.rows.size() - 1;
        model.fireTableRowsInserted(row, row);
        table.setRowSelectionInterval(row, row);
        table.editCellAt(row, name.isEmpty() ? 0 : 1);
        Component editor = table.getEditorComponent();
        if (editor != null) editor.requestFocusInWindow();
    }

    private void save() {
        if (table.isEditing()) table.getCellEditor().stopCellEditing();
        Map<String, String> vars = new LinkedHashMap<>();
        for (String[] row : model.rows) {
            String name = row[0].trim();
            if (name.isEmpty()) continue;
            if (!EnvFile.validName(name)) {
                JOptionPane.showMessageDialog(this, "“" + name + "” is not a valid variable name.\n"
                        + "Use letters, digits and underscores (not starting with a digit).", "Invalid name", JOptionPane.WARNING_MESSAGE);
                return;
            }
            if (vars.containsKey(name)) {
                JOptionPane.showMessageDialog(this, "“" + name + "” is defined twice.", "Duplicate name", JOptionPane.WARNING_MESSAGE);
                return;
            }
            vars.put(name, row[1]);
        }
        try {
            EnvFile.save(master, vars);
            log.accept("Saved " + vars.size() + " environment variable(s) to " + EnvFile.path(master));
            dispose();
        } catch (Exception e) {
            JOptionPane.showMessageDialog(this, "Could not save .env: " + e.getMessage(), "Error", JOptionPane.ERROR_MESSAGE);
        }
    }

    private static final class VarModel extends AbstractTableModel {
        final List<String[]> rows = new ArrayList<>();

        @Override
        public int getRowCount() {
            return rows.size();
        }

        @Override
        public int getColumnCount() {
            return 2;
        }

        @Override
        public String getColumnName(int column) {
            return column == 0 ? "Name" : "Value";
        }

        @Override
        public boolean isCellEditable(int row, int column) {
            return true;
        }

        @Override
        public Object getValueAt(int row, int column) {
            return rows.get(row)[column];
        }

        @Override
        public void setValueAt(Object value, int row, int column) {
            rows.get(row)[column] = value == null ? "" : value.toString();
            fireTableCellUpdated(row, column);
        }
    }
}
