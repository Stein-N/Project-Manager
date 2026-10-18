package net.morthen.ui;

import net.morthen.core.Backport;
import net.morthen.core.BackportClaude;
import net.morthen.core.Changelog;
import net.morthen.core.ClaudeRunner;
import net.morthen.core.Config;
import net.morthen.core.Git;
import net.morthen.core.GradleRunner;
import net.morthen.core.ModProject;
import net.morthen.core.Readme;
import net.morthen.core.SystemActions;
import net.morthen.core.Tags;

import javax.swing.BorderFactory;
import javax.swing.DefaultListCellRenderer;
import javax.swing.DefaultListModel;
import javax.swing.JButton;
import javax.swing.JCheckBox;
import javax.swing.JComboBox;
import javax.swing.JEditorPane;
import javax.swing.JLabel;
import javax.swing.JList;
import javax.swing.JOptionPane;
import javax.swing.JPanel;
import javax.swing.JScrollPane;
import javax.swing.JSplitPane;
import javax.swing.JTabbedPane;
import javax.swing.JTable;
import javax.swing.JTextPane;
import javax.swing.ListSelectionModel;
import javax.swing.SwingUtilities;
import javax.swing.SwingWorker;
import javax.swing.table.AbstractTableModel;
import javax.swing.table.DefaultTableCellRenderer;
import java.awt.BorderLayout;
import java.awt.Component;
import java.awt.FlowLayout;
import java.awt.Font;
import java.awt.GridLayout;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Optional;
import java.util.Set;
import java.util.concurrent.Callable;
import java.util.concurrent.CompletableFuture;
import java.util.function.Consumer;

/**
 * Tab "Backport" of an older Minecraft version: ports commits from another version of the mod (usually main) without
 * creating commits. Work happens in a hidden worktree; conflicts and build errors can be handed to Claude (restricted
 * tools, see {@link BackportClaude}); the result is applied here as uncommitted changes. One instance per project
 * survives the rebuilds of the detail view, so a backport in progress is kept.
 */
final class BackportTab extends JPanel {
    interface Host {
        Git git();

        Config config();

        GradleRunner gradle();

        Optional<Path> master();

        /** The other worktrees of the same repository. */
        List<ModProject> siblings(ModProject project);

        /** Main branch of the repository or null. */
        String mainBranch(ModProject project);

        /** Claude tokens from CLAUDE_API; empty = login of the Claude Code CLI. */
        List<String> claudeApiKeys();

        /** Log line (EDT). */
        void log(String line);

        void error(String title, String message);

        void filesChanged(ModProject project);

        void openCommitDialog(ModProject project, String message);

        void openInIde(Path folder, String name);
    }

    /** Upper limit for the original commit in a prompt. */
    private static final int MAX_COMMIT_CHARS = 200_000;
    /** Lines of the build output Claude gets (the errors are at the end). */
    private static final int BUILD_TAIL_LINES = 250;

    private final Host host;
    private ModProject project;

    // --- commits
    private final JComboBox<ModProject> sourceBox = new JComboBox<>();
    private final JLabel typesLabel = Ui.muted(" ");
    private final CandidateModel candidates = new CandidateModel();
    private final JTable table = new JTable(candidates);
    private final JLabel countLabel = Ui.muted(" ");
    /** Last release tag of the source; only commits after it are offered (null = no tag yet). */
    private String sinceTag;
    private final JCheckBox syncBox = new JCheckBox("Take over version and CHANGELOG.md", true);
    private final JCheckBox claudeBox = new JCheckBox("Resolve conflicts with Claude");
    private final JButton portButton = Ui.primary("Port selected");

    // --- result
    private final JTabbedPane steps = new JTabbedPane();
    private final JLabel statusLabel = new JLabel(" ");
    private final JPanel conflictPanel = new JPanel(new BorderLayout(8, 6));
    private final JLabel conflictLabel = new JLabel();
    private final DefaultListModel<Backport.FileChange> fileModel = new DefaultListModel<>();
    private final JList<Backport.FileChange> fileList = new JList<>(fileModel);
    private final JTextPane diffPane = new JTextPane();
    private final JButton buildButton = new JButton("Run build", Icons.get(Icons.BUILD));
    private final JButton fixBuildButton = new JButton("Fix build with Claude");
    private final JButton discardButton = new JButton("Discard");
    private final JButton applyButton = Ui.primary("Apply");
    private final JButton continueButton = Ui.primary("Continue");
    private final JButton resolveButton = new JButton("Resolve with Claude");
    private final JButton skipButton = new JButton("Skip this commit");
    private final JButton cancelClaudeButton = new JButton("Cancel Claude", Icons.get(Icons.STOP));
    private final JEditorPane notesPane = new JEditorPane("text/html", "");

    // --- state of the current backport (EDT only, unless volatile)
    private boolean busy;
    private boolean filling;
    private Path workDir;
    private String baseSha;
    private volatile String baseShaAsync;
    private ModProject source;
    private List<Changelog.Commit> queue = List.of();
    private final List<Changelog.Commit> ported = new ArrayList<>();
    /** Markdown shown in "Notes": what the tool and Claude did, per commit. */
    private final List<String> notes = new ArrayList<>();
    private Changelog.Commit conflictCommit;
    private List<String> conflictFiles = List.of();
    private String conflictCheckpoint;
    private String versionNote;
    private boolean buildFailed;
    private final List<String> buildLines = Collections.synchronizedList(new ArrayList<>());
    private volatile ClaudeRunner.Run claudeRun;
    private volatile boolean cancelled;

