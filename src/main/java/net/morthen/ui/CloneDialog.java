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
 * Dialog: Git-Repository in {@code <master>/<version>/<name>} mit gewählter Branch klonen.
 */
final class CloneDialog extends JDialog {
    record Request(String url, String branch, String version, String name, boolean worktree) {
    }

    private final Git git;
    private final Path master;
    private final JTextField urlField = new JTextField(36);
    private final JButton loadButton = new JButton("Branches laden");
    private final JComboBox<String> branchBox = new JComboBox<>();
    private final JComboBox<String> versionBox;
    private final JTextField nameField = new JTextField();
    private final JLabel targetLabel = Ui.muted(" ");
    private final JLabel infoLabel = Ui.muted(" ");
    private final JButton okButton = Ui.primary("Klonen");
    private final JCheckBox worktreeBox = new JCheckBox("Als Worktree anlegen (ein gemeinsames Repository pro Mod unter .repos)", true);
    private final JLabel repoLabel = Ui.muted(" ");

    private boolean nameEdited;
    private boolean settingName;
    private String defaultBranch;
    private Request result;

    private CloneDialog(Window owner, Git git, Path master, List<String> versions,
                        String presetUrl, String presetName, String presetVersion) {
        super(owner, "Projekt klonen", ModalityType.APPLICATION_MODAL);
        this.git = git;
        this.master = master;

        versionBox = new JComboBox<>(versions.toArray(String[]::new));
        versionBox.setEditable(true);
        branchBox.setEditable(true);
        urlField.putClientProperty("JTextField.placeholderText", "https://github.com/user/mod.git");
        versionBox.setToolTipText("Bestehender Versionsordner oder neue Version eintippen");

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
                .add("Minecraft-Version", versionBox)
                .add("Branch", branchBox)
                .add("Projektname", nameField)
                .add("", worktreeBox)
                .add("", repoLabel)
                .addFull(targetLabel)
                .addFull(infoLabel);
        form.setBorder(BorderFactory.createEmptyBorder(16, 16, 8, 16));

        JButton cancel = new JButton("Abbrechen");
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
        worktreeBox.setToolTipText("Alle Versionen einer Mod teilen sich Git-Objekte und Branches – Commits in einer Version sind sofort in allen sichtbar.");
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
        infoLabel.setText("Lade Branches…");
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
                    infoLabel.setText(branches.branches().size() + " Branches gefunden"
                            + (defaultBranch != null ? " · Standard: " + defaultBranch : ""));
                    if (current != null && branches.branches().contains(current.toString())) {
                        branchBox.setSelectedItem(current);
                    }
                    suggestBranch();
                } catch (Exception e) {
                    String msg = e.getCause() != null ? e.getCause().getMessage() : e.getMessage();
                    infoLabel.setForeground(UIManager.getColor("Component.error.focusedBorderColor"));
                    infoLabel.setText("<html>Fehler: " + Ui.html(lastLine(msg)) + "</html>");
                }
                pack();
            }
        }.execute();
    }

    /** Wählt die Branch, die am besten zur Minecraft-Version passt (exakt > beginnt mit > enthält). */
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
                    ? "Repository " + repo.getFileName() + " existiert bereits – es wird nur ein neuer Worktree angelegt."
                    : "Neues gemeinsames Repository: " + repo);
        }
        if (target == null) {
            targetLabel.setText("Ziel: –");
            okButton.setEnabled(false);
        } else if (Files.exists(target)) {
            targetLabel.setText("<html>Ziel: " + Ui.html(target.toString()) + " <font color='#e57373'>(existiert bereits)</font></html>");
            okButton.setEnabled(false);
        } else {
            targetLabel.setText("Ziel: " + target);
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
            JOptionPane.showMessageDialog(this, "Bitte eine Branch wählen bzw. eintragen.",
                    "Branch fehlt", JOptionPane.WARNING_MESSAGE);
            return;
        }
        result = new Request(url, branchName, selectedVersion(), nameField.getText().trim(), worktreeBox.isSelected());
        dispose();
    }

    private static String lastLine(String text) {
        if (text == null) return "unbekannt";
        String[] lines = text.strip().split("\n");
        return lines[lines.length - 1];
    }
}
