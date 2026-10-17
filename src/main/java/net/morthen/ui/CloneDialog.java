package net.morthen.ui;

import net.morthen.core.BranchMatcher;
import net.morthen.core.Git;
import net.morthen.core.Worktrees;

import javax.swing.BorderFactory;
import javax.swing.JButton;
import javax.swing.JCheckBox;
import javax.swing.JComboBox;
import javax.swing.JDialog;
import javax.swing.JLabel;
import javax.swing.JOptionPane;
import javax.swing.JPanel;
import javax.swing.JTextField;
import javax.swing.SwingWorker;
import javax.swing.UIManager;
import javax.swing.text.JTextComponent;
import java.awt.BorderLayout;
import java.awt.FlowLayout;
import java.awt.Window;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.Optional;

/**
 * Dialog: clone a Git repository into {@code <master>/<version>/<name>} with the chosen branch.
 */
final class CloneDialog extends JDialog {
    record Request(String url, String branch, String version, String name, boolean worktree) {
    }

    private final Git git;
    private final Path master;
    private final JTextField urlField = new JTextField(36);
    private final JButton loadButton = new JButton("Load branches");
    private final JComboBox<String> branchBox = new JComboBox<>();
    private final JComboBox<String> versionBox;
    private final JTextField nameField = new JTextField();
    private final JLabel targetLabel = Ui.muted(" ");
    private final JLabel infoLabel = Ui.muted(" ");
    private final JButton okButton = Ui.primary("Clone");
    private final JCheckBox worktreeBox = new JCheckBox("Create as worktree (one shared repository per mod in .repos)", true);
    private final JLabel repoLabel = Ui.muted(" ");

    private boolean nameEdited;
    private boolean settingName;
    private String defaultBranch;
    private Request result;

    private CloneDialog(Window owner, Git git, Path master, List<String> versions,
                        String presetUrl, String presetName, String presetVersion) {
        super(owner, "Clone project", ModalityType.APPLICATION_MODAL);
        this.git = git;
        this.master = master;

        versionBox = new JComboBox<>(versions.toArray(String[]::new));
        versionBox.setEditable(true);
        branchBox.setEditable(true);
        urlField.putClientProperty("JTextField.placeholderText", "https://github.com/user/mod.git");
        versionBox.setToolTipText("Existing version folder or type a new version");

        if (presetVersion != null) {
            versionBox.setSelectedItem(presetVersion);
        }
        if (presetName != null) {
            setProjectName(presetName);
            nameEdited = true;
        }
        if (presetUrl != null) {
            urlField.setText(presetUrl);
        }

        Ui.Form form = new Ui.Form()
                .add("Git-URL", urlField, loadButton)
                .add("Minecraft version", versionBox)
                .add("Branch", branchBox)
                .add("Project name", nameField)
                .add("", worktreeBox)
                .add("", repoLabel)
                .addFull(targetLabel)
                .addFull(infoLabel);
        form.setBorder(BorderFactory.createEmptyBorder(16, 16, 8, 16));

        JButton cancel = new JButton("Cancel");
        JPanel buttons = new JPanel(new FlowLayout(FlowLayout.RIGHT, 8, 0));
        buttons.setBorder(BorderFactory.createEmptyBorder(8, 16, 16, 16));
        buttons.add(cancel);
        buttons.add(okButton);

        getContentPane().setLayout(new BorderLayout());
        getContentPane().add(form, BorderLayout.CENTER);
        getContentPane().add(buttons, BorderLayout.SOUTH);
        getRootPane().setDefaultButton(okButton);

        loadButton.addActionListener(e -> loadBranches());
        cancel.addActionListener(e -> dispose());
        okButton.addActionListener(e -> submit());
        worktreeBox.addActionListener(e -> updateTarget());
        worktreeBox.setToolTipText("All versions of a mod share Git objects and branches – commits in one version are visible in all of them right away.");
        Ui.onChange(urlField, this::urlChanged);
        Ui.onChange(nameField, () -> {
            if (!settingName) nameEdited = true;
            updateTarget();
        });
        versionBox.addActionListener(e -> {
            suggestBranch();
            updateTarget();
        });
        if (versionBox.getEditor().getEditorComponent() instanceof JTextComponent editor) {
            Ui.onChange(editor, this::updateTarget);
        }

        updateTarget();
        pack();
        setMinimumSize(getSize());
        setLocationRelativeTo(owner);

        if (presetUrl != null && !presetUrl.isBlank()) {
            loadBranches();
        }
    }

    static Optional<Request> show(Window owner, Git git, Path master, List<String> versions,
                                  String presetUrl, String presetName, String presetVersion) {
        CloneDialog dialog = new CloneDialog(owner, git, master, versions, presetUrl, presetName, presetVersion);
        dialog.setVisible(true);
        return Optional.ofNullable(dialog.result);
    }