    BackportTab(Host host, ModProject project) {
        super(new BorderLayout(0, 8));
        this.host = host;
        setBorder(BorderFactory.createEmptyBorder(10, 12, 10, 12));

        // --- top: source and rules
        sourceBox.setRenderer(new DefaultListCellRenderer() {
            @Override
            public Component getListCellRendererComponent(JList<?> list, Object value, int index, boolean selected, boolean focus) {
                String text = value instanceof ModProject p ? p.mcVersion() + "  (" + p.branch() + ")" : "–";
                return super.getListCellRendererComponent(list, text, index, selected, focus);
            }
        });
        sourceBox.addActionListener(e -> {
            if (!filling) loadCandidates();
        });
        JButton reload = new JButton(Icons.get(Icons.REFRESH));
        reload.setToolTipText("Reload commits");
        reload.putClientProperty("JButton.buttonType", "toolBarButton");
        reload.addActionListener(e -> loadCandidates());
        JButton editTypes = new JButton("Types…");
        editTypes.putClientProperty("JButton.buttonType", "toolBarButton");
        editTypes.setToolTipText("Commit types that are ported into this version");
        editTypes.addActionListener(e -> editTypes());
        JPanel sourceRow = new JPanel(new BorderLayout(6, 0));
        sourceRow.add(new JLabel("From"), BorderLayout.WEST);
        sourceRow.add(sourceBox, BorderLayout.CENTER);
        JPanel sourceEast = new JPanel(new FlowLayout(FlowLayout.RIGHT, 4, 0));
        sourceEast.add(typesLabel);
        sourceEast.add(editTypes);
        sourceEast.add(reload);
        sourceRow.add(sourceEast, BorderLayout.EAST);

        // --- "Commits"
        table.setRowHeight(22);
        table.setShowGrid(false);
        table.getColumnModel().getColumn(0).setMaxWidth(28);
        table.getColumnModel().getColumn(1).setPreferredWidth(64);
        table.getColumnModel().getColumn(1).setMaxWidth(70);
        table.getColumnModel().getColumn(2).setPreferredWidth(50);
        table.getColumnModel().getColumn(2).setMaxWidth(70);
        table.getColumnModel().getColumn(4).setPreferredWidth(120);
        table.getColumnModel().getColumn(4).setMaxWidth(170);
        DefaultTableCellRenderer muted = new DefaultTableCellRenderer() {
            @Override
            public Component getTableCellRendererComponent(JTable t, Object v, boolean sel, boolean focus, int row, int col) {
                Component c = super.getTableCellRendererComponent(t, v, sel, focus, row, col);
                if (!sel) c.setForeground(candidates.rows.get(row).candidate.state() == Backport.State.PORT
                        ? t.getForeground() : javax.swing.UIManager.getColor("Label.disabledForeground"));
                return c;
            }
        };
        for (int i = 1; i < 5; i++) table.getColumnModel().getColumn(i).setCellRenderer(muted);
        candidates.addTableModelListener(e -> updateButtons());
        portButton.addActionListener(e -> portSelected());
        syncBox.setToolTipText("Sets the mod part of the version to the one of the source (the Minecraft part stays) "
                + "and takes over its CHANGELOG.md; the older entry moves to CHANGELOG_FULL.md");
        claudeBox.setToolTipText("Every conflict goes to Claude (backport model in the settings); the run only stops "
                + "when Claude cannot resolve it or skips a commit");
        JButton all = new JButton("Select portable");
        all.setToolTipText("Select every commit that is not filtered, marked or already ported");
        all.putClientProperty("JButton.buttonType", "toolBarButton");
        all.addActionListener(e -> candidates.selectPortable(true));
        JButton none = new JButton("None");
        none.putClientProperty("JButton.buttonType", "toolBarButton");
        none.addActionListener(e -> candidates.selectPortable(false));
        JPanel selectRow = new JPanel(new BorderLayout(8, 0));
        JPanel selectButtons = new JPanel(new FlowLayout(FlowLayout.LEFT, 0, 0));
        selectButtons.add(all);
        selectButtons.add(none);
        selectRow.add(selectButtons, BorderLayout.WEST);
        selectRow.add(countLabel, BorderLayout.EAST);
        JPanel options = new JPanel(new GridLayout(2, 1, 0, 0));
        options.add(syncBox);
        options.add(claudeBox);
        JPanel portRow = new JPanel(new BorderLayout(10, 0));
        JPanel portButtonPanel = new JPanel(new java.awt.GridBagLayout()); // vertically centered
        portButtonPanel.add(portButton);
        portRow.add(portButtonPanel, BorderLayout.WEST);
        portRow.add(options, BorderLayout.CENTER);
        JPanel commitsPanel = new JPanel(new BorderLayout(0, 6));
        commitsPanel.setBorder(BorderFactory.createEmptyBorder(8, 0, 0, 0));
        commitsPanel.add(selectRow, BorderLayout.NORTH);
        commitsPanel.add(new JScrollPane(table), BorderLayout.CENTER);
        commitsPanel.add(portRow, BorderLayout.SOUTH);

        // --- "Result"
        JButton openIde = new JButton("IntelliJ", Icons.get(Icons.INTELLIJ));
        openIde.setToolTipText("Open the work folder in IntelliJ");
        openIde.addActionListener(e -> {
            if (workDir != null) host.openInIde(workDir, workDir.getFileName().toString());
        });
        JButton openExplorer = new JButton("Explorer", Icons.get(Icons.FOLDER));
        openExplorer.addActionListener(e -> {
            try {
                if (workDir != null) SystemActions.openExplorer(workDir);
            } catch (Exception ex) {
                host.error("Backport", ex.getMessage());
            }
        });
        continueButton.setToolTipText("The conflict is resolved in the work folder – go on with the next commit");
        continueButton.addActionListener(e -> continueAfterConflict());
        resolveButton.setToolTipText("Claude resolves the conflict in the work folder (restricted tools, nothing is committed)");
        resolveButton.addActionListener(e -> resolveWithClaude());
        skipButton.setToolTipText("Leave this commit out and go on with the next one");
        skipButton.addActionListener(e -> skipCommit());
        JPanel conflictButtons = new JPanel(new FlowLayout(FlowLayout.LEFT, 6, 0));
        conflictButtons.add(openIde);
        conflictButtons.add(openExplorer);
        conflictButtons.add(resolveButton);
        conflictButtons.add(skipButton);
        conflictButtons.add(continueButton);
        conflictPanel.add(conflictLabel, BorderLayout.CENTER);
        conflictPanel.add(conflictButtons, BorderLayout.SOUTH);
        conflictPanel.setBorder(BorderFactory.createCompoundBorder(
                BorderFactory.createMatteBorder(1, 1, 1, 1, new java.awt.Color(0xE0A800)),
                BorderFactory.createEmptyBorder(6, 8, 6, 8)));
        conflictPanel.setVisible(false);

        fileList.setSelectionMode(ListSelectionModel.SINGLE_SELECTION);
        fileList.setCellRenderer(new DefaultListCellRenderer() {
            @Override
            public Component getListCellRendererComponent(JList<?> list, Object value, int index, boolean selected, boolean focus) {
                String text = value instanceof Backport.FileChange f ? f.status() + "  " + f.path() : String.valueOf(value);
                return super.getListCellRendererComponent(list, text, index, selected, focus);
            }
        });
        fileList.addListSelectionListener(e -> {
            if (!e.getValueIsAdjusting()) showDiff();
        });
        diffPane.setEditable(false);
        diffPane.setFont(new Font(Font.MONOSPACED, Font.PLAIN, 12));
        JPanel noWrap = new JPanel(new BorderLayout()); // a text pane inside a panel scrolls instead of wrapping lines
        noWrap.add(diffPane);
        JScrollPane diffScroll = new JScrollPane(noWrap);
        diffScroll.getVerticalScrollBar().setUnitIncrement(16);
        JSplitPane split = new JSplitPane(JSplitPane.HORIZONTAL_SPLIT, new JScrollPane(fileList), diffScroll);
        split.setResizeWeight(0.35);
        split.setBorder(null);

        buildButton.setToolTipText("gradlew build in the work folder");
        buildButton.addActionListener(e -> runBuild());
        fixBuildButton.setToolTipText("Claude fixes the errors of the last build in the work folder");
        fixBuildButton.addActionListener(e -> fixBuildWithClaude());
        cancelClaudeButton.addActionListener(e -> cancelClaude());
        cancelClaudeButton.setVisible(false);
        discardButton.addActionListener(e -> discard());
        applyButton.addActionListener(e -> apply());
        JPanel resultButtons = new JPanel(new FlowLayout(FlowLayout.RIGHT, 6, 0));
        resultButtons.add(cancelClaudeButton);
        resultButtons.add(buildButton);
        resultButtons.add(fixBuildButton);
        resultButtons.add(discardButton);
        resultButtons.add(applyButton);

        JPanel resultTop = new JPanel(new BorderLayout(0, 6));
        resultTop.add(statusLabel, BorderLayout.NORTH);
        resultTop.add(conflictPanel, BorderLayout.CENTER);
        JPanel resultPanel = new JPanel(new BorderLayout(0, 6));
        resultPanel.setBorder(BorderFactory.createEmptyBorder(8, 0, 0, 0));
        resultPanel.add(resultTop, BorderLayout.NORTH);
        resultPanel.add(split, BorderLayout.CENTER);
        resultPanel.add(resultButtons, BorderLayout.SOUTH);

        // --- "Notes"
        notesPane.setEditable(false);
        JPanel notesPanel = new JPanel(new BorderLayout());
        notesPanel.setBorder(BorderFactory.createEmptyBorder(8, 0, 0, 0));
        notesPanel.add(new JScrollPane(notesPane), BorderLayout.CENTER);

        steps.putClientProperty("JTabbedPane.tabHeight", 26);
        steps.addTab("Commits", commitsPanel);
        steps.addTab("Result", resultPanel);
        steps.addTab("Notes", notesPanel);

        add(sourceRow, BorderLayout.NORTH);
        add(steps, BorderLayout.CENTER);

        renderNotes();
        update(project);
    }

