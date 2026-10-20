package net.morthen.ui;

import net.morthen.core.GithubRepos;

import javax.swing.BorderFactory;
import javax.swing.JButton;
import javax.swing.JCheckBox;
import javax.swing.JComboBox;
import javax.swing.JDialog;
import javax.swing.JLabel;
import javax.swing.JPanel;
import javax.swing.JScrollPane;
import javax.swing.JTable;
import javax.swing.JTextField;
import javax.swing.ListSelectionModel;
import javax.swing.RowFilter;
import javax.swing.SwingWorker;
import javax.swing.UIManager;
import javax.swing.table.AbstractTableModel;
import javax.swing.table.TableRowSorter;
import java.awt.BorderLayout;
import java.awt.Dimension;
import java.awt.FlowLayout;
import java.awt.Window;
import java.awt.event.MouseAdapter;
import java.awt.event.MouseEvent;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.Optional;
import java.util.TreeSet;

/**
 * Picks a repository from the GitHub account and its organizations, Minecraft mods only by default.
 */
final class GithubRepoDialog extends JDialog {
    private static final String ALL_OWNERS = "All owners";

    private final String token;
    private final RepoModel model = new RepoModel();
    private final JTable table = new JTable(model);
    private final TableRowSorter<RepoModel> sorter = new TableRowSorter<>(model);
    private final JTextField search = new JTextField(20);
    private final JCheckBox modsOnly = new JCheckBox("Minecraft mods only", true);
    private final JCheckBox hideForks = new JCheckBox("Hide forks and archived");
    private final JComboBox<String> ownerBox = new JComboBox<>(new String[]{ALL_OWNERS});
    private final JLabel status = Ui.muted(" ");
    private final JButton select = Ui.primary("Select");

    private GithubRepos.Repo result;

    private GithubRepoDialog(Window owner, String token) {
        super(owner, "GitHub repositories", ModalityType.APPLICATION_MODAL);
        this.token = token;

        table.setRowSorter(sorter);
        table.setRowHeight(24);
        table.setSelectionMode(ListSelectionModel.SINGLE_SELECTION);
        table.getColumnModel().getColumn(0).setPreferredWidth(240);
        table.getColumnModel().getColumn(1).setPreferredWidth(380);
        table.getColumnModel().getColumn(2).setPreferredWidth(80);
        table.getColumnModel().getColumn(3).setPreferredWidth(90);
        sorter.setSortKeys(List.of(new javax.swing.RowSorter.SortKey(3, javax.swing.SortOrder.DESCENDING)));
        search.putClientProperty("JTextField.placeholderText", "Search name or description");

        JPanel filters = new JPanel(new FlowLayout(FlowLayout.LEFT, 8, 0));
        filters.setBorder(BorderFactory.createEmptyBorder(0, 0, 8, 0));
        filters.add(search);
        filters.add(ownerBox);
        filters.add(modsOnly);
        filters.add(hideForks);

        JButton cancel = new JButton("Cancel");
        JButton reload = new JButton("Reload");
        JPanel buttons = new JPanel(new FlowLayout(FlowLayout.RIGHT, 8, 0));
        buttons.add(reload);
        buttons.add(cancel);
        buttons.add(select);
        JPanel bottom = new JPanel(new BorderLayout());
        bottom.setBorder(BorderFactory.createEmptyBorder(8, 0, 0, 0));
        bottom.add(status, BorderLayout.CENTER);
        bottom.add(buttons, BorderLayout.EAST);

        JPanel content = new JPanel(new BorderLayout());
        content.setBorder(BorderFactory.createEmptyBorder(12, 12, 12, 12));
        content.add(filters, BorderLayout.NORTH);
        content.add(new JScrollPane(table), BorderLayout.CENTER);
        content.add(bottom, BorderLayout.SOUTH);
        setContentPane(content);
        getRootPane().setDefaultButton(select);
        setSize(new Dimension(900, 520));
        setLocationRelativeTo(owner);

        select.setEnabled(false);
        table.getSelectionModel().addListSelectionListener(e -> select.setEnabled(table.getSelectedRow() >= 0));
        table.addMouseListener(new MouseAdapter() {
            @Override
            public void mouseClicked(MouseEvent e) {
                if (e.getClickCount() == 2) choose();
            }
        });
        select.addActionListener(e -> choose());
        cancel.addActionListener(e -> dispose());
        reload.addActionListener(e -> load());
        Ui.onChange(search, this::applyFilter);
        modsOnly.addActionListener(e -> applyFilter());
        hideForks.addActionListener(e -> applyFilter());
        ownerBox.addActionListener(e -> applyFilter());

        load();
    }

