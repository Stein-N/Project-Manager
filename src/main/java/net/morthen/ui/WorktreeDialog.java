package net.morthen.ui;

import net.morthen.core.BranchMatcher;
import net.morthen.core.Git;
import net.morthen.core.Worktrees;

import javax.swing.BorderFactory;
import javax.swing.DefaultListCellRenderer;
import javax.swing.JButton;
import javax.swing.JCheckBox;
import javax.swing.JComboBox;
import javax.swing.JDialog;
import javax.swing.JLabel;
import javax.swing.JList;
import javax.swing.JPanel;
import javax.swing.JTextField;
import javax.swing.SwingWorker;
import javax.swing.UIManager;
import javax.swing.text.JTextComponent;
import java.awt.BorderLayout;
import java.awt.Component;
import java.awt.FlowLayout;
import java.awt.Window;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.Map;
import java.util.Optional;

/**
 * Dialog: create another worktree (= another Minecraft version) for a mod.
 */
final class WorktreeDialog extends JDialog {
    /** @param newBranch null = check out existing {@code branch}, otherwise a new branch based on {@code branch} */
    record Request(String version, String name, String branch, String newBranch) {
    }

    private record Loaded(List<String> branches, Map<String, Path> busy, String fetchError) {
    }

    private final Path master;
    private final String baseBranch;
    private final JComboBox<String> versionBox;
    private final JComboBox<String> branchBox = new JComboBox<>();
    private final JLabel branchLabel = new JLabel("Branch");
    {
        branchLabel.setForeground(UIManager.getColor("Label.disabledForeground"));
    }
    private final JCheckBox newBranchBox = new JCheckBox("Create new branch:");
    private final JTextField newBranchField = new JTextField(16);
    private final JTextField nameField;
    private final JLabel targetLabel = Ui.muted(" ");
    private final JLabel infoLabel = Ui.muted("Updating branches (fetch) …");
    private final JButton okButton = Ui.primary("Create worktree");

    private List<String> branches = List.of();
    private Map<String, Path> busy = Map.of();
    private boolean newBranchNameEdited;
    private boolean updating;
    private Request result;

    private WorktreeDialog(Window owner, Git git, Path gitDir, Path master, String modName,
                           String currentBranch, List<String> versions, String presetVersion) {
        super(owner, "Add version – " + modName, ModalityType.APPLICATION_MODAL);
        this.master = master;
        this.baseBranch = currentBranch;

        versionBox = new JComboBox<>(versions.toArray(String[]::new));
        versionBox.setEditable(true);
        versionBox.setSelectedItem(presetVersion == null ? "" : presetVersion);
        nameField = new JTextField(modName, 24);
        branchBox.setEnabled(false);
        newBranchField.setEnabled(false);
        branchBox.setRenderer(new DefaultListCellRenderer() {
            @Override
            public Component getListCellRendererComponent(JList<?> list, Object value, int index,
                                                          boolean selected, boolean focus) {
                super.getListCellRendererComponent(list, value, index, selected, focus);
                if (value != null && busy.containsKey(value.toString()) && !newBranchBox.isSelected()) {
                    setText(value + "   — in use: " + shortPath(busy.get(value.toString())));
                    if (!selected) setForeground(UIManager.getColor("Label.disabledForeground"));
                }
                return this;
            }
        });

        JPanel newBranchRow = new JPanel(new BorderLayout(8, 0));
        newBranchRow.add(newBranchBox, BorderLayout.WEST);
        newBranchRow.add(newBranchField, BorderLayout.CENTER);

        Ui.Form form = new Ui.Form()
                .add("Minecraft version", versionBox)
                .add("", newBranchRow)
                .add(branchLabel, branchBox, null)
                .add("Folder name", nameField)
                .addFull(targetLabel)
                .addFull(infoLabel);
        form.setBorder(BorderFactory.createEmptyBorder(16, 16, 8, 16));

        JLabel hint = Ui.muted("<html>A worktree uses the same repository – commits and branches are visible in all versions "
                + "right away.<br>Each branch can only be checked out in one worktree at a time.</html>");
        hint.setBorder(BorderFactory.createEmptyBorder(0, 16, 0, 16));

        JButton cancel = new JButton("Cancel");
        JPanel buttons = new JPanel(new FlowLayout(FlowLayout.RIGHT, 8, 0));
        buttons.setBorder(BorderFactory.createEmptyBorder(12, 16, 16, 16));
        buttons.add(cancel);
        buttons.add(okButton);

        JPanel south = new JPanel(new BorderLayout());
        south.add(hint, BorderLayout.NORTH);
        south.add(buttons, BorderLayout.SOUTH);

        getContentPane().setLayout(new BorderLayout());
        getContentPane().add(form, BorderLayout.CENTER);
        getContentPane().add(south, BorderLayout.SOUTH);
        getRootPane().setDefaultButton(okButton);

        cancel.addActionListener(e -> dispose());
        okButton.addActionListener(e -> submit());
        versionBox.addActionListener(e -> versionChanged());
        if (versionBox.getEditor().getEditorComponent() instanceof JTextComponent editor) {
            Ui.onChange(editor, this::validateInput);
        }
        branchBox.addActionListener(e -> validateInput());
        newBranchBox.addActionListener(e -> {
            newBranchField.setEnabled(newBranchBox.isSelected());
            branchLabel.setText(newBranchBox.isSelected() ? "Base branch" : "Branch");
            validateInput();
        });
        Ui.onChange(newBranchField, () -> {
            if (!updating) newBranchNameEdited = true;
            validateInput();
        });
        Ui.onChange(nameField, this::validateInput);

        okButton.setEnabled(false);
        updateTargetLabel();
        pack();
        setSize(Math.max(getWidth(), 620), getHeight() + 20);
        setLocationRelativeTo(owner);

        new SwingWorker<Loaded, Void>() {
            @Override
            protected Loaded doInBackground() {
                String error = null;
                if (!git.run(gitDir, "config", "--get", "remote.origin.url").output().isBlank()) {
                    Git.Result fetch = git.run(gitDir, "fetch", "origin", "--prune");
                    if (!fetch.ok()) error = fetch.output();
                }
                return new Loaded(git.branches(gitDir), Worktrees.checkedOutBranches(git, gitDir), error);
            }

            @Override
            protected void done() {
                try {
                    Loaded loaded = get();
                    branches = loaded.branches();
                    busy = loaded.busy();
                    branchBox.removeAllItems();
                    branches.forEach(branchBox::addItem);
                    branchBox.setEnabled(true);
                    infoLabel.setText(loaded.fetchError() == null
                            ? branches.size() + " branches · " + busy.size() + " checked out"
                            : "Fetch failed – showing local state");
                    versionChanged();
                } catch (Exception e) {
                    infoLabel.setText("Error: " + e.getMessage());
                }
            }
        }.execute();
    }