    /** New scan result for the target project. */
    void update(ModProject p) {
        boolean first = project == null;
        project = p;
        applyButton.setText("Apply to " + p.mcVersion());
        List<ModProject> sources = host.siblings(p);
        ModProject selected = (ModProject) sourceBox.getSelectedItem();
        filling = true;
        sourceBox.removeAllItems();
        sources.forEach(sourceBox::addItem);
        String main = host.mainBranch(p);
        ModProject keep = selected == null ? null
                : sources.stream().filter(s -> s.path().equals(selected.path())).findFirst().orElse(null);
        ModProject preferred = keep != null ? keep
                : sources.stream().filter(s -> s.branch() != null && s.branch().equals(main)).findFirst()
                .orElse(sources.isEmpty() ? null : sources.getFirst());
        sourceBox.setSelectedItem(preferred);
        filling = false;
        updateTypesLabel();
        if (first || keep == null) loadCandidates();
        updateButtons();
    }

    private String targetName() {
        return project.mcVersion();
    }

    // ---------------------------------------------------------------- type rules

    private Set<String> types() {
        ModProject s = (ModProject) sourceBox.getSelectedItem();
        return host.config().backportTypes(project.name(), targetName())
                .orElseGet(() -> s == null ? Set.of() : Backport.typesFromRepo(s.path(), targetName()));
    }

    private void updateTypesLabel() {
        Set<String> types = types();
        typesLabel.setText(types.isEmpty() ? "Types: all" : "Types: " + String.join(", ", types));
    }

    private void editTypes() {
        Set<String> current = types();
        JCheckBox all = new JCheckBox("All types", current.isEmpty());
        List<JCheckBox> boxes = new ArrayList<>();
        JPanel grid = new JPanel(new GridLayout(0, 4, 8, 4));
        for (String type : Backport.TYPES) {
            JCheckBox box = new JCheckBox(type, current.contains(type));
            box.setEnabled(!all.isSelected());
            boxes.add(box);
            grid.add(box);
        }
        all.addActionListener(e -> boxes.forEach(b -> b.setEnabled(!all.isSelected())));
        JPanel panel = new JPanel(new BorderLayout(0, 8));
        panel.add(new JLabel("<html>Commit types ported from the other versions into <b>" + Ui.html(project.name() + " "
                + targetName()) + "</b>.<br>Commits without a type are always offered.</html>"), BorderLayout.NORTH);
        panel.add(all, BorderLayout.CENTER);
        panel.add(grid, BorderLayout.SOUTH);
        if (JOptionPane.showConfirmDialog(this, panel, "Backport types", JOptionPane.OK_CANCEL_OPTION,
                JOptionPane.PLAIN_MESSAGE) != JOptionPane.OK_OPTION) return;
        List<String> chosen = all.isSelected() ? List.of()
                : boxes.stream().filter(JCheckBox::isSelected).map(JCheckBox::getText).toList();
        host.config().setBackportTypes(project.name(), targetName(), chosen);
        host.config().save();
        updateTypesLabel();
        loadCandidates();
    }