    /** @return the chosen repository, empty if cancelled */
    static Optional<GithubRepos.Repo> show(Window owner, String token) {
        GithubRepoDialog dialog = new GithubRepoDialog(owner, token);
        dialog.setVisible(true);
        return Optional.ofNullable(dialog.result);
    }

    private void load() {
        status.setForeground(UIManager.getColor("Label.disabledForeground"));
        status.setText("Loading repositories from GitHub …");
        new SwingWorker<List<GithubRepos.Repo>, Void>() {
            @Override
            protected List<GithubRepos.Repo> doInBackground() throws Exception {
                return GithubRepos.fetchAll(token);
            }

            @Override
            protected void done() {
                try {
                    model.set(get());
                    Object current = ownerBox.getSelectedItem();
                    ownerBox.removeAllItems();
                    ownerBox.addItem(ALL_OWNERS);
                    new TreeSet<>(model.repos.stream().map(GithubRepos.Repo::owner).toList()).forEach(ownerBox::addItem);
                    if (current != null) ownerBox.setSelectedItem(current);
                    applyFilter();
                } catch (Exception e) {
                    String msg = e.getCause() != null ? e.getCause().getMessage() : e.getMessage();
                    status.setForeground(UIManager.getColor("Component.error.focusedBorderColor"));
                    status.setText("Error: " + msg);
                }
            }
        }.execute();
    }

    private void applyFilter() {
        String text = search.getText().trim().toLowerCase(Locale.ROOT);
        Object owner = ownerBox.getSelectedItem();
        sorter.setRowFilter(new RowFilter<>() {
            @Override
            public boolean include(Entry<? extends RepoModel, ? extends Integer> entry) {
                GithubRepos.Repo repo = model.repos.get(entry.getIdentifier());
                if (modsOnly.isSelected() && !repo.mod()) return false;
                if (hideForks.isSelected() && (repo.fork() || repo.archived())) return false;
                if (owner != null && !ALL_OWNERS.equals(owner) && !owner.equals(repo.owner())) return false;
                return text.isEmpty() || repo.fullName().toLowerCase(Locale.ROOT).contains(text)
                        || (repo.description() != null && repo.description().toLowerCase(Locale.ROOT).contains(text));
            }
        });
        if (!model.repos.isEmpty()) {
            status.setForeground(UIManager.getColor("Label.disabledForeground"));
            status.setText(table.getRowCount() + " of " + model.repos.size() + " repositories");
        }
    }

    private void choose() {
        int row = table.getSelectedRow();
        if (row < 0) return;
        result = model.repos.get(table.convertRowIndexToModel(row));
        dispose();
    }

    private static final class RepoModel extends AbstractTableModel {
        private static final String[] COLUMNS = {"Repository", "Description", "Visibility", "Last push"};
        final List<GithubRepos.Repo> repos = new ArrayList<>();

        void set(List<GithubRepos.Repo> list) {
            repos.clear();
            repos.addAll(list);
            fireTableDataChanged();
        }

        @Override
        public int getRowCount() {
            return repos.size();
        }

        @Override
        public int getColumnCount() {
            return COLUMNS.length;
        }

        @Override
        public String getColumnName(int column) {
            return COLUMNS[column];
        }

        @Override
        public Object getValueAt(int row, int column) {
            GithubRepos.Repo r = repos.get(row);
            return switch (column) {
                case 0 -> r.fullName() + (r.fork() ? " (fork)" : "") + (r.archived() ? " (archived)" : "");
                case 1 -> r.description() == null ? "" : r.description();
                case 2 -> r.isPrivate() ? "private" : "public";
                default -> r.pushedAt() == null ? "" : r.pushedAt().substring(0, Math.min(10, r.pushedAt().length()));
            };
        }
    }
}
