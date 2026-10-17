package net.morthen.ui;

import net.morthen.core.Git;
import net.morthen.core.GitChanges;

import javax.swing.BorderFactory;
import javax.swing.JButton;
import javax.swing.JCheckBox;
import javax.swing.JDialog;
import javax.swing.JLabel;
import javax.swing.JPanel;
import javax.swing.JScrollPane;
import javax.swing.JSplitPane;
import javax.swing.JTable;
import javax.swing.JTextArea;
import javax.swing.JTextPane;
import javax.swing.ListSelectionModel;
import javax.swing.SwingWorker;
import javax.swing.table.AbstractTableModel;
import javax.swing.text.BadLocationException;
import javax.swing.text.SimpleAttributeSet;
import javax.swing.text.StyleConstants;
import javax.swing.text.StyledDocument;
import java.awt.BorderLayout;
import java.awt.Color;
import java.awt.Dimension;
import java.awt.FlowLayout;
import java.awt.Font;
import java.awt.Window;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Optional;

/**
 * Dialog: select changed files, enter a message, commit (and optionally push).
 */
final class CommitDialog extends JDialog {
    record Request(List<GitChanges.Change> all, List<GitChanges.Change> selected, String message, boolean push) {
    }

    private final Git git;
    private final Path repo;
    private final FileModel model = new FileModel();
    private final JTable table = new JTable(model);
    private final JCheckBox allBox = new JCheckBox("All", true);
    private final JTextPane diffPane = new JTextPane();
    private final JTextArea messageArea = new JTextArea(4, 40);
    private final JLabel infoLabel = Ui.muted("Loading changes…");
    private final JButton commitButton = new JButton("Commit");
    private final JButton pushButton = Ui.primary("Commit & Push");
    private Request result;

    private CommitDialog(Window owner, Git git, Path repo, String title) {
        super(owner, "Commit – " + title, ModalityType.APPLICATION_MODAL);
        this.git = git;
        this.repo = repo;

        table.setSelectionMode(ListSelectionModel.SINGLE_SELECTION);
        table.setRowHeight(24);
        table.setShowGrid(false);
        table.getColumnModel().getColumn(0).setMaxWidth(32);
        table.getColumnModel().getColumn(1).setPreferredWidth(80);
        table.getColumnModel().getColumn(1).setMaxWidth(100);
        table.getSelectionModel().addListSelectionListener(e -> {
            if (!e.getValueIsAdjusting()) showDiff();
        });
        model.addTableModelListener(e -> updateButtons());

        diffPane.setEditable(false);
        diffPane.setBackground(javax.swing.UIManager.getColor("TextArea.background"));
        diffPane.setFont(new Font(Font.MONOSPACED, Font.PLAIN, 12));

        JPanel filesPanel = new JPanel(new BorderLayout(0, 4));
        JPanel filesHeader = new JPanel(new BorderLayout());
        filesHeader.add(Ui.heading("Changed files", "h4"), BorderLayout.WEST);
        filesHeader.add(allBox, BorderLayout.EAST);
        filesPanel.add(filesHeader, BorderLayout.NORTH);
        filesPanel.add(new JScrollPane(table), BorderLayout.CENTER);

        JPanel diffPanel = new JPanel(new BorderLayout(0, 4));
        diffPanel.add(Ui.heading("Diff", "h4"), BorderLayout.NORTH);
        diffPanel.add(new JScrollPane(diffPane), BorderLayout.CENTER);

        JSplitPane split = new JSplitPane(JSplitPane.HORIZONTAL_SPLIT, filesPanel, diffPanel);
        split.setDividerLocation(330);
        split.setBorder(null);

        messageArea.setLineWrap(true);
        messageArea.setWrapStyleWord(true);
        messageArea.putClientProperty("JTextArea.placeholderText", "Commit message (first line = summary)");
        JPanel messagePanel = new JPanel(new BorderLayout(0, 4));
        messagePanel.add(Ui.heading("Message", "h4"), BorderLayout.NORTH);
        messagePanel.add(new JScrollPane(messageArea), BorderLayout.CENTER);
        Ui.onChange(messageArea, this::updateButtons);

        JButton cancel = new JButton("Cancel");
        JPanel buttons = new JPanel(new FlowLayout(FlowLayout.RIGHT, 8, 0));
        buttons.add(cancel);
        buttons.add(commitButton);
        buttons.add(pushButton);
        JPanel bottom = new JPanel(new BorderLayout());
        bottom.setBorder(BorderFactory.createEmptyBorder(8, 0, 0, 0));
        bottom.add(infoLabel, BorderLayout.WEST);
        bottom.add(buttons, BorderLayout.EAST);

        JPanel south = new JPanel(new BorderLayout());
        south.setBorder(BorderFactory.createEmptyBorder(10, 0, 0, 0));
        south.add(messagePanel, BorderLayout.CENTER);
        south.add(bottom, BorderLayout.SOUTH);

        JPanel content = new JPanel(new BorderLayout());
        content.setBorder(BorderFactory.createEmptyBorder(12, 12, 12, 12));
        content.add(split, BorderLayout.CENTER);
        content.add(south, BorderLayout.SOUTH);
        setContentPane(content);

        allBox.addActionListener(e -> model.setAll(allBox.isSelected()));
        cancel.addActionListener(e -> dispose());
        commitButton.addActionListener(e -> submit(false));
        pushButton.addActionListener(e -> submit(true));

        updateButtons();
        setSize(new Dimension(880, 580));
        setLocationRelativeTo(owner);
        loadChanges();
    }

