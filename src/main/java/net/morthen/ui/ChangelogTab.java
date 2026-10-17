package net.morthen.ui;

import net.morthen.core.Changelog;
import net.morthen.core.ClaudeRunner;
import net.morthen.core.Config;
import net.morthen.core.Git;
import net.morthen.core.ModProject;
import net.morthen.core.Readme;

import javax.swing.BorderFactory;
import javax.swing.JButton;
import javax.swing.JComboBox;
import javax.swing.JEditorPane;
import javax.swing.JLabel;
import javax.swing.JOptionPane;
import javax.swing.JPanel;
import javax.swing.JScrollPane;
import javax.swing.JSplitPane;
import javax.swing.JTabbedPane;
import javax.swing.JTable;
import javax.swing.JTextArea;
import javax.swing.SwingUtilities;
import javax.swing.SwingWorker;
import javax.swing.Timer;
import javax.swing.table.AbstractTableModel;
import java.awt.BorderLayout;
import java.awt.FlowLayout;
import java.awt.Font;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.time.LocalDate;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.Optional;

/**
 * Tab "Changelog": commits since the last version → draft (Claude or fallback) → editor with preview →
 * CHANGELOG.md / CHANGELOG_FULL.md. Nothing is committed here. One instance per project survives the frequent
 * rebuilds of the detail view, so the draft and a running Claude call are kept.
 */
final class ChangelogTab extends JPanel {
    /** What the tab needs from the main window. */
    interface Host {
        Git git();

        Config config();

        /** Log line (EDT). */
        void log(String line);

        void error(String title, String message);

        /** Files of the project were written. */
        void filesChanged(ModProject project);

        void openCommitDialog(ModProject project, String message);
    }

    private final Host host;
    private ModProject project;
    private String version;

    private final JLabel versionLabel = new JLabel();
    private final JComboBox<Changelog.Start> startBox = new JComboBox<>();
    private final JLabel countLabel = Ui.muted("");
    private final CommitModel commits = new CommitModel();
    private final JTable table = new JTable(commits);
    private final JButton claudeButton = Ui.primary("Generate with Claude");
    private final JButton fallbackButton = new JButton("Generate without Claude");
    private final JButton cancelButton = new JButton("Cancel");
    private final JLabel statusLabel = Ui.muted(" ");
    private final JTextArea editor = new JTextArea();
    private final JEditorPane preview = new JEditorPane("text/html", "");
    private final JLabel planLabel = Ui.muted(" ");
    private final JButton saveButton = new JButton("Save");
    private final JButton saveCommitButton = new JButton("Save & open commit dialog");
    private final Timer previewTimer = new Timer(300, e -> renderPreview());
    private final JTabbedPane steps = new JTabbedPane();

    private ClaudeRunner.Run claudeRun;
    private boolean loadingStarts;