    static Optional<Request> show(Window owner, Git git, Path gitDir, Path master, String modName,
                                  String currentBranch, List<String> versions, String presetVersion) {
        WorktreeDialog dialog = new WorktreeDialog(owner, git, gitDir, master, modName, currentBranch, versions, presetVersion);
        dialog.setVisible(true);
        return Optional.ofNullable(dialog.result);
    }

    /** Pick a matching branch – if none is free, suggest a new branch (name = version). */
    private void versionChanged() {
        if (branches.isEmpty()) {
            validateInput();
            return;
        }
        String version = version();
        Optional<String> match = BranchMatcher.bestMatch(branches, version);
        updating = true;
        if (match.isPresent() && !busy.containsKey(match.get())) {
            newBranchBox.setSelected(false);
            branchBox.setSelectedItem(match.get());
        } else if (!version.isEmpty()) {
            newBranchBox.setSelected(true);
            if (!newBranchNameEdited) newBranchField.setText(version);
            branchBox.setSelectedItem(baseBranch != null && branches.contains(baseBranch) ? baseBranch : branches.getFirst());
        }
        newBranchField.setEnabled(newBranchBox.isSelected());
        branchLabel.setText(newBranchBox.isSelected() ? "Base branch" : "Branch");
        updating = false;
        validateInput();
    }

    private void validateInput() {
        updateTargetLabel();
        String problem = null;
        Path target = target();
        Object branch = branchBox.getSelectedItem();
        if (branches.isEmpty()) {
            problem = "";
        } else if (target == null) {
            problem = "Enter version and folder name.";
        } else if (Files.exists(target)) {
            problem = "The target folder already exists.";
        } else if (branch == null) {
            problem = "Select a branch.";
        } else if (newBranchBox.isSelected()) {
            String nb = newBranchField.getText().trim();
            if (nb.isEmpty()) problem = "Enter a name for the new branch.";
            else if (branches.contains(nb)) problem = "Branch “" + nb + "” already exists – uncheck the box and select it directly.";
        } else if (busy.containsKey(branch.toString())) {
            problem = "“" + branch + "” is already checked out in " + shortPath(busy.get(branch.toString()))
                    + " – create a new branch or pick another one.";
        }
        okButton.setEnabled(problem == null);
        if (problem != null && !problem.isEmpty()) {
            infoLabel.setForeground(new java.awt.Color(0xD32F2F));
            infoLabel.setText(problem);
        } else if (!branches.isEmpty()) {
            infoLabel.setForeground(UIManager.getColor("Label.disabledForeground"));
            infoLabel.setText(newBranchBox.isSelected()
                    ? "New branch “" + newBranchField.getText().trim() + "” will be created from “" + branch + "” (no upstream – set it on first push)."
                    : "Branch “" + branch + "” will be checked out.");
        }
    }

    private void updateTargetLabel() {
        Path target = target();
        targetLabel.setText("Target: " + (target == null ? "–" : target.toString()));
    }

    private String version() {
        Object v = versionBox.getEditor().getItem();
        return v == null ? "" : v.toString().trim();
    }

    private Path target() {
        String v = version();
        String n = nameField.getText().trim();
        if (v.isEmpty() || n.isEmpty()) return null;
        try {
            return master.resolve(v).resolve(n);
        } catch (Exception e) {
            return null;
        }
    }

    private String shortPath(Path path) {
        try {
            return Worktrees.real(master).relativize(Worktrees.real(path)).toString();
        } catch (IllegalArgumentException e) {
            return path.toString();
        }
    }

    private void submit() {
        if (!okButton.isEnabled()) return;
        String nb = newBranchBox.isSelected() ? newBranchField.getText().trim() : null;
        result = new Request(version(), nameField.getText().trim(), String.valueOf(branchBox.getSelectedItem()), nb);
        dispose();
    }
}