    static Optional<Request> show(Window owner, Git git, Path repo, String title) {
        CommitDialog dialog = new CommitDialog(owner, git, repo, title);
        dialog.setVisible(true);
        return Optional.ofNullable(dialog.result);
    }

    private void loadChanges() {
        new SwingWorker<List<GitChanges.Change>, Void>() {
            @Override
            protected List<GitChanges.Change> doInBackground() throws Exception {
                return GitChanges.changes(git, repo);
            }

            @Override
            protected void done() {
                try {
                    model.setChanges(get());
                    infoLabel.setText(model.rows.isEmpty() ? "No changes to commit." : model.rows.size() + " changed file(s)");
                    if (!model.rows.isEmpty()) table.setRowSelectionInterval(0, 0);
                } catch (Exception e) {
                    infoLabel.setText("Error: " + (e.getCause() != null ? e.getCause().getMessage() : e.getMessage()));
                }
                updateButtons();
            }
        }.execute();
    }

    private void showDiff() {
        int row = table.getSelectedRow();
        if (row < 0) return;
        GitChanges.Change change = model.rows.get(row).change;
        new SwingWorker<String, Void>() {
            @Override
            protected String doInBackground() {
                return GitChanges.diff(git, repo, change);
            }

            @Override
            protected void done() {
                try {
                    renderDiff(get());
                } catch (Exception ignored) {
                    // keep previous diff
                }
            }
        }.execute();
    }

    private void renderDiff(String diff) {
        StyledDocument doc = diffPane.getStyledDocument();
        SimpleAttributeSet plain = new SimpleAttributeSet();
        SimpleAttributeSet added = new SimpleAttributeSet();
        StyleConstants.setForeground(added, new Color(0x2E7D32));
        StyleConstants.setBackground(added, new Color(0xE8F5E9));
        SimpleAttributeSet removed = new SimpleAttributeSet();
        StyleConstants.setForeground(removed, new Color(0xC62828));
        StyleConstants.setBackground(removed, new Color(0xFFEBEE));
        SimpleAttributeSet hunk = new SimpleAttributeSet();
        StyleConstants.setForeground(hunk, new Color(0x6A1B9A));
        try {
            doc.remove(0, doc.getLength());
            for (String line : diff.split("\n", -1)) {
                SimpleAttributeSet style = line.startsWith("+++") || line.startsWith("---") ? plain
                        : line.startsWith("+") ? added
                        : line.startsWith("-") ? removed
                        : line.startsWith("@@") ? hunk : plain;
                doc.insertString(doc.getLength(), line + "\n", style);
            }
            diffPane.setCaretPosition(0);
        } catch (BadLocationException ignored) {
            // cannot happen
        }
    }

    private void updateButtons() {
        boolean ok = !model.selected().isEmpty() && !messageArea.getText().isBlank();
        commitButton.setEnabled(ok);
        pushButton.setEnabled(ok);
    }

    private void submit(boolean push) {
        List<GitChanges.Change> selected = model.selected();
        if (selected.isEmpty() || messageArea.getText().isBlank()) return;
        result = new Request(model.rows.stream().map(r -> r.change).toList(), selected, messageArea.getText(), push);
        dispose();
    }

    private static final class Row {
        final GitChanges.Change change;
        boolean checked = true;

        Row(GitChanges.Change change) {
            this.change = change;
        }
    }

    private static final class FileModel extends AbstractTableModel {
        private final List<Row> rows = new ArrayList<>();

        void setChanges(List<GitChanges.Change> changes) {
            rows.clear();
            changes.forEach(c -> rows.add(new Row(c)));
            fireTableDataChanged();
        }

        void setAll(boolean checked) {
            rows.forEach(r -> r.checked = checked);
            fireTableDataChanged();
        }

        List<GitChanges.Change> selected() {
            return rows.stream().filter(r -> r.checked).map(r -> r.change).toList();
        }

        @Override
        public int getRowCount() {
            return rows.size();
        }

        @Override
        public int getColumnCount() {
            return 3;
        }

        @Override
        public String getColumnName(int column) {
            return switch (column) {
                case 0 -> "";
                case 1 -> "Status";
                default -> "File";
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
        public Object getValueAt(int row, int column) {
            Row r = rows.get(row);
            return switch (column) {
                case 0 -> r.checked;
                case 1 -> r.change.label();
                default -> r.change.origPath() != null ? r.change.origPath() + " → " + r.change.path() : r.change.path();
            };
        }

        @Override
        public void setValueAt(Object value, int row, int column) {
            if (column == 0) {
                rows.get(row).checked = Boolean.TRUE.equals(value);
                fireTableRowsUpdated(row, row);
            }
        }
    }
}