    ChangelogTab(Host host, ModProject project) {
        super(new BorderLayout(0, 8));
        this.host = host;
        setBorder(BorderFactory.createEmptyBorder(10, 12, 10, 12));

        // --- top: version and status (always visible)
        JPanel top = new JPanel(new BorderLayout(12, 0));
        top.add(versionLabel, BorderLayout.WEST);
        top.add(statusLabel, BorderLayout.CENTER);
        statusLabel.setHorizontalAlignment(JLabel.RIGHT);

        // --- "Commits": range, table, generate buttons
        JButton reload = new JButton(Icons.get(Icons.REFRESH));
        reload.setToolTipText("Reload commits");
        reload.putClientProperty("JButton.buttonType", "toolBarButton");
        reload.addActionListener(e -> loadStarts());
        startBox.addActionListener(e -> {
            if (!loadingStarts) loadCommits();
        });
        JPanel range = new JPanel(new BorderLayout(6, 0));
        range.add(new JLabel("Since"), BorderLayout.WEST);
        range.add(startBox, BorderLayout.CENTER);
        JPanel rangeEast = new JPanel(new FlowLayout(FlowLayout.RIGHT, 6, 0));
        rangeEast.add(countLabel);
        rangeEast.add(reload);
        range.add(rangeEast, BorderLayout.EAST);

        table.setRowHeight(22);
        table.setShowGrid(false);
        table.getColumnModel().getColumn(0).setMaxWidth(28);
        table.getColumnModel().getColumn(1).setPreferredWidth(64);
        table.getColumnModel().getColumn(1).setMaxWidth(70);
        table.getColumnModel().getColumn(2).setPreferredWidth(60);
        table.getColumnModel().getColumn(2).setMaxWidth(80);
        commits.addTableModelListener(e -> updateCount());

        claudeButton.addActionListener(e -> generateWithClaude());
        fallbackButton.addActionListener(e -> generateFallback());
        cancelButton.addActionListener(e -> {
            if (claudeRun != null) claudeRun.cancel();
        });
        cancelButton.setVisible(false);
        JPanel generate = new JPanel(new FlowLayout(FlowLayout.LEFT, 6, 0));
        generate.add(claudeButton);
        generate.add(fallbackButton);
        generate.add(cancelButton);

        JPanel commitsPanel = new JPanel(new BorderLayout(0, 6));
        commitsPanel.setBorder(BorderFactory.createEmptyBorder(8, 0, 0, 0));
        commitsPanel.add(range, BorderLayout.NORTH);
        commitsPanel.add(new JScrollPane(table), BorderLayout.CENTER);
        commitsPanel.add(generate, BorderLayout.SOUTH);

        // --- "Draft": editor | preview, save buttons
        editor.setFont(new Font(Font.MONOSPACED, Font.PLAIN, 12));
        editor.setLineWrap(true);
        editor.setWrapStyleWord(true);
        editor.putClientProperty("JTextArea.placeholderText", "Generate a draft or write the changelog here");
        previewTimer.setRepeats(false);
        Ui.onChange(editor, () -> {
            previewTimer.restart();
            updateSaveButtons();
        });
        preview.setEditable(false);
        JSplitPane editors = new JSplitPane(JSplitPane.HORIZONTAL_SPLIT, new JScrollPane(editor), new JScrollPane(preview));
        editors.setResizeWeight(0.5);
        editors.setBorder(null);

        saveButton.addActionListener(e -> save(false));
        saveCommitButton.addActionListener(e -> save(true));
        JPanel saveButtons = new JPanel(new FlowLayout(FlowLayout.RIGHT, 6, 0));
        saveButtons.add(saveButton);
        saveButtons.add(saveCommitButton);
        JPanel bottom = new JPanel(new BorderLayout(8, 0));
        bottom.add(planLabel, BorderLayout.CENTER);
        bottom.add(saveButtons, BorderLayout.EAST);

        JPanel draftPanel = new JPanel(new BorderLayout(0, 6));
        draftPanel.setBorder(BorderFactory.createEmptyBorder(8, 0, 0, 0));
        draftPanel.add(editors, BorderLayout.CENTER);
        draftPanel.add(bottom, BorderLayout.SOUTH);

        steps.putClientProperty("JTabbedPane.tabHeight", 26);
        steps.addTab("Commits", commitsPanel);
        steps.addTab("Draft", draftPanel);

        add(top, BorderLayout.NORTH);
        add(steps, BorderLayout.CENTER);

        update(project);
    }

    /** New scan result for the project; reloads when the version changed. */
    void update(ModProject p) {
        boolean first = project == null;
        boolean versionChanged = !first && !java.util.Objects.equals(project.modVersion(), p.modVersion());
        project = p;
        version = Changelog.displayVersion(p.modVersion(), mcVersion());
        versionLabel.setText(version == null
                ? "<html><font color='#d32f2f'>No mod_version in gradle.properties</font></html>"
                : "<html>Version <b>" + Ui.html(version) + "</b> · Minecraft " + Ui.html(mcVersion()) + "</html>");
        if (first) loadExistingChangelog();
        if (first || versionChanged) loadStarts();
        updatePlan();
        updateSaveButtons();
    }

    private String mcVersion() {
        return project.gradleMcVersion() != null && !project.gradleMcVersion().isBlank()
                ? project.gradleMcVersion() : project.mcVersion();
    }

    private String tag() {
        return "[" + project.mcVersion() + "/" + project.name() + "] ";
    }

    // ---------------------------------------------------------------- loading

