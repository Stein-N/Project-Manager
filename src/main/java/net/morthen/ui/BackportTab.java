package net.morthen.ui;

import net.morthen.core.Backport;
import net.morthen.core.Changelog;
import net.morthen.core.Config;
import net.morthen.core.Git;
import net.morthen.core.GradleRunner;
import net.morthen.core.ModProject;
import net.morthen.core.SystemActions;

import javax.swing.BorderFactory;
import javax.swing.DefaultListCellRenderer;
import javax.swing.DefaultListModel;
import javax.swing.JButton;
import javax.swing.JCheckBox;
import javax.swing.JComboBox;
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
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Optional;
import java.util.Set;
import java.util.concurrent.Callable;
import java.util.function.Consumer;

/**
 * Tab "Backport" of an older Minecraft version: ports commits from another version of the mod (usually main) without
 * creating commits. Work happens in a hidden worktree; the result is applied here as uncommitted changes. One
 * instance per project survives the rebuilds of the detail view, so a backport in progress is kept.
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

        /** Log line (EDT). */
        void log(String line);

        void error(String title, String message);

        void filesChanged(ModProject project);

        void openCommitDialog(ModProject project, String message);

        void openInIde(Path folder, String name);
    }

    private final Host host;
    private ModProject project;

    // --- commits
    private final JComboBox<ModProject> sourceBox = new JComboBox<>();
    private final JLabel typesLabel = Ui.muted(" ");
    private final CandidateModel candidates = new CandidateModel();
    private final JTable table = new JTable(candidates);
    private final JLabel countLabel = Ui.muted(" ");
    private final JCheckBox syncBox = new JCheckBox("Take over version and CHANGELOG.md", true);
    private final JButton portButton = Ui.primary("Port selected");

    // --- result
    private final JTabbedPane steps = new JTabbedPane();
    private final JLabel statusLabel = new JLabel(" ");
    private final JPanel conflictPanel = new JPanel(new BorderLayout(8, 6));
    private final JLabel conflictLabel = new JLabel();
    private final DefaultListModel<Backport.FileChange> fileModel = new DefaultListModel<>();
    private final JList<Backport.FileChange> fileList = new JList<>(fileModel);
    private final JTextPane diffPane = new JTextPane();
    private final JLabel notesLabel = Ui.muted(" ");
    private final JButton buildButton = new JButton("Run build", Icons.get(Icons.BUILD));
    private final JButton discardButton = new JButton("Discard");
    private final JButton applyButton = Ui.primary("Apply");
    private final JButton continueButton = Ui.primary("Continue");

    // --- state of the current backport (EDT only)
    private boolean busy;
    private Path workDir;
    private String baseSha;
    private ModProject source;
    private List<Changelog.Commit> queue = List.of();
    private final List<Changelog.Commit> ported = new ArrayList<>();
    private final Set<String> notes = new LinkedHashSet<>();
    private Changelog.Commit conflictCommit;
    private List<String> conflictFiles = List.of();
    private String versionNote;

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
        JPanel portRow = new JPanel(new BorderLayout(8, 0));
        JPanel portLeft = new JPanel(new FlowLayout(FlowLayout.LEFT, 6, 0));
        portLeft.add(portButton);
        JButton all = new JButton("Select portable");
        all.setToolTipText("Select every commit that is not filtered, marked or already ported");
        all.putClientProperty("JButton.buttonType", "toolBarButton");
        all.addActionListener(e -> candidates.selectPortable(true));
        JButton none = new JButton("None");
        none.putClientProperty("JButton.buttonType", "toolBarButton");
        none.addActionListener(e -> candidates.selectPortable(false));
        portLeft.add(syncBox);
        portRow.add(portLeft, BorderLayout.WEST);
        JPanel selectRow = new JPanel(new BorderLayout(8, 0));
        JPanel selectButtons = new JPanel(new FlowLayout(FlowLayout.LEFT, 0, 0));
        selectButtons.add(all);
        selectButtons.add(none);
        selectRow.add(selectButtons, BorderLayout.WEST);
        selectRow.add(countLabel, BorderLayout.EAST);
        JPanel commitsPanel = new JPanel(new BorderLayout(0, 6));
        commitsPanel.setBorder(BorderFactory.createEmptyBorder(8, 0, 0, 0));
        commitsPanel.add(selectRow, BorderLayout.NORTH);
        commitsPanel.add(new JScrollPane(table), BorderLayout.CENTER);
        commitsPanel.add(portRow, BorderLayout.SOUTH);

        // --- "Result"
        JButton openIde = new JButton("Open in IntelliJ", Icons.get(Icons.INTELLIJ));
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
        continueButton.addActionListener(e -> continueAfterConflict());
        JPanel conflictButtons = new JPanel(new FlowLayout(FlowLayout.LEFT, 6, 0));
        conflictButtons.add(openIde);
        conflictButtons.add(openExplorer);
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
        discardButton.addActionListener(e -> discard());
        applyButton.addActionListener(e -> apply());
        JPanel resultButtons = new JPanel(new FlowLayout(FlowLayout.RIGHT, 6, 0));
        resultButtons.add(buildButton);
        resultButtons.add(discardButton);
        resultButtons.add(applyButton);
        JPanel resultBottom = new JPanel(new BorderLayout(0, 4));
        resultBottom.add(notesLabel, BorderLayout.CENTER);
        resultBottom.add(resultButtons, BorderLayout.SOUTH);

        JPanel resultTop = new JPanel(new BorderLayout(0, 6));
        resultTop.add(statusLabel, BorderLayout.NORTH);
        resultTop.add(conflictPanel, BorderLayout.CENTER);
        JPanel resultPanel = new JPanel(new BorderLayout(0, 6));
        resultPanel.setBorder(BorderFactory.createEmptyBorder(8, 0, 0, 0));
        resultPanel.add(resultTop, BorderLayout.NORTH);
        resultPanel.add(split, BorderLayout.CENTER);
        resultPanel.add(resultBottom, BorderLayout.SOUTH);

        steps.putClientProperty("JTabbedPane.tabHeight", 26);
        steps.addTab("Commits", commitsPanel);
        steps.addTab("Result", resultPanel);

        add(sourceRow, BorderLayout.NORTH);
        add(steps, BorderLayout.CENTER);

        update(project);
    }

    private boolean filling;

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
        background(() -> Backport.candidates(host.git(), p.path(), s.branch(), p.branch(), names, types),
                list -> {
                    candidates.set(list);
                    updateButtons();
                });
    }

    // ---------------------------------------------------------------- porting

    /** Outcome of a run through the queue (computed in the background). */
    private record Run(List<Changelog.Commit> ported, Changelog.Commit conflict, List<String> conflictFiles,
                       List<Changelog.Commit> remaining, List<String> notes, String versionNote,
                       List<Backport.FileChange> changes, String error) {
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
        ModProject p = project;
        Path dir = workDir;
        boolean sync = syncBox.isSelected();
        host.log("Backport " + selected.size() + " commit(s) " + s.branch() + " → " + p.branch() + "    (work folder " + dir + ")");
        setBusy(true, "Preparing the work folder… (first time: a full checkout)");
        steps.setSelectedIndex(1);
        background(() -> {
            Backport.prepare(host.git(), p.path(), dir, p.branch(), this::logAsync);
            baseShaAsync = host.git().runOrThrow(dir, null, "rev-parse", "HEAD").output();
            return runQueue(dir, selected, s, sync);
        }, run -> {
            baseSha = baseShaAsync;
            finishRun(run);
        });
    }

    private volatile String baseShaAsync;

    /** Background: picks the commits in order until one conflicts; at the end version + changelog. */
    private Run runQueue(Path dir, List<Changelog.Commit> todo, ModProject s, boolean sync) throws Exception {
        List<Changelog.Commit> done = new ArrayList<>();
        List<String> runNotes = new ArrayList<>();
        for (int i = 0; i < todo.size(); i++) {
            Changelog.Commit c = todo.get(i);
            logAsync("Porting " + c.shortSha() + " " + c.message().subject());
            Backport.PickResult r = Backport.pick(host.git(), dir, c.sha(), this::logAsync);
            for (String path : r.reverted()) {
                runNotes.add("kept as in the target: " + path);
                if (path.contains("src/generated/")) runNotes.add("generated data changed in the source – run runData in this version");
            }
            switch (r.state()) {
                case OK -> done.add(c);
                case EMPTY -> runNotes.add(c.shortSha() + " is already contained, nothing to port");
                case CONFLICT -> {
                    return new Run(done, c, r.conflicts(), todo.subList(i + 1, todo.size()), runNotes, null, List.of(), null);
                }
                case FAILED -> {
                    return new Run(done, null, List.of(), List.of(), runNotes, null, Backport.changes(host.git(), dir),
                            "cherry-pick of " + c.shortSha() + " failed:\n" + r.output());
                }
            }
        }
        String note = null;
        if (sync) note = syncVersionAndChangelog(dir, s, runNotes);
        return new Run(done, null, List.of(), List.of(), runNotes, note, Backport.changes(host.git(), dir), null);
    }

    /** Background: mod part of the version + CHANGELOG.md of the source. */
    private String syncVersionAndChangelog(Path dir, ModProject s, List<String> runNotes) throws Exception {
        String sourceVersion = Changelog.displayVersion(s.modVersion(), mc(s));
        if (sourceVersion == null) {
            runNotes.add("the source has no mod_version – version not taken over");
            return null;
        }
        String change = Backport.syncVersion(dir, mc(project), sourceVersion);
        if (change != null) runNotes.add("version " + change);
        runNotes.add(Backport.syncChangelog(host.git(), dir, s.branch(), sourceVersion));
        return "version " + sourceVersion + " and its changelog";
    }

    private static String mc(ModProject p) {
        return p.gradleMcVersion() != null && !p.gradleMcVersion().isBlank() ? p.gradleMcVersion() : p.mcVersion();
    }

    private void finishRun(Run run) {
        ported.addAll(run.ported());
        notes.addAll(run.notes());
        if (run.versionNote() != null) versionNote = run.versionNote();
        verifyNoCommit();
        if (run.conflict() != null) {
            conflictCommit = run.conflict();
            conflictFiles = run.conflictFiles();
            queue = run.remaining();
            conflictLabel.setText("<html><body style='width:420px'><b>Conflict</b> in "
                    + Ui.html(conflictCommit.shortSha() + " " + conflictCommit.message().subject()) + " ("
                    + conflictFiles.size() + " file(s), listed below). Resolve it in the work folder – keep the behavior "
                    + "of the original commit, or delete a file the target does not have any more – then click Continue."
                    + (queue.isEmpty() ? "" : " " + queue.size() + " more commit(s) follow.") + "</body></html>");
            conflictPanel.setVisible(true);
            fileModel.clear();
            conflictFiles.forEach(f -> fileModel.addElement(new Backport.FileChange('U', f)));
            fileList.setSelectedIndex(0);
            host.log("⚠ Backport: conflict in " + conflictCommit.shortSha() + " – " + String.join(", ", conflictFiles));
            setBusy(false, "Waiting for the conflict to be resolved");
            return;
        }
        conflictPanel.setVisible(false);
        conflictCommit = null;
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

    private void continueAfterConflict() {
        if (busy || conflictCommit == null) return;
        Path dir = workDir;
        List<String> files = conflictFiles;
        Changelog.Commit c = conflictCommit;
        List<Changelog.Commit> rest = queue;
        boolean sync = syncBox.isSelected();
        ModProject s = source;
        setBusy(true, "Continuing…");
        background(() -> {
            List<String> left = Backport.continueAfterConflict(host.git(), dir, files);
            if (!left.isEmpty()) {
                return new Run(List.of(), c, files, rest, List.of(), null, List.of(),
                        "Still conflict markers in: " + String.join(", ", left));
            }
            Run next = runQueue(dir, rest, s, sync);
            List<Changelog.Commit> done = new ArrayList<>(List.of(c));
            done.addAll(next.ported());
            return new Run(done, next.conflict(), next.conflictFiles(), next.remaining(), next.notes(), next.versionNote(),
                    next.changes(), next.error());
        }, run -> {
            if (run.error() != null && run.conflict() == c) {
                setBusy(false, "Waiting for the conflict to be resolved");
                host.error("Backport", run.error());
                return;
            }
            finishRun(run);
        });
    }

    /** Safety net: the work folder must still be at the target commit – nothing may have been committed. */
    private void verifyNoCommit() {
        if (workDir == null || baseSha == null) return;
        String head = host.git().run(workDir, "rev-parse", "HEAD").output();
        if (!baseSha.equals(head)) {
            host.error("Backport", "The work folder is no longer at the target commit (HEAD " + head + " instead of "
                    + baseSha + "). Something was committed there – the result is not applied. Discard and start again.");
            applyButton.setEnabled(false);
            baseSha = null;
        }
    }

    // ---------------------------------------------------------------- result

    private void showChanges(List<Backport.FileChange> changes) {
        fileModel.clear();
        changes.forEach(fileModel::addElement);
        if (!changes.isEmpty()) fileList.setSelectedIndex(0);
        else CommitDialog.renderDiff(diffPane, "");
        notesLabel.setText(notes.isEmpty() ? " " : "<html>" + Ui.html(String.join("\n", notes)).replace("\n", "<br>") + "</html>");
        updateButtons();
    }

    private void showDiff() {
        Backport.FileChange f = fileList.getSelectedValue();
        if (f == null || workDir == null) return;
        Path dir = workDir;
        background(() -> f.status() == 'U' && conflictCommit != null ? Backport.conflictDiff(host.git(), dir, f.path(), conflictCommit.sha()) : Backport.diff(host.git(), dir, f.path()),
                diff -> CommitDialog.renderDiff(diffPane, diff));
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
        buildButton.setEnabled(false);
        try {
            gradle.start(dir, "build", line -> logAsync(tag + line), exit -> SwingUtilities.invokeLater(() -> {
                buildButton.setEnabled(true);
                notes.add(exit == 0 ? "build passed" : "build FAILED (exit code " + exit + ") – see the log");
                host.log(exit == 0 ? "✔ " + tag + "build passed" : "✖ " + tag + "build failed (exit code " + exit + ")");
                showChanges(fileModelList());
            }));
        } catch (Exception e) {
            buildButton.setEnabled(true);
            host.error("Gradle", e.getMessage());
        }
    }

    private List<Backport.FileChange> fileModelList() {
        List<Backport.FileChange> list = new ArrayList<>();
        for (int i = 0; i < fileModel.size(); i++) list.add(fileModel.get(i));
        return list;
    }

    private void apply() {
        if (busy || workDir == null || fileModel.isEmpty() || baseSha == null) return;
        if (project.changes() > 0 && JOptionPane.showConfirmDialog(this, "<html>" + Ui.html(project.mcVersion()) + " has "
                + project.changes() + " uncommitted change(s).<br>The backport is applied on top of them (3-way). Continue?</html>",
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
        versionNote = null;
        baseSha = null;
        conflictPanel.setVisible(false);
        fileModel.clear();
        CommitDialog.renderDiff(diffPane, "");
        notesLabel.setText(" ");
    }

    // ---------------------------------------------------------------- helpers

    private void setBusy(boolean value, String status) {
        busy = value;
        statusLabel.setText(status);
        updateButtons();
    }

    private void updateButtons() {
        long selected = candidates.selected().size();
        countLabel.setText(candidates.rows.isEmpty() ? "Nothing to port" : candidates.rows.size() + " commit(s), " + selected + " selected");
        steps.setTitleAt(0, "Commits (" + selected + ")");
        boolean waiting = conflictCommit != null;
        portButton.setEnabled(!busy && !waiting && selected > 0);
        continueButton.setEnabled(!busy && waiting);
        boolean result = !busy && !waiting && !fileModel.isEmpty();
        applyButton.setEnabled(result && baseSha != null);
        buildButton.setEnabled(result);
        discardButton.setEnabled(!busy && workDir != null && (waiting || !fileModel.isEmpty()));
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
        boolean selected;

        Row(Backport.Candidate candidate) {
            this.candidate = candidate;
            this.selected = false; // old targets can be hundreds of commits behind: the user picks
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