    // ---------------------------------------------------------------- candidates

    private void loadCandidates() {
        ModProject s = (ModProject) sourceBox.getSelectedItem();
        if (s == null || s.branch() == null || project.branch() == null) {
            candidates.set(List.of());
            countLabel.setText(s == null ? "No other version of this mod in the same repository" : "No branch");
            return;
        }
        ModProject p = project;
        Set<String> names = new LinkedHashSet<>(List.of(p.mcVersion(), p.branch()));
        if (p.gradleMcVersion() != null) names.add(p.gradleMcVersion());
        Set<String> types = types();
        countLabel.setText("Loading…");
        background(() -> {
            String tag = Tags.latest(host.git(), p.path(), s.branch()).orElse(null);
            return new Loaded(tag, Backport.candidates(host.git(), p.path(), s.branch(), p.branch(), tag, names, types));
        }, loaded -> {
            sinceTag = loaded.tag();
            candidates.set(loaded.list());
            updateButtons();
        });
    }

    private record Loaded(String tag, List<Backport.Candidate> list) {
    }

    // ---------------------------------------------------------------- porting

    /**
     * Outcome of a run through the queue (computed in the background).
     *
     * @param conflict   the commit that stopped the run (null if it went through)
     * @param checkpoint state before that commit, to skip it
     * @param notes      Markdown lines/sections for "Notes"
     */
    private record Run(List<Changelog.Commit> ported, Changelog.Commit conflict, List<String> conflictFiles,
                       String checkpoint, List<Changelog.Commit> remaining, List<String> notes, String versionNote,
                       List<Backport.FileChange> changes, String error) {
        Run withPortedFirst(List<Changelog.Commit> before, List<String> notesBefore) {
            List<Changelog.Commit> all = new ArrayList<>(before);
            all.addAll(ported);
            List<String> allNotes = new ArrayList<>(notesBefore);
            allNotes.addAll(notes);
            return new Run(all, conflict, conflictFiles, checkpoint, remaining, allNotes, versionNote, changes, error);
        }
    }

    /** Everything a run in the background needs (no Swing state). */
    private record Job(Path dir, ModProject source, ModProject target, boolean sync, boolean claude) {
    }

    private Job job() {
        return new Job(workDir, source, project, syncBox.isSelected(), claudeBox.isSelected());
    }

    private void portSelected() {
        List<Changelog.Commit> selected = candidates.selected();
        ModProject s = (ModProject) sourceBox.getSelectedItem();
        Optional<Path> master = host.master();
        if (selected.isEmpty() || s == null || master.isEmpty() || busy) return;
        if (!ported.isEmpty() || !fileModel.isEmpty()) {
            if (JOptionPane.showConfirmDialog(this, "Discard the current backport result and start again?", "Backport",
                    JOptionPane.OK_CANCEL_OPTION, JOptionPane.WARNING_MESSAGE) != JOptionPane.OK_OPTION) return;
        }
        resetState();
        source = s;
        workDir = Backport.workDir(master.get(), project.name(), targetName());
        Job job = job();
        host.log("Backport " + selected.size() + " commit(s) " + s.branch() + " → " + project.branch()
                + (job.claude() ? " (conflicts with Claude)" : "") + "    (work folder " + job.dir() + ")");
        setBusy(true, "Preparing the work folder… (first time: a full checkout)");
        steps.setSelectedIndex(1);
        background(() -> {
            Backport.prepare(host.git(), job.target().path(), job.dir(), job.target().branch(), this::logAsync);
            baseShaAsync = host.git().runOrThrow(job.dir(), null, "rev-parse", "HEAD").output();
            return runQueue(job, selected, false);
        }, run -> {
            baseSha = baseShaAsync;
            finishRun(run);
        });
    }

    /** Background: picks the commits in order until one stops the run; at the end version + changelog. */
    private Run runQueue(Job job, List<Changelog.Commit> todo, boolean portedBefore) throws Exception {
        List<Changelog.Commit> done = new ArrayList<>();
        List<String> runNotes = new ArrayList<>();
        for (int i = 0; i < todo.size(); i++) {
            Changelog.Commit c = todo.get(i);
            logAsync("Porting " + c.shortSha() + " " + c.message().subject());
            String checkpoint = Backport.checkpoint(host.git(), job.dir());
            Backport.PickResult r = Backport.pick(host.git(), job.dir(), c.sha(), this::logAsync);
            for (String path : r.reverted()) {
                runNotes.add("- `" + c.shortSha() + "`: kept as in the target: `" + path + "`");
                if (path.contains("src/generated/")) runNotes.add("- generated data changed in the source – run `runData` in this version");
            }
            List<Changelog.Commit> rest = todo.subList(i + 1, todo.size());
            switch (r.state()) {
                case OK -> done.add(c);
                case EMPTY -> runNotes.add("- `" + c.shortSha() + "` is already contained, nothing to port");
                case FAILED -> {
                    return new Run(done, null, List.of(), null, List.of(), runNotes, null,
                            Backport.changes(host.git(), job.dir()), "cherry-pick of " + c.shortSha() + " failed:\n" + r.output());
                }
                case CONFLICT -> {
                    if (!job.claude() || cancelled) {
                        return new Run(done, c, r.conflicts(), checkpoint, rest, runNotes, null, List.of(), null);
                    }
                    ClaudeStep step = claudeResolve(job, c, r.conflicts());
                    runNotes.add(step.notes());
                    if (step.resolved()) {
                        done.add(c);
                    } else if (step.skip()) {
                        Backport.restore(host.git(), job.dir(), checkpoint);
                    } else {
                        List<String> open = Backport.unmerged(host.git(), job.dir());
                        return new Run(done, c, open.isEmpty() ? step.left() : open, checkpoint, rest, runNotes, null,
                                List.of(), step.error());
                    }
                }
            }
        }
        String note = null;
        // version and changelog only come along when something was actually ported
        if (job.sync() && (portedBefore || !done.isEmpty())) note = syncVersionAndChangelog(job, runNotes);
        return new Run(done, null, List.of(), null, List.of(), runNotes, note, Backport.changes(host.git(), job.dir()), null);
    }