    /** Starts with CHANGELOG.md when it already holds the current version (editing an existing entry). */
    private void loadExistingChangelog() {
        Path file = project.path().resolve(Changelog.FILE);
        try {
            if (version != null && Files.isRegularFile(file)) {
                String text = Files.readString(file, StandardCharsets.UTF_8);
                if (version.equals(Changelog.detectVersion(text).orElse(null))) {
                    editor.setText(text.replace("\r\n", "\n"));
                    editor.setCaretPosition(0);
                    statusLabel.setText("Loaded the existing CHANGELOG.md");
                }
            }
        } catch (IOException e) {
            statusLabel.setText("Could not read CHANGELOG.md: " + e.getMessage());
        }
        renderPreview();
    }

    private void loadStarts() {
        ModProject p = project;
        String v = version;
        countLabel.setText("Loading…");
        new SwingWorker<List<Changelog.Start>, Void>() {
            @Override
            protected List<Changelog.Start> doInBackground() {
                return Changelog.starts(host.git(), p.path(), p.modVersion(), v);
            }

            @Override
            protected void done() {
                try {
                    loadingStarts = true;
                    startBox.removeAllItems();
                    get().forEach(startBox::addItem);
                    if (startBox.getItemCount() > 0) startBox.setSelectedIndex(0);
                } catch (Exception e) {
                    countLabel.setText("Error: " + rootMessage(e));
                } finally {
                    loadingStarts = false;
                }
                loadCommits();
            }
        }.execute();
    }

    private void loadCommits() {
        Changelog.Start start = (Changelog.Start) startBox.getSelectedItem();
        if (start == null) return;
        ModProject p = project;
        countLabel.setText("Loading…");
        new SwingWorker<List<Changelog.Commit>, Void>() {
            @Override
            protected List<Changelog.Commit> doInBackground() throws Exception {
                return Changelog.commits(host.git(), p.path(), start.sha());
            }

            @Override
            protected void done() {
                try {
                    commits.set(get());
                } catch (Exception e) {
                    commits.set(List.of());
                    countLabel.setText("Error: " + rootMessage(e));
                }
            }
        }.execute();
    }

    private void updateCount() {
        int total = commits.rows.size();
        long selected = commits.rows.stream().filter(r -> r.selected).count();
        countLabel.setText(total + " commit(s)" + (selected < total ? ", " + selected + " selected" : ""));
        steps.setTitleAt(0, "Commits (" + selected + ")");
        updateGenerateButtons();
    }

    // ---------------------------------------------------------------- generating

    private String heading() {
        return Changelog.heading(version, mcVersion(), LocalDate.now());
    }

    private boolean confirmReplaceDraft() {
        return editor.getText().isBlank() || JOptionPane.showConfirmDialog(this,
                "Replace the current text in the editor?", "Changelog",
                JOptionPane.OK_CANCEL_OPTION, JOptionPane.QUESTION_MESSAGE) == JOptionPane.OK_OPTION;
    }

    private void generateFallback() {
        if (version == null || !confirmReplaceDraft()) return;
        setDraft(Changelog.fallback(heading(), commits.selected()));
        statusLabel.setText("Draft from the commit subjects – please review");
    }

    private void generateWithClaude() {
        if (version == null || claudeRun != null) return;
        Config config = host.config();
        Optional<Path> exe = ClaudeRunner.find(config.claudeExecutable());
        if (exe.isEmpty()) {
            host.error("Claude Code CLI not found",
                    "Install Claude Code or set the path in Settings → Claude Code CLI.\nThe draft without Claude is always available.");
            return;
        }
        List<Changelog.Commit> selected = commits.selected();
        if (selected.isEmpty()) {
            host.error("Changelog", "No commits selected.");
            return;
        }
        if (!confirmReplaceDraft()) return;
        String prompt;
        try {
            String claudeMd = readIfExists(project.path().resolve("CLAUDE.md"));
            String name = project.metadata() != null && project.metadata().name() != null
                    ? project.metadata().name() : project.name();
            prompt = Changelog.prompt(name, project.loader(), heading(), selected,
                    Changelog.styleSample(project.path(), 40), Changelog.rulesFromClaudeMd(claudeMd));
        } catch (IOException e) {
            host.error("Changelog", e.getMessage());
            return;
        }
        String tag = tag();
        String v = version;
        host.log("> claude -p (changelog " + v + ", " + selected.size() + " commits, no tools)    (" + project.path() + ")");
        ClaudeRunner.Request request = new ClaudeRunner.Request(project.path(), prompt, ClaudeRunner.Tools.none(),
                config.claudeModel(), config.claudeMaxBudgetUsd(), Duration.ofMinutes(config.claudeTimeoutMinutes()));
        try {
            claudeRun = ClaudeRunner.start(exe.get(), request,
                    line -> SwingUtilities.invokeLater(() -> host.log("    " + tag + line.replace("\n", "\n    " + tag))),
                    outcome -> SwingUtilities.invokeLater(() -> claudeFinished(outcome, v)));
        } catch (IOException e) {
            host.error("Could not start Claude", e.getMessage());
            return;
        }
        statusLabel.setText("Claude is writing the changelog… (output in the log)");
        updateGenerateButtons();
    }