    private void urlChanged() {
        if (!nameEdited) {
            String url = urlField.getText().trim().replaceAll("[/\\\\]+$", "");
            String name = url.substring(Math.max(url.lastIndexOf('/'), Math.max(url.lastIndexOf('\\'), url.lastIndexOf(':'))) + 1);
            setProjectName(name.endsWith(".git") ? name.substring(0, name.length() - 4) : name);
        }
    }

    private void setProjectName(String name) {
        settingName = true;
        nameField.setText(name);
        settingName = false;
    }

    private void loadBranches() {
        String url = urlField.getText().trim();
        if (url.isEmpty()) {
            return;
        }
        loadButton.setEnabled(false);
        infoLabel.setForeground(UIManager.getColor("Label.disabledForeground"));
        infoLabel.setText("Loading branches…");
        new SwingWorker<Git.RemoteBranches, Void>() {
            @Override
            protected Git.RemoteBranches doInBackground() throws Exception {
                return git.remoteBranches(url);
            }

            @Override
            protected void done() {
                loadButton.setEnabled(true);
                try {
                    Git.RemoteBranches branches = get();
                    Object current = branchBox.getSelectedItem();
                    branchBox.removeAllItems();
                    branches.branches().forEach(branchBox::addItem);
                    defaultBranch = branches.defaultBranch();
                    infoLabel.setText(branches.branches().size() + " branches found"
                            + (defaultBranch != null ? " · default: " + defaultBranch : ""));
                    if (current != null && branches.branches().contains(current.toString())) {
                        branchBox.setSelectedItem(current);
                    }
                    suggestBranch();
                } catch (Exception e) {
                    String msg = e.getCause() != null ? e.getCause().getMessage() : e.getMessage();
                    infoLabel.setForeground(UIManager.getColor("Component.error.focusedBorderColor"));
                    infoLabel.setText("<html>Error: " + Ui.html(lastLine(msg)) + "</html>");
                }
                pack();
            }
        }.execute();
    }

    /** Selects the branch that best matches the Minecraft version (exact > starts with > contains). */
    private void suggestBranch() {
        String version = selectedVersion();
        if (version.isEmpty() || branchBox.getItemCount() == 0) {
            return;
        }
        List<String> all = new java.util.ArrayList<>();
        for (int i = 0; i < branchBox.getItemCount(); i++) all.add(branchBox.getItemAt(i));
        String pick = BranchMatcher.bestMatch(all, version).orElse(defaultBranch);
        if (pick != null) {
            branchBox.setSelectedItem(pick);
        }
    }

    private String selectedVersion() {
        Object editorValue = versionBox.getEditor().getItem();
        return editorValue == null ? "" : editorValue.toString().trim();
    }

    private Path target() {
        String version = selectedVersion();
        String name = nameField.getText().trim();
        if (version.isEmpty() || name.isEmpty()) {
            return null;
        }
        try {
            return master.resolve(version).resolve(name);
        } catch (Exception e) {
            return null;
        }
    }

    private void updateTarget() {
        Path target = target();
        String name = nameField.getText().trim();
        if (!worktreeBox.isSelected() || name.isEmpty()) {
            repoLabel.setText(" ");
        } else {
            Path repo = Worktrees.sharedRepo(master, name);
            repoLabel.setText(Files.isDirectory(repo)
                    ? "Repository " + repo.getFileName() + " already exists – only a new worktree will be created."
                    : "New shared repository: " + repo);
        }
        if (target == null) {
            targetLabel.setText("Target: –");
            okButton.setEnabled(false);
        } else if (Files.exists(target)) {
            targetLabel.setText("<html>Target: " + Ui.html(target.toString()) + " <font color='#d32f2f'>(already exists)</font></html>");
            okButton.setEnabled(false);
        } else {
            targetLabel.setText("Target: " + target);
            okButton.setEnabled(true);
        }
    }

    private void submit() {
        String url = urlField.getText().trim();
        Object branch = branchBox.getEditor().getItem();
        String branchName = branch == null ? "" : branch.toString().trim();
        if (url.isEmpty() || target() == null) {
            return;
        }
        if (branchName.isEmpty()) {
            JOptionPane.showMessageDialog(this, "Please select or enter a branch.",
                    "Branch missing", JOptionPane.WARNING_MESSAGE);
            return;
        }
        result = new Request(url, branchName, selectedVersion(), nameField.getText().trim(), worktreeBox.isSelected());
        dispose();
    }

    private static String lastLine(String text) {
        if (text == null) return "unknown";
        String[] lines = text.strip().split("\n");
        return lines[lines.length - 1];
    }
}