    /** Background: mod part of the version + CHANGELOG.md of the source. */
    private String syncVersionAndChangelog(Job job, List<String> runNotes) throws Exception {
        String sourceVersion = Changelog.displayVersion(job.source().modVersion(), mc(job.source()));
        if (sourceVersion == null) {
            runNotes.add("- the source has no mod_version – version not taken over");
            return null;
        }
        String change = Backport.syncVersion(job.dir(), mc(job.target()), sourceVersion);
        if (change != null) runNotes.add("- version " + change);
        runNotes.add("- " + Backport.syncChangelog(host.git(), job.dir(), job.source().branch(), sourceVersion));
        return "version " + sourceVersion + " and its changelog";
    }

    private static String mc(ModProject p) {
        return p.gradleMcVersion() != null && !p.gradleMcVersion().isBlank() ? p.gradleMcVersion() : p.mcVersion();
    }

    private void finishRun(Run run) {
        ported.addAll(run.ported());
        notes.addAll(run.notes());
        if (run.versionNote() != null) versionNote = run.versionNote();
        renderNotes();
        verifyNoCommit();
        if (run.conflict() != null) {
            conflictCommit = run.conflict();
            conflictFiles = run.conflictFiles();
            conflictCheckpoint = run.checkpoint();
            queue = run.remaining();
            conflictLabel.setText("<html><body style='width:420px'><b>Conflict</b> in "
                    + Ui.html(conflictCommit.shortSha() + " " + conflictCommit.message().subject()) + " ("
                    + conflictFiles.size() + " file(s), listed below). Let Claude resolve it, skip the commit, or resolve "
                    + "it yourself in the work folder – keep the behavior of the original commit, or delete a file the "
                    + "target does not have any more – then click Continue."
                    + (queue.isEmpty() ? "" : " " + queue.size() + " more commit(s) follow.") + "</body></html>");
            conflictPanel.setVisible(true);
            fileModel.clear();
            conflictFiles.forEach(f -> fileModel.addElement(new Backport.FileChange('U', f)));
            if (!fileModel.isEmpty()) fileList.setSelectedIndex(0);
            host.log("⚠ Backport: conflict in " + conflictCommit.shortSha() + " – " + String.join(", ", conflictFiles));
            setBusy(false, "Waiting for the conflict to be resolved");
            if (run.error() != null) host.error("Backport", run.error());
            return;
        }
        conflictPanel.setVisible(false);
        conflictCommit = null;
        conflictCheckpoint = null;
        queue = List.of();
        showChanges(run.changes());
        if (run.error() != null) {
            setBusy(false, "Stopped");
            host.error("Backport", run.error());
            return;
        }
        setBusy(false, ported.size() + " commit(s) ported – review, then apply to " + project.mcVersion() + " (uncommitted)");
        host.log("✔ Backport ready: " + ported.size() + " commit(s), " + run.changes().size() + " file(s) – nothing committed");
    }

    /** "Continue": the user resolved the conflict in the work folder. */
    private void continueAfterConflict() {
        if (busy || conflictCommit == null) return;
        Job job = job();
        List<String> files = conflictFiles;
        Changelog.Commit c = conflictCommit;
        List<Changelog.Commit> rest = queue;
        setBusy(true, "Continuing…");
        background(() -> {
            List<String> left = Backport.continueAfterConflict(host.git(), job.dir(), files);
            if (!left.isEmpty()) {
                return new Run(List.of(), c, files, conflictCheckpointSnapshot(), rest, List.of(), null, List.of(),
                        "Still conflict markers in: " + String.join(", ", left));
            }
            return runQueue(job, rest, true).withPortedFirst(List.of(c), List.of());
        }, this::afterConflictStep);
    }

    /** "Resolve with Claude" for the current conflict. */
    private void resolveWithClaude() {
        if (busy || conflictCommit == null) return;
        Job job = job();
        Changelog.Commit c = conflictCommit;
        List<String> files = conflictFiles;
        String checkpoint = conflictCheckpoint;
        List<Changelog.Commit> rest = queue;
        boolean portedBefore = !ported.isEmpty();
        cancelled = false;
        setBusy(true, "Claude is resolving the conflict in " + c.shortSha() + "… (output in the log window)");
        background(() -> {
            ClaudeStep step = claudeResolve(job, c, files);
            if (step.resolved()) return runQueue(job, rest, true).withPortedFirst(List.of(c), List.of(step.notes()));
            if (step.skip()) {
                Backport.restore(host.git(), job.dir(), checkpoint);
                return runQueue(job, rest, portedBefore).withPortedFirst(List.of(), List.of(step.notes()));
            }
            List<String> open = Backport.unmerged(host.git(), job.dir());
            return new Run(List.of(), c, open.isEmpty() ? (step.left().isEmpty() ? files : step.left()) : open, checkpoint,
                    rest, List.of(step.notes()), null, List.of(), step.error());
        }, this::afterConflictStep);
    }

    /** "Skip this commit": back to the state before it, then the next commits. */
    private void skipCommit() {
        if (busy || conflictCommit == null || conflictCheckpoint == null) return;
        Job job = job();
        Changelog.Commit c = conflictCommit;
        String checkpoint = conflictCheckpoint;
        List<Changelog.Commit> rest = queue;
        boolean portedBefore = !ported.isEmpty();
        setBusy(true, "Skipping " + c.shortSha() + "…");
        background(() -> {
            Backport.restore(host.git(), job.dir(), checkpoint);
            return runQueue(job, rest, portedBefore).withPortedFirst(List.of(),
                    List.of("- `" + c.shortSha() + "` " + c.message().subject() + ": **skipped**"));
        }, this::afterConflictStep);
    }