    private void claudeFinished(ClaudeRunner.Outcome outcome, String v) {
        claudeRun = null;
        updateGenerateButtons();
        String cost = String.format(Locale.ROOT, "$%.4f", outcome.costUsd());
        if (!outcome.ok()) {
            host.log("✖ " + tag() + "Claude: " + outcome.status());
            statusLabel.setText("Claude: " + outcome.status().name().toLowerCase(Locale.ROOT));
            if (outcome.status() != ClaudeRunner.Status.CANCELLED) host.error("Claude failed", outcome.text());
            return;
        }
        Optional<String> clean = Changelog.cleanAnswer(outcome.text(), v);
        if (clean.isPresent()) {
            setDraft(clean.get());
            statusLabel.setText("Draft by Claude (" + cost + ") – please review");
            host.log("✔ " + tag() + "Changelog draft by Claude (" + cost + ")");
        } else {
            setDraft(outcome.text());
            statusLabel.setText("<html><font color='#c77800'>Claude's answer does not start with “## " + Ui.html(v)
                    + "” – please fix</font></html>");
            host.log("⚠ " + tag() + "Claude's answer does not start with the version heading");
        }
    }

    private void setDraft(String text) {
        editor.setText(text);
        editor.setCaretPosition(0);
        renderPreview();
        steps.setSelectedIndex(1);
    }

    private void updateGenerateButtons() {
        boolean running = claudeRun != null;
        claudeButton.setEnabled(!running && version != null && !commits.selected().isEmpty());
        fallbackButton.setEnabled(!running && version != null);
        cancelButton.setVisible(running);
    }

    private void renderPreview() {
        preview.setText("<html><head><style>body { font-family: sans-serif; font-size: 11px; margin: 4px 6px; }"
                + " h1 { font-size: 13px; } h2 { font-size: 12px; } h3, h4, h5, h6 { font-size: 11px; }"
                + " h1, h2, h3, h4, h5, h6 { margin: 8px 0 4px 0; } ul { margin: 0 0 0 16px; }</style></head><body>"
                + Readme.markdownToHtml(editor.getText()) + "</body></html>");
        preview.setCaretPosition(0);
    }

    // ---------------------------------------------------------------- saving

    private void updatePlan() {
        if (version == null) {
            planLabel.setText(" ");
            return;
        }
        try {
            Changelog.Plan plan = Changelog.plan(project.path(), version);
            planLabel.setText(switch (plan.action()) {
                case CREATE -> "CHANGELOG.md will be created";
                case REPLACE_SAME_VERSION -> "Replaces " + version + " in CHANGELOG.md";
                case MOVE_OLD_VERSION -> plan.oldVersion() + " moves to CHANGELOG_FULL.md";
                case OLD_VERSION_ALREADY_IN_FULL -> plan.oldVersion() + " is already in CHANGELOG_FULL.md";
                case UNKNOWN_CONTENT -> "CHANGELOG.md has no version heading";
            });
            planLabel.setToolTipText(planLabel.getText());
        } catch (IOException e) {
            planLabel.setText("Could not read CHANGELOG.md: " + e.getMessage());
        }
    }

    private void updateSaveButtons() {
        boolean ok = version != null && !editor.getText().isBlank();
        saveButton.setEnabled(ok);
        saveCommitButton.setEnabled(ok);
    }