    private String conflictCheckpointSnapshot() {
        return conflictCheckpoint;
    }

    private void afterConflictStep(Run run) {
        if (run.conflict() == conflictCommit && run.ported().isEmpty() && run.error() != null) {
            // still the same conflict (markers left or Claude could not resolve it)
            notes.addAll(run.notes());
            renderNotes();
            conflictFiles = run.conflictFiles();
            fileModel.clear();
            conflictFiles.forEach(f -> fileModel.addElement(new Backport.FileChange('U', f)));
            if (!fileModel.isEmpty()) fileList.setSelectedIndex(0);
            setBusy(false, "Waiting for the conflict to be resolved");
            host.error("Backport", run.error());
            return;
        }
        finishRun(run);
    }

    // ---------------------------------------------------------------- Claude

    /**
     * Result of one Claude call in the backport.
     *
     * @param resolved Claude answered "backport" and no conflict is left
     * @param skip     Claude answered "skip"
     * @param left     files that still have conflict markers
     * @param notes    Markdown section for "Notes"
     * @param error    why it stopped (otherwise null)
     */
    private record ClaudeStep(boolean resolved, boolean skip, List<String> left, String notes, String error) {
    }

    /** Background: Claude resolves the conflicts of {@code c}; then the markers are checked. */
    private ClaudeStep claudeResolve(Job job, Changelog.Commit c, List<String> conflicts) throws Exception {
        String commitText = host.git().run(job.dir(), "show", "--format=fuller", "--no-color", c.sha()).output();
        if (commitText.length() > MAX_COMMIT_CHARS) {
            commitText = commitText.substring(0, MAX_COMMIT_CHARS) + "\n… (diff cut, use `git show " + c.shortSha() + "` for the rest)";
        }
        String prompt = BackportClaude.conflictPrompt(context(job), c, commitText, conflicts);
        String heading = "#### `" + c.shortSha() + "` " + c.message().subject();
        ClaudeRunner.Outcome o = callClaude(job.dir(), prompt, "conflict in " + c.shortSha());
        if (!o.ok()) {
            return new ClaudeStep(false, false, conflicts, heading + "\nClaude: " + o.status().name().toLowerCase(Locale.ROOT)
                    + " – " + o.text() + denialNotes(o), o.status() == ClaudeRunner.Status.CANCELLED ? "Cancelled." : "Claude failed:\n" + o.text());
        }
        BackportClaude.Answer a = BackportClaude.parse(o.text());
        String section = heading + " – Claude: **" + a.decision().name().toLowerCase(Locale.ROOT) + "** ("
                + String.format(Locale.ROOT, "$%.2f", o.costUsd()) + ")\n\n" + a.notes() + denialNotes(o);
        if (a.decision() == BackportClaude.Decision.SKIP) {
            logAsync("Claude skips " + c.shortSha());
            return new ClaudeStep(false, true, List.of(), section, null);
        }
        List<String> left = Backport.continueAfterConflict(host.git(), job.dir(), conflicts);
        if (!left.isEmpty()) {
            return new ClaudeStep(false, false, left, section, "Claude left conflict markers in: " + String.join(", ", left));
        }
        if (a.decision() == BackportClaude.Decision.UNKNOWN) {
            section += "\n\n_(Claude gave no DECISION line – the conflict is resolved, please review.)_";
        }
        return new ClaudeStep(true, false, List.of(), section, null);
    }

    private BackportClaude.Context context(Job job) throws IOException {
        Path claudeMd = job.dir().resolve("CLAUDE.md");
        String text = Files.isRegularFile(claudeMd) ? Files.readString(claudeMd, StandardCharsets.UTF_8) : null;
        ModProject t = job.target();
        String name = t.metadata() != null && t.metadata().name() != null ? t.metadata().name() : t.name();
        return new BackportClaude.Context(name, t.loader(), job.source().branch(), mc(job.source()), t.branch(), mc(t), text);
    }

    /**
     * Background: one Claude call in the work folder with the restricted tools; afterwards HEAD and all branches of
     * the repository must be unchanged.
     */
    private ClaudeRunner.Outcome callClaude(Path dir, String prompt, String what) throws Exception {
        Config config = host.config();
        Path exe = ClaudeRunner.find(config.claudeExecutable())
                .orElseThrow(() -> new IOException("Claude Code CLI not found – set it in Settings → Claude Code CLI."));
        List<String> tokens = host.claudeApiKeys();
        logAsync("> claude -p (" + what + ", model " + config.backportModel() + ", restricted tools, "
                + (tokens.isEmpty() ? "Claude Code login" : tokens.size() + " token(s) from CLAUDE_API") + ")");
        String before = Backport.snapshot(host.git(), dir);
        CompletableFuture<ClaudeRunner.Outcome> done = new CompletableFuture<>();
        ClaudeRunner.Request request = new ClaudeRunner.Request(dir, prompt, BackportClaude.tools(), config.backportModel(),
                config.backportMaxBudgetUsd(), Duration.ofMinutes(Math.max(30, config.claudeTimeoutMinutes())), tokens);
        claudeRun = ClaudeRunner.start(exe, request, line -> logAsync("claude: " + line.replace("\n", "\n    claude: ")),
                done::complete);
        SwingUtilities.invokeLater(this::updateButtons);
        ClaudeRunner.Outcome outcome;
        try {
            outcome = done.get();
        } finally {
            claudeRun = null;
            SwingUtilities.invokeLater(this::updateButtons);
        }
        String after = Backport.snapshot(host.git(), dir);
        if (!before.equals(after)) {
            throw new IOException("Branches or HEAD of the work folder changed during the Claude run – something was "
                    + "committed. The result must not be used: Discard and start again.");
        }
        return outcome;
    }