    private void save(boolean commit) {
        if (version == null) return;
        String text = editor.getText();
        String detected = Changelog.detectVersion(text).orElse(null);
        if (!version.equals(detected) && JOptionPane.showConfirmDialog(this,
                "<html>The first “## ” heading should carry the version <b>" + Ui.html(version) + "</b>"
                        + (detected == null ? "" : " (found: " + Ui.html(detected) + ")")
                        + ".<br>Otherwise the next run cannot recognize it. Save anyway?</html>",
                "Changelog", JOptionPane.OK_CANCEL_OPTION, JOptionPane.WARNING_MESSAGE) != JOptionPane.OK_OPTION) {
            return;
        }
        boolean moveUnknown = false;
        try {
            Changelog.Plan plan = Changelog.plan(project.path(), version);
            if (plan.action() == Changelog.Action.REPLACE_SAME_VERSION && JOptionPane.showConfirmDialog(this,
                    "CHANGELOG.md already contains " + version + ". Overwrite it?", "Changelog",
                    JOptionPane.OK_CANCEL_OPTION, JOptionPane.QUESTION_MESSAGE) != JOptionPane.OK_OPTION) {
                return;
            }
            if (plan.action() == Changelog.Action.UNKNOWN_CONTENT) {
                Object[] options = {"Move to CHANGELOG_FULL.md", "Overwrite", "Cancel"};
                int choice = JOptionPane.showOptionDialog(this,
                        "CHANGELOG.md has content without a recognizable version heading.\nWhat should happen with it?",
                        "Changelog", JOptionPane.DEFAULT_OPTION, JOptionPane.QUESTION_MESSAGE, null, options, options[0]);
                if (choice != 0 && choice != 1) return;
                moveUnknown = choice == 0;
            }
            Changelog.Plan done = Changelog.save(project.path(), version, text, moveUnknown);
            host.log("✔ " + tag() + "Saved CHANGELOG.md for " + version
                    + (done.action() == Changelog.Action.MOVE_OLD_VERSION || moveUnknown
                    ? " (previous entry moved to CHANGELOG_FULL.md)" : ""));
        } catch (IOException e) {
            host.error("Could not save the changelog", e.getMessage());
            return;
        }
        statusLabel.setText("Saved – not committed");
        updatePlan();
        host.filesChanged(project);
        if (commit) host.openCommitDialog(project, "docs: changelog for " + version);
    }

    // ---------------------------------------------------------------- helpers

    private static String readIfExists(Path file) throws IOException {
        return Files.isRegularFile(file) ? Files.readString(file, StandardCharsets.UTF_8) : null;
    }

    private static String rootMessage(Exception e) {
        Throwable t = e.getCause() != null ? e.getCause() : e;
        return String.valueOf(t.getMessage());
    }

    private static final class Row {
        final Changelog.Commit commit;
        boolean selected;

        Row(Changelog.Commit commit) {
            this.commit = commit;
            this.selected = !commit.hiddenByDefault();
        }
    }

    private static final class CommitModel extends AbstractTableModel {
        private static final String[] COLUMNS = {"", "Commit", "Type", "Subject"};
        final List<Row> rows = new ArrayList<>();

        void set(List<Changelog.Commit> list) {
            rows.clear();
            list.forEach(c -> rows.add(new Row(c)));
            fireTableDataChanged();
        }

        List<Changelog.Commit> selected() {
            return rows.stream().filter(r -> r.selected).map(r -> r.commit).toList();
        }

        @Override
        public int getRowCount() {
            return rows.size();
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
            var m = r.commit.message();
            return switch (column) {
                case 0 -> r.selected;
                case 1 -> r.commit.shortSha();
                case 2 -> m.type() == null ? (r.commit.merge() ? "merge" : "") : m.type() + (m.breaking() ? "!" : "");
                default -> m.subject() + (m.isBackport() ? "  (backport)" : "");
            };
        }

        @Override
        public void setValueAt(Object value, int row, int column) {
            if (column == 0) {
                rows.get(row).selected = Boolean.TRUE.equals(value);
                fireTableRowsUpdated(row, row);
            }
        }
    }
}