    private static String denialNotes(ClaudeRunner.Outcome o) {
        if (o.denials().isEmpty()) return "";
        StringBuilder sb = new StringBuilder("\n\nDenied by the permission rules:\n");
        o.denials().forEach(d -> sb.append("- `").append(d.replace("`", "'")).append("`\n"));
        return sb.toString();
    }

    private void cancelClaude() {
        cancelled = true;
        ClaudeRunner.Run run = claudeRun;
        if (run != null) run.cancel();
    }

    /** "Fix build with Claude" after a failed build. */
    private void fixBuildWithClaude() {
        if (busy || workDir == null || !buildFailed) return;
        Job job = job();
        List<Changelog.Commit> commits = List.copyOf(ported);
        String output;
        synchronized (buildLines) {
            output = BackportClaude.tail(new ArrayList<>(buildLines), BUILD_TAIL_LINES);
        }
        cancelled = false;
        setBusy(true, "Claude is fixing the build… (output in the log window)");
        background(() -> {
            ClaudeRunner.Outcome o = callClaude(job.dir(), BackportClaude.buildPrompt(context(job), commits, output), "build errors");
            String section;
            if (!o.ok()) {
                section = "#### Build – Claude: " + o.status().name().toLowerCase(Locale.ROOT) + " – " + o.text() + denialNotes(o);
            } else {
                BackportClaude.Answer a = BackportClaude.parse(o.text());
                section = "#### Build – Claude: **" + (a.decision() == BackportClaude.Decision.SKIP ? "could not fix it" : "fixed")
                        + "** (" + String.format(Locale.ROOT, "$%.2f", o.costUsd()) + ")\n\n" + a.notes() + denialNotes(o);
            }
            return new Run(List.of(), null, List.of(), null, List.of(), List.of(section), null,
                    Backport.changes(host.git(), job.dir()), null);
        }, run -> {
            notes.addAll(run.notes());
            renderNotes();
            showChanges(run.changes());
            verifyNoCommit();
            setBusy(false, "Claude worked on the build errors – see Notes, then run the build again");
        });
    }

    /** Safety net: the work folder must still be at the target commit – nothing may have been committed. */
    private void verifyNoCommit() {
        if (workDir == null || baseSha == null) return;
        String head = host.git().run(workDir, "rev-parse", "HEAD").output();
        if (!baseSha.equals(head)) {
            host.error("Backport", "The work folder is no longer at the target commit (HEAD " + head + " instead of "
                    + baseSha + "). Something was committed there – the result is not applied. Discard and start again.");
            baseSha = null;
            updateButtons();
        }
    }

    // ---------------------------------------------------------------- result

    private void showChanges(List<Backport.FileChange> changes) {
        fileModel.clear();
        changes.forEach(fileModel::addElement);
        if (!changes.isEmpty()) fileList.setSelectedIndex(0);
        else CommitDialog.renderDiff(diffPane, "");
        updateButtons();
    }

    private void showDiff() {
        Backport.FileChange f = fileList.getSelectedValue();
        if (f == null || workDir == null) return;
        Path dir = workDir;
        Changelog.Commit c = conflictCommit;
        background(() -> f.status() == 'U' && c != null ? Backport.conflictDiff(host.git(), dir, f.path(), c.sha())
                : Backport.diff(host.git(), dir, f.path()), diff -> CommitDialog.renderDiff(diffPane, diff));
    }

    private void renderNotes() {
        String md = notes.isEmpty() ? "_No notes yet. Notes from Claude and from the tool appear here._"
                : String.join("\n\n", notes);
        notesPane.setText("<html><head><style>body { font-family: sans-serif; font-size: 11px; margin: 4px 6px; }"
                + " h1, h2, h3, h4 { font-size: 12px; margin: 10px 0 4px 0; } ul { margin: 0 0 0 16px; }</style></head><body>"
                + Readme.markdownToHtml(md) + "</body></html>");
        notesPane.setCaretPosition(0);
        steps.setTitleAt(2, notes.isEmpty() ? "Notes" : "Notes (" + notes.stream().filter(n -> n.startsWith("####")).count() + ")");
    }

    private void runBuild() {
        if (workDir == null || busy) return;
        GradleRunner gradle = host.gradle();
        Path dir = workDir;
        if (!gradle.canStart(dir, "build")) {
            host.error("Backport", "A build is already running in the work folder.");
            return;
        }
        String tag = "[backport " + project.mcVersion() + "] ";
        host.log("> gradlew build    (" + dir + ")");
        buildLines.clear();
        buildButton.setEnabled(false);
        fixBuildButton.setEnabled(false);
        statusLabel.setText("Building in the work folder… (output in the log window)");
        try {
            gradle.start(dir, "build", line -> {
                buildLines.add(line);
                logAsync(tag + line);
            }, exit -> SwingUtilities.invokeLater(() -> {
                buildFailed = exit != 0;
                notes.add(exit == 0 ? "- build **passed**" : "- build **failed** (exit code " + exit + ")");
                renderNotes();
                host.log(exit == 0 ? "✔ " + tag + "build passed" : "✖ " + tag + "build failed (exit code " + exit + ")");
                statusLabel.setText(exit == 0 ? "Build passed – apply when the review is done"
                        : "Build failed – fix it in the work folder or with Claude");
                updateButtons();
            }));
        } catch (Exception e) {
            updateButtons();
            host.error("Gradle", e.getMessage());
        }
    }

    private void apply() {
        if (busy || workDir == null || fileModel.isEmpty() || baseSha == null) return;
        if (project.changes() > 0 && JOptionPane.showConfirmDialog(this, "<html>" + Ui.html(project.mcVersion()) + " has "
                + project.changes() + " uncommitted change(s).<br>The backport is applied on top of them (3-way). Continue?</html>",
                "Backport", JOptionPane.OK_CANCEL_OPTION, JOptionPane.WARNING_MESSAGE) != JOptionPane.OK_OPTION) return;
        if (buildFailed && JOptionPane.showConfirmDialog(this, "The last build in the work folder failed. Apply anyway?",
                "Backport", JOptionPane.OK_CANCEL_OPTION, JOptionPane.WARNING_MESSAGE) != JOptionPane.OK_OPTION) return;
        String message = Backport.commitMessage(ported, source.branch(), versionNote);
        ModProject p = project;
        Path dir = workDir;
        setBusy(true, "Applying…");
        background(() -> Backport.apply(host.git(), dir, p.path(), this::logAsync), conflicts -> {
            host.log("✔ Backport applied to " + p.mcVersion() + "/" + p.name() + " as uncommitted changes"
                    + (conflicts.isEmpty() ? "" : " – conflicts in " + String.join(", ", conflicts)));
            resetState();
            setBusy(false, "Applied to " + p.mcVersion() + " – not committed");
            host.filesChanged(p);
            if (!conflicts.isEmpty()) {
                host.error("Backport", "Applied with conflicts in:\n" + String.join("\n", conflicts)
                        + "\n\nResolve them in " + p.path() + " before committing.");
                return;
            }
            if (JOptionPane.showConfirmDialog(this, "Applied as uncommitted changes. Open the commit dialog now?",
                    "Backport", JOptionPane.YES_NO_OPTION) == JOptionPane.YES_OPTION) {
                host.openCommitDialog(p, message);
            }
            loadCandidates();
        });
    }

    private void discard() {
        if (busy || workDir == null) return;
        if (JOptionPane.showConfirmDialog(this, "Throw away the backport result?", "Backport",
                JOptionPane.OK_CANCEL_OPTION, JOptionPane.WARNING_MESSAGE) != JOptionPane.OK_OPTION) return;
        ModProject p = project;
        Path dir = workDir;
        setBusy(true, "Discarding…");
        background(() -> {
            Backport.discard(host.git(), p.path(), dir);
            return true;
        }, ok -> {
            resetState();
            setBusy(false, "Discarded");
            host.log("Backport discarded");
        });
    }

    private void resetState() {
        ported.clear();
        notes.clear();
        queue = List.of();
        conflictCommit = null;
        conflictFiles = List.of();
        conflictCheckpoint = null;
        versionNote = null;
        baseSha = null;
        buildFailed = false;
        buildLines.clear();
        cancelled = false;
        conflictPanel.setVisible(false);
        fileModel.clear();
        CommitDialog.renderDiff(diffPane, "");
        renderNotes();
    }

    // ---------------------------------------------------------------- helpers

    private void setBusy(boolean value, String status) {
        busy = value;
        statusLabel.setText(status);
        updateButtons();
    }

    private void updateButtons() {
        long selected = candidates.selected().size();
        String since = sinceTag == null ? "" : " since " + sinceTag;
        countLabel.setText(candidates.rows.isEmpty() ? "Nothing to port" + since
                : candidates.rows.size() + " commit(s)" + since + ", " + selected + " selected");
        steps.setTitleAt(0, "Commits (" + selected + ")");
        boolean waiting = conflictCommit != null;
        boolean claudeRunning = claudeRun != null;
        portButton.setEnabled(!busy && !waiting && selected > 0);
        continueButton.setEnabled(!busy && waiting);
        resolveButton.setEnabled(!busy && waiting);
        skipButton.setEnabled(!busy && waiting && conflictCheckpoint != null);
        boolean result = !busy && !waiting && !fileModel.isEmpty();
        applyButton.setEnabled(result && baseSha != null);
        buildButton.setEnabled(result);
        fixBuildButton.setEnabled(result && buildFailed && !ported.isEmpty());
        fixBuildButton.setVisible(buildFailed);
        discardButton.setEnabled(!busy && workDir != null && (waiting || !fileModel.isEmpty()));
        cancelClaudeButton.setVisible(claudeRunning);
    }

    private void logAsync(String line) {
        SwingUtilities.invokeLater(() -> host.log("    " + line));
    }

    private <T> void background(Callable<T> work, Consumer<T> onDone) {
        new SwingWorker<T, Void>() {
            @Override
            protected T doInBackground() throws Exception {
                return work.call();
            }

            @Override
            protected void done() {
                try {
                    onDone.accept(get());
                } catch (Exception e) {
                    Throwable cause = e.getCause() != null ? e.getCause() : e;
                    if (busy) setBusy(false, "Error");
                    host.log("✖ Backport: " + cause.getMessage());
                    host.error("Backport", String.valueOf(cause.getMessage()));
                }
            }
        }.execute();
    }

    private static final class Row {
        final Backport.Candidate candidate;
        boolean selected; // old targets can be hundreds of commits behind: the user picks

        Row(Backport.Candidate candidate) {
            this.candidate = candidate;
        }

        boolean selectable() {
            return candidate.state() != Backport.State.ALREADY_PORTED && candidate.state() != Backport.State.MERGE;
        }
    }

    private static final class CandidateModel extends AbstractTableModel {
        private static final String[] COLUMNS = {"", "Commit", "Type", "Subject", "Note"};
        final List<Row> rows = new ArrayList<>();

        void set(List<Backport.Candidate> list) {
            rows.clear();
            list.reversed().forEach(c -> rows.add(new Row(c))); // newest first
            fireTableDataChanged();
        }

        /** Selected commits, oldest first (the order they are ported in). */
        List<Changelog.Commit> selected() {
            return rows.reversed().stream().filter(r -> r.selected).map(r -> r.candidate.commit()).toList();
        }

        void selectPortable(boolean on) {
            rows.forEach(r -> r.selected = on && r.candidate.state() == Backport.State.PORT);
            fireTableDataChanged();
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
            return column == 0 && rows.get(row).selectable();
        }

        @Override
        public Object getValueAt(int row, int column) {
            Row r = rows.get(row);
            var m = r.candidate.commit().message();
            return switch (column) {
                case 0 -> r.selected;
                case 1 -> r.candidate.commit().shortSha();
                case 2 -> m.type() == null ? "" : m.type() + (m.breaking() ? "!" : "");
                case 3 -> m.subject();
                default -> r.candidate.reason();
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
