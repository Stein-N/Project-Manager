package net.morthen.ui;

import net.morthen.core.Autostart;
import net.morthen.core.BackportClaude;
import net.morthen.core.Changelog;
import net.morthen.core.ClaudeKeys;
import net.morthen.core.ClaudeRunner;
import net.morthen.core.Config;
import net.morthen.core.Dependencies;
import net.morthen.core.EnvFile;
import net.morthen.core.Git;
import net.morthen.core.GitChanges;
import net.morthen.core.GradleRunner;
import net.morthen.core.GradleTasks;
import net.morthen.core.ModMetadata;
import net.morthen.core.ModProject;
import net.morthen.core.ProjectScanner;
import net.morthen.core.Readme;
import net.morthen.core.SystemActions;
import net.morthen.core.Worktrees;

import javax.swing.BorderFactory;
import javax.swing.ButtonGroup;
import javax.swing.JButton;
import javax.swing.JCheckBox;
import javax.swing.JComboBox;
import javax.swing.JComponent;
import javax.swing.JFrame;
import javax.swing.JLabel;
import javax.swing.JMenuItem;
import javax.swing.JOptionPane;
import javax.swing.JPanel;
import javax.swing.JPopupMenu;
import javax.swing.JProgressBar;
import javax.swing.JRadioButton;
import javax.swing.JScrollPane;
import javax.swing.JTextArea;
import javax.swing.JTextField;
import javax.swing.JToolBar;
import javax.swing.JTree;
import javax.swing.KeyStroke;
import javax.swing.SwingUtilities;
import javax.swing.SwingWorker;
import javax.swing.ToolTipManager;
import javax.swing.UIManager;
import javax.swing.tree.DefaultMutableTreeNode;
import javax.swing.tree.DefaultTreeModel;
import javax.swing.tree.TreePath;
import javax.swing.tree.TreeSelectionModel;
import java.awt.BorderLayout;
import java.awt.Dimension;
import java.awt.FlowLayout;
import java.awt.Font;
import java.awt.GridLayout;
import java.awt.event.KeyEvent;
import java.awt.event.MouseAdapter;
import java.awt.event.MouseEvent;
import java.nio.file.Files;
import java.nio.file.InvalidPathException;
import java.nio.file.Path;
import java.time.LocalTime;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.Enumeration;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Optional;
import java.util.concurrent.Callable;
import java.util.function.Consumer;

public final class MainFrame extends JFrame {
    private static final DateTimeFormatter TIME = DateTimeFormatter.ofPattern("HH:mm:ss");
    /** Fixed window size; the layout is tuned to exactly this size. */
    private static final Dimension WINDOW_SIZE = new Dimension(900, 560);
    private static final int LEFT_WIDTH = 290;
    private static final int MAX_LOG_CHARS = 400_000;

    private final Config config;
    private final Git git;
    private final GradleRunner gradle = new GradleRunner();
    private final TrayManager tray = new TrayManager(this, this::openInIntelliJ, this::exitApplication);
    /** Changelog tabs keep their draft (and a running Claude call) while the details are rebuilt. */
    private final Map<Path, ChangelogTab> changelogTabs = new java.util.HashMap<>();
    /** Backport tabs keep a backport in progress (e.g. waiting for a conflict) while the details are rebuilt. */
    private final Map<Path, BackportTab> backportTabs = new java.util.HashMap<>();
    private final BackportTab.Host backportHost = new BackportTab.Host() {
        @Override
        public Git git() {
            return git;
        }

        @Override
        public Config config() {
            return config;
        }

        @Override
        public GradleRunner gradle() {
            return gradle;
        }

        @Override
        public Optional<Path> master() {
            return config.masterPath().filter(Files::isDirectory);
        }

        @Override
        public List<ModProject> siblings(ModProject project) {
            return MainFrame.this.siblings(project);
        }

        @Override
        public List<String> claudeApiKeys() {
            return MainFrame.this.claudeApiKeys();
        }

        @Override
        public String mainBranch(ModProject project) {
            return MainFrame.this.mainBranch(project);
        }

        @Override
        public void log(String line) {
            MainFrame.this.log(line);
        }

        @Override
        public void error(String title, String message) {
            MainFrame.this.error(title, message);
        }

        @Override
        public void filesChanged(ModProject project) {
            refresh();
        }

        @Override
        public void openCommitDialog(ModProject project, String message) {
            commitDialog(project, message);
        }

        @Override
        public void openInIde(Path folder, String name) {
            openInIntelliJ(folder, name);
        }
    };
    /** Main branch per repo (common git dir), "" = none; see {@link Git#defaultBranch}. */
    private final Map<Path, String> mainBranches = new java.util.HashMap<>();
    private final ChangelogTab.Host changelogHost = new ChangelogTab.Host() {
        @Override
        public Git git() {
            return git;
        }

        @Override
        public Config config() {
            return config;
        }

        @Override
        public void log(String line) {
            MainFrame.this.log(line);
        }

        @Override
        public void error(String title, String message) {
            MainFrame.this.error(title, message);
        }

        @Override
        public List<String> claudeApiKeys() {
            return MainFrame.this.claudeApiKeys();
        }

        @Override
        public void filesChanged(ModProject project) {
            refresh();
        }

        @Override
        public void openCommitDialog(ModProject project, String message) {
            commitDialog(project, message);
        }
    };

    private List<String> versions = List.of();
    private List<ModProject> projects = List.of();
    private List<ProjectScanner.SharedRepo> repos = List.of();

    private final DefaultMutableTreeNode root = new DefaultMutableTreeNode("Master");
    private final DefaultTreeModel treeModel = new DefaultTreeModel(root);
    private final JTree tree = new JTree(treeModel);
    private final JTextField filterField = new JTextField();
    private final JPanel detailPanel = new JPanel(new BorderLayout());
    private final JTextArea logArea = new JTextArea();
    private final JButton logButton = toolButton("Log", Icons.LOG, "Output of git, Gradle and Claude (separate window)", this::openLog);
    private LogWindow logWindow;
    private final JLabel statusLabel = new JLabel();
    private final JProgressBar busyBar = new JProgressBar();
    private int runningTasks;
    private javax.swing.Timer autoFetchTimer;
    private boolean autoFetchRunning;
    private Object shownDetail;
    /** Title (first word) of the selected project tab, kept across rebuilds. */
    private String selectedTab = "Overview";
    /** Latest dependency versions per project from "Check for updates" (session cache). */
    private final Map<Path, Map<String, String>> latestVersions = new java.util.HashMap<>();
    /** Projects whose run tasks are being detected right now. */
    private final java.util.Set<Path> detectingTasks = new java.util.HashSet<>();
    /** Projects for which an automatic detection was already started this session. */
    private final java.util.Set<Path> autoDetected = new java.util.HashSet<>();

    public MainFrame(Config config) {
        super(windowTitle());
        this.config = config;
        this.git = new Git(config.gitExecutable());

        setDefaultCloseOperation(DO_NOTHING_ON_CLOSE);
        addWindowListener(new java.awt.event.WindowAdapter() {
            @Override
            public void windowClosing(java.awt.event.WindowEvent e) {
                closeRequested();
            }

            @Override
            public void windowIconified(java.awt.event.WindowEvent e) {
                if (config.minimizeToTray() && TrayManager.supported()) tray.hideToTray();
            }
        });
        setIconImages(AppIcon.images());
        setSize(WINDOW_SIZE);
        setResizable(false);
        setLocationRelativeTo(null);

        buildUi();
        scheduleAutoFetch();
        applyTraySetting();
        SwingUtilities.invokeLater(this::firstStart);
    }

    /** Start without taking focus: hidden in the tray if possible, otherwise as an iconified window. */
    public void showMinimized() {
        if (config.minimizeToTray() && TrayManager.supported()) {
            tray.install();
            if (tray.installed()) return; // stays hidden, the tray icon opens it
        }
        setExtendedState(ICONIFIED);
        setVisible(true);
    }

    /** Title with the version from the jar manifest (missing when started from the IDE). */
    private static String windowTitle() {
        String version = MainFrame.class.getPackage().getImplementationVersion();
        return "MC Mod Project Manager" + (version != null ? " " + version : "");
    }

    // =====================================================================  Layout

    private void buildUi() {
        JToolBar toolbar = new JToolBar();
        toolbar.setFloatable(false);
        toolbar.setBorder(BorderFactory.createEmptyBorder(6, 8, 6, 8));
        toolbar.add(toolButton("Clone project", Icons.CLONE, "Clone a Git repository into a version folder", () -> cloneDialog(null, null, selectedVersion())));
        toolbar.add(toolButton("Import", Icons.IMPORT, "Take over an existing local project folder", () -> importProject(selectedVersion())));
        toolbar.add(toolButton("New version", Icons.NEW_FOLDER, "Create a new Minecraft version folder", this::createVersion));
        toolbar.addSeparator();
        toolbar.add(toolButton("Refresh", Icons.REFRESH, "Rescan folders (F5)", this::refresh));
        toolbar.add(toolButton("Fetch all", Icons.FETCH, "git fetch for all projects", this::fetchAll));
        toolbar.add(toolButton("Clean up", Icons.CLEANUP, "Gradle caches and build output – find data no project uses",
                () -> CleanupDialog.show(this, projects, gradle, this::logAsync)));
        toolbar.addSeparator();
        toolbar.add(logButton);
        toolbar.add(javax.swing.Box.createHorizontalGlue());
        toolbar.add(toolButton("Settings", Icons.SETTINGS, "Master path, IntelliJ, Git", this::openSettings));

        // --- left side: filter + tree
        filterField.putClientProperty("JTextField.placeholderText", "Filter projects…");
        filterField.putClientProperty("JTextField.showClearButton", true);
        Ui.onChange(filterField, this::rebuildTree);

        tree.setRootVisible(true);
        tree.setShowsRootHandles(true);
        tree.setCellRenderer(new ProjectTreeRenderer(gradle::isRunning));
        tree.getSelectionModel().setSelectionMode(TreeSelectionModel.SINGLE_TREE_SELECTION);
        tree.addTreeSelectionListener(e -> showDetails(selectedObject()));
        tree.addMouseListener(new MouseAdapter() {
            @Override
            public void mouseClicked(MouseEvent e) {
                if (e.getClickCount() == 2 && SwingUtilities.isLeftMouseButton(e)
                        && selectedObject() instanceof ModProject p) {
                    openInIntelliJ(p);
                }
            }

            @Override
            public void mousePressed(MouseEvent e) {
                maybePopup(e);
            }

            @Override
            public void mouseReleased(MouseEvent e) {
                maybePopup(e);
            }
        });
        tree.getInputMap().put(KeyStroke.getKeyStroke(KeyEvent.VK_ENTER, 0), "openIde");
        tree.getActionMap().put("openIde", action(() -> {
            if (selectedObject() instanceof ModProject p) openInIntelliJ(p);
        }));
        ToolTipManager.sharedInstance().registerComponent(tree);

        JPanel left = new JPanel(new BorderLayout(0, 6));
        left.setBorder(BorderFactory.createEmptyBorder(8, 8, 8, 4));
        left.setPreferredSize(new Dimension(LEFT_WIDTH, 0));
        left.add(filterField, BorderLayout.NORTH);
        left.add(new JScrollPane(tree, JScrollPane.VERTICAL_SCROLLBAR_AS_NEEDED,
                JScrollPane.HORIZONTAL_SCROLLBAR_NEVER), BorderLayout.CENTER);

        // --- right side: details
        detailPanel.setBorder(BorderFactory.createEmptyBorder(8, 4, 8, 8));

        JPanel center = new JPanel(new BorderLayout());
        center.add(left, BorderLayout.WEST);
        center.add(detailPanel, BorderLayout.CENTER);

        // --- log: own window, opened from the toolbar
        logArea.setEditable(false);
        logArea.setFont(new Font(Font.MONOSPACED, Font.PLAIN, 12));

        // --- status bar
        busyBar.setIndeterminate(true);
        busyBar.setVisible(false);
        busyBar.setPreferredSize(new Dimension(120, 10));
        JPanel status = new JPanel(new BorderLayout(8, 0));
        status.setBorder(BorderFactory.createEmptyBorder(4, 10, 6, 10));
        status.add(statusLabel, BorderLayout.CENTER);
        status.add(busyBar, BorderLayout.EAST);

        getContentPane().setLayout(new BorderLayout());
        getContentPane().add(toolbar, BorderLayout.NORTH);
        getContentPane().add(center, BorderLayout.CENTER);
        getContentPane().add(status, BorderLayout.SOUTH);

        getRootPane().getInputMap(JComponent.WHEN_IN_FOCUSED_WINDOW)
                .put(KeyStroke.getKeyStroke(KeyEvent.VK_F5, 0), "refresh");
        getRootPane().getActionMap().put("refresh", action(this::refresh));
    }

    private void firstStart() {
        if (config.masterPath().filter(Files::isDirectory).isEmpty()) {
            JOptionPane.showMessageDialog(this,
                    "<html>Welcome! Please choose your <b>master folder</b> first.<br><br>"
                            + "It will contain one subfolder per Minecraft version,<br>"
                            + "e.g. <tt>Master\\1.21.1\\MyMod</tt>.</html>",
                    "Choose master folder", JOptionPane.INFORMATION_MESSAGE);
            chooseMaster();
        }
        refresh();
    }

    // =====================================================================  Data & tree

    private void refresh() {
        Optional<Path> master = config.masterPath();
        updateStatus();
        if (master.isEmpty() || !Files.isDirectory(master.get())) {
            versions = List.of();
            projects = List.of();
            repos = List.of();
            rebuildTree();
            showDetails(null);
            return;
        }
        Object keep = selectionKey();
        background("Scanning projects…", () -> ProjectScanner.scan(master.get(), git), result -> {
            apply(result);
            rebuildTree();
            reselect(keep);
            updateStatus();
        });
    }

    private void apply(ProjectScanner.ScanResult result) {
        versions = result.versions();
        projects = result.projects();
        repos = result.repos();
        tray.updateProjects(projects);
    }

    /** Other worktrees of the same repository. */
    private List<ModProject> siblings(ModProject p) {
        if (p.commonDir() == null) return List.of();
        return projects.stream().filter(o -> o != p && p.commonDir().equals(o.commonDir())).toList();
    }

    private boolean isShared(ModProject p) {
        return p.commonDir() != null && repos.stream().anyMatch(r -> r.path().equals(p.commonDir()));
    }

    private void rebuildTree() {
        Path master = config.masterPath().orElse(null);
        String filter = filterField.getText().trim().toLowerCase(Locale.ROOT);
        Object keep = selectionKey();

        Map<String, List<ModProject>> byVersion = new LinkedHashMap<>();
        for (String v : versions) byVersion.put(v, new ArrayList<>());
        for (ModProject p : projects) {
            if (filter.isEmpty() || p.name().toLowerCase(Locale.ROOT).contains(filter)
                    || (p.branch() != null && p.branch().toLowerCase(Locale.ROOT).contains(filter))) {
                byVersion.computeIfAbsent(p.mcVersion(), k -> new ArrayList<>()).add(p);
            }
        }

        root.removeAllChildren();
        root.setUserObject(master == null ? "No master folder selected"
                : master.getFileName() == null ? master.toString() : master.getFileName().toString());
        tree.setToolTipText(master == null ? null : master.toString());
        byVersion.forEach((version, list) -> {
            if (!filter.isEmpty() && list.isEmpty() && !version.toLowerCase(Locale.ROOT).contains(filter)) {
                return;
            }
            DefaultMutableTreeNode node = new DefaultMutableTreeNode(
                    new VersionEntry(version, master == null ? null : master.resolve(version), list.size()));
            list.forEach(p -> node.add(new DefaultMutableTreeNode(p, false)));
            root.add(node);
        });
        treeModel.reload();
        for (int i = 0; i < tree.getRowCount(); i++) {
            tree.expandRow(i);
        }
        reselect(keep);
    }

    /** Remembers the selection (project path / version) so it can be restored after a rebuild. */
    private Object selectionKey() {
        Object o = selectedObject();
        if (o instanceof ModProject p) return p.path();
        if (o instanceof VersionEntry v) return "v:" + v.version();
        return null;
    }

    private void reselect(Object key) {
        if (key == null) {
            tree.setSelectionRow(0);
            return;
        }
        Enumeration<?> e = root.depthFirstEnumeration();
        while (e.hasMoreElements()) {
            DefaultMutableTreeNode node = (DefaultMutableTreeNode) e.nextElement();
            Object o = node.getUserObject();
            if ((o instanceof ModProject p && p.path().equals(key))
                    || (o instanceof VersionEntry v && key.equals("v:" + v.version()))) {
                TreePath path = new TreePath(node.getPath());
                tree.setSelectionPath(path);
                tree.scrollPathToVisible(path);
                showDetails(o);
                return;
            }
        }
        tree.setSelectionRow(0);
    }

    private Object selectedObject() {
        TreePath path = tree.getSelectionPath();
        return path == null ? null : ((DefaultMutableTreeNode) path.getLastPathComponent()).getUserObject();
    }

    private String selectedVersion() {
        Object o = selectedObject();
        if (o instanceof ModProject p) return p.mcVersion();
        if (o instanceof VersionEntry v) return v.version();
        return versions.isEmpty() ? null : versions.getFirst();
    }

    private void updateStatus() {
        String master = config.masterPath().map(Path::toString).orElse("–");
        long dirty = projects.stream().filter(ModProject::hasUnsavedWork).count();
        // Counters first, so a long path only truncates the path
        statusLabel.setText(versions.size() + " versions  ·  " + projects.size() + " projects"
                + (dirty > 0 ? "  ·  " + dirty + " with unsaved work" : "") + "  ·  Master: " + master);
        statusLabel.setToolTipText(master);
    }

    // =====================================================================  Detail view

    private void showDetails(Object obj) {
        shownDetail = obj;
        detailPanel.removeAll();
        if (obj instanceof ModProject p) {
            // tabs bring their own scroll panes
            detailPanel.add(projectDetails(p), BorderLayout.CENTER);
            detailPanel.revalidate();
            detailPanel.repaint();
            return;
        }
        JComponent content;
        if (obj instanceof VersionEntry v) {
            content = versionDetails(v);
        } else {
            content = overview();
        }
        JScrollPane scroll = new JScrollPane(content, JScrollPane.VERTICAL_SCROLLBAR_AS_NEEDED,
                JScrollPane.HORIZONTAL_SCROLLBAR_NEVER);
        scroll.setBorder(null);
        scroll.getVerticalScrollBar().setUnitIncrement(16);
        detailPanel.add(scroll, BorderLayout.CENTER);
        detailPanel.revalidate();
        detailPanel.repaint();
    }

    /** Header (always visible) + tabs. The selected tab is kept when the details are rebuilt. */
    private JComponent projectDetails(ModProject p) {
        ModMetadata meta = p.metadata();
        JPanel header = new JPanel(new BorderLayout(12, 0));
        header.setBorder(BorderFactory.createEmptyBorder(4, 12, 10, 12));
        javax.swing.Icon headerIcon = meta != null ? Ui.modIcon(meta.icon(), 40) : null;
        if (headerIcon != null) header.add(new JLabel(headerIcon), BorderLayout.WEST);
        JPanel titles = new JPanel(new GridLayout(2, 1, 0, 2));
        titles.add(Ui.heading(p.name(), "h2"));
        JLabel subtitle = Ui.muted("Minecraft " + p.mcVersion() + (p.loader() != null ? "  ·  " + p.loader() : "")
                + (meta != null && meta.id() != null ? "  ·  " + meta.id() : ""));
        subtitle.setToolTipText(subtitle.getText());
        titles.add(subtitle);
        header.add(titles, BorderLayout.CENTER);
        JButton openIde = Ui.primary("Open in IntelliJ");
        openIde.addActionListener(e -> openInIntelliJ(p));
        JPanel openRow = new JPanel(new java.awt.GridBagLayout()); // vertically centered
        openRow.add(openIde);
        header.add(openRow, BorderLayout.EAST);

        javax.swing.JTabbedPane tabs = new javax.swing.JTabbedPane();
        tabs.putClientProperty("JTabbedPane.tabHeight", 30);
        tabs.addTab("Overview", scrollTab(overviewTab(p)));
        Readme.find(p.path()).ifPresent(readme -> tabs.addTab("README", ReadmeView.create(readme, msg -> error("README", msg))));
        if (p.git()) {
            String gitTitle = "Git" + (p.changes() > 0 ? "  ●" + p.changes() : "") + (p.ahead() > 0 ? "  ↑" + p.ahead() : "")
                    + (p.behind() > 0 ? "  ↓" + p.behind() : "");
            tabs.addTab(gitTitle, scrollTab(gitTab(p)));
        }
        if (GradleRunner.wrapper(p.path()).isPresent()) {
            int runningCount = gradle.runningTasks(p.path()).size();
            tabs.addTab(runningCount > 1 ? "Gradle (" + runningCount + ")" : "Gradle", runningCount > 0 ? Icons.get(Icons.RUN) : null, scrollTab(gradleTab(p)));
        }
        String mainBranch = p.git() ? mainBranch(p) : null;
        if (p.git() && mainBranch != null && !mainBranch.equals(p.branch())) {
            tabs.addTab("Changelog", scrollTab(changelogElsewhereTab(p, mainBranch)));
        } else if (p.git()) {
            ChangelogTab changelog = changelogTabs.get(p.path());
            if (changelog == null) {
                changelog = new ChangelogTab(changelogHost, p);
                changelogTabs.put(p.path(), changelog);
            } else {
                changelog.update(p);
            }
            tabs.addTab("Changelog", changelog);
        }
        // Backports go from the main branch (or another version) into the older versions
        if (p.git() && mainBranch != null && !mainBranch.equals(p.branch()) && !siblings(p).isEmpty()) {
            BackportTab backport = backportTabs.get(p.path());
            if (backport == null) {
                backport = new BackportTab(backportHost, p);
                backportTabs.put(p.path(), backport);
            } else {
                backport.update(p);
            }
            tabs.addTab("Backport", backport);
        }
        List<Dependencies.Entry> deps = Dependencies.read(p.path());
        if (!deps.isEmpty() || (meta != null && !meta.dependencies().isEmpty())) {
            tabs.addTab("Dependencies", scrollTab(dependenciesTab(p, deps)));
        }
        for (int i = 0; i < tabs.getTabCount(); i++) {
            if (tabs.getTitleAt(i).startsWith(selectedTab)) tabs.setSelectedIndex(i);
        }
        tabs.addChangeListener(e -> {
            String title = tabs.getTitleAt(tabs.getSelectedIndex());
            selectedTab = title.contains(" ") ? title.substring(0, title.indexOf(' ')) : title;
        });

        JPanel panel = new JPanel(new BorderLayout());
        panel.add(header, BorderLayout.NORTH);
        panel.add(tabs, BorderLayout.CENTER);
        return panel;
    }

    /** Main branch of the project's repo (cached per repo); null if there is none. */
    private String mainBranch(ModProject p) {
        Path key = p.commonDir() != null ? p.commonDir() : p.path();
        String branch = mainBranches.computeIfAbsent(key, k -> {
            String b = git.defaultBranch(p.path());
            return b == null ? "" : b;
        });
        return branch.isEmpty() ? null : branch;
    }

    /** Changelogs are only written on the main branch; the backport takes them over into the other versions. */
    private Ui.Stack changelogElsewhereTab(ModProject p, String mainBranch) {
        Ui.Stack stack = new Ui.Stack();
        Optional<ModProject> main = siblings(p).stream().filter(s -> mainBranch.equals(s.branch())).findFirst();
        stack.push(new JLabel("<html><body style='width:420px'>The changelog is only written on the main branch <b>"
                + Ui.html(mainBranch) + "</b>" + main.map(m -> " (" + Ui.html(m.mcVersion()) + ")").orElse("")
                + ". The backport takes it over into this version together with the changes.</body></html>"), 10);
        String here;
        try {
            Path file = p.path().resolve(Changelog.FILE);
            here = Files.isRegularFile(file)
                    ? Changelog.detectVersion(Files.readString(file)).map(v -> "v" + v).orElse("no version heading")
                    : "none";
        } catch (Exception e) {
            here = "could not be read";
        }
        Ui.Form info = new Ui.Form();
        info.add("CHANGELOG.md here", Ui.copyable(here));
        stack.push(info, 10);
        main.ifPresent(m -> {
            JPanel row = new JPanel(new FlowLayout(FlowLayout.LEFT, 0, 0));
            row.add(button("Go to " + m.mcVersion() + "/" + m.name(), Icons.BRANCH, () -> reselect(m.path())));
            stack.push(row, 0);
        });
        return stack;
    }

    private static JScrollPane scrollTab(Ui.Stack content) {
        content.setBorder(BorderFactory.createEmptyBorder(10, 12, 10, 12));
        JScrollPane scroll = new JScrollPane(content.finish(), JScrollPane.VERTICAL_SCROLLBAR_AS_NEEDED,
                JScrollPane.HORIZONTAL_SCROLLBAR_NEVER);
        scroll.setBorder(null);
        scroll.getVerticalScrollBar().setUnitIncrement(16);
        SwingUtilities.invokeLater(() -> scroll.getVerticalScrollBar().setValue(0));
        return scroll;
    }

    private Ui.Stack overviewTab(ModProject p) {
        Ui.Stack stack = new Ui.Stack();
        ModMetadata meta = p.metadata();
        Ui.Form info = new Ui.Form();
        info.add("Path", Ui.copyable(p.path().toString()));
        if (meta != null && meta.name() != null) info.add("Name", Ui.copyable(meta.name()));
        if (meta != null && meta.id() != null) info.add("Mod ID", Ui.copyable(meta.id()));
        String version = meta != null && meta.version() != null ? meta.version() : p.modVersion();
        if (version != null) info.add("Mod version", Ui.copyable(version));
        if (p.gradleMcVersion() != null) {
            String text = p.gradleMcVersion();
            JLabel mc = new JLabel(p.versionMismatch()
                    ? "<html>" + Ui.html(text) + " <font color='#d32f2f'>does not match folder “" + Ui.html(p.mcVersion()) + "”</font></html>"
                    : text);
            if (p.versionMismatch()) {
                mc.setIcon(Icons.get(Icons.WARNING));
                mc.setHorizontalTextPosition(JLabel.LEADING); // value first, warning icon behind it
            }
            info.add("MC (gradle)", mc);
        }
        if (meta != null) {
            if (!meta.authors().isEmpty()) info.add("Authors", Ui.copyable(String.join(", ", meta.authors())));
            if (meta.description() != null && !meta.description().isBlank()) {
                String description = meta.description().strip().replaceAll("\\s+", " ");
                if (description.length() > 200) description = description.substring(0, 197) + "…";
                JLabel d = new JLabel("<html><body style='width:300px'>" + Ui.html(description) + "</body></html>");
                d.setToolTipText(meta.description());
                info.add("Description", d);
            }
            info.add("Metadata", Ui.copyable(meta.file()));
        }
        stack.push(info, 14);

        JPanel actions = new JPanel(new GridLayout(0, 2, 8, 8));
        actions.add(button("Open in Explorer", Icons.FOLDER, () -> run(() -> SystemActions.openExplorer(p.path()))));
        actions.add(button("Open terminal", Icons.TERMINAL, () -> run(() -> SystemActions.openTerminal(p.path()))));
        actions.add(button("Move / rename…", Icons.EDIT, () -> moveProject(p)));
        JButton delete = button("Remove…", Icons.DELETE, () -> deleteProject(p));
        delete.setForeground(new java.awt.Color(0xD32F2F));
        actions.add(delete);
        stack.push(actions, 0);
        return stack;
    }

    private Ui.Stack gitTab(ModProject p) {
        Ui.Stack stack = new Ui.Stack();
        Ui.Form info = new Ui.Form();
        info.add("Branch", Ui.copyable(p.branch()));
        info.add("Upstream", Ui.copyable(p.upstream() == null ? "– (no tracking branch)" : p.upstream()));
        info.add("Status", new JLabel(statusHtml(p), p.changes() == 0 ? Icons.get(Icons.SUCCESS) : null, JLabel.LEADING));
        info.add("Remote", Ui.copyable(p.remoteUrl().isEmpty() ? "–" : p.remoteUrl()));
        info.add("Repository", new JLabel(repoDescription(p)));
        List<ModProject> siblings = siblings(p);
        if (!siblings.isEmpty()) {
            // grid instead of a flow layout so the chips wrap inside the fixed width
            JPanel links = new JPanel(new GridLayout(0, 3, 6, 6));
            for (ModProject s : siblings) {
                boolean ownBranch = s.branch() != null && !s.branch().equals(s.mcVersion());
                JButton link = new JButton(s.mcVersion() + (ownBranch ? "  " + s.branch() : ""), ownBranch ? Icons.get(Icons.BRANCH) : null);
                link.putClientProperty("JButton.buttonType", "roundRect");
                link.setToolTipText(s.path() + (s.branch() != null ? "  [" + s.branch() + "]" : ""));
                link.addActionListener(e -> reselect(s.path()));
                links.add(link);
            }
            info.add("Other versions", links);
        }
        stack.push(info, 14);

        JPanel actions = new JPanel(new GridLayout(0, 2, 8, 8));
        actions.add(button("Fetch", Icons.FETCH, () -> gitTask("Fetch " + p.name(), p.path(), "fetch", "--all", "--prune")));
        actions.add(button("Pull (fast-forward)", Icons.PULL, () -> gitTask("Pull " + p.name(), p.path(), "pull", "--ff-only")));
        JButton commit = button(p.changes() > 0 ? "Commit (" + p.changes() + ")…" : "Commit…", Icons.COMMIT, () -> commitDialog(p));
        commit.setEnabled(p.changes() > 0);
        actions.add(commit);
        boolean canPush = p.ahead() > 0 || (p.upstream() == null && !p.remoteUrl().isEmpty() && p.branch() != null);
        JButton push = button(p.upstream() == null ? "Push (set upstream)" : "Push" + (p.ahead() > 0 ? " (" + p.ahead() + ")" : ""), Icons.PUSH, () -> push(p));
        push.setEnabled(canPush);
        actions.add(push);
        actions.add(button("Add version (worktree)…", Icons.ADD, () -> addWorktree(p)));
        if (p.standaloneClone()) {
            actions.add(button("Convert to shared repo…", Icons.MERGE, () -> convertToShared(p)));
        }
        stack.push(actions, 16);

        stack.push(Ui.heading("Switch branch", "h4"), 6);
        JComboBox<String> branches = new JComboBox<>(new String[]{"Loading…"});
        branches.setEnabled(false);
        JButton checkout = new JButton("Checkout");
        checkout.setEnabled(false);
        checkout.addActionListener(e -> {
            Object b = branches.getSelectedItem();
            if (b != null && !b.toString().equals(p.branch())) {
                if (p.changes() > 0 && JOptionPane.showConfirmDialog(this,
                        "There are " + p.changes() + " uncommitted change(s). Switch anyway?",
                        "Switch branch", JOptionPane.YES_NO_OPTION, JOptionPane.WARNING_MESSAGE) != JOptionPane.YES_OPTION) {
                    return;
                }
                gitTask("Checkout " + b, p.path(), "checkout", b.toString());
            }
        });
        JPanel row = new JPanel(new BorderLayout(8, 0));
        row.add(branches, BorderLayout.CENTER);
        row.add(checkout, BorderLayout.EAST);
        stack.push(row, 4);
        stack.push(Ui.muted("Tip: for parallel versions, use “Add version (worktree)…” instead."), 0);

        new SwingWorker<List<String>, Void>() {
            @Override
            protected List<String> doInBackground() {
                return git.branches(p.path());
            }

            @Override
            protected void done() {
                if (shownDetail != p) return;
                try {
                    branches.removeAllItems();
                    get().forEach(branches::addItem);
                    branches.setSelectedItem(p.branch());
                    branches.setEnabled(true);
                    checkout.setEnabled(true);
                } catch (Exception ignored) {
                    branches.removeAllItems();
                }
            }
        }.execute();
        return stack;
    }

    private Ui.Stack gradleTab(ModProject p) {
        Ui.Stack stack = new Ui.Stack();
        List<String> running = gradle.runningTasks(p.path());
        if (!running.isEmpty()) {
            // one row per running task, each with its own stop button
            Ui.Form runningForm = new Ui.Form();
            for (String t : running) {
                JButton stop = button("Stop", Icons.STOP, () -> stopGradle(p, t));
                stop.setForeground(new java.awt.Color(0xD32F2F));
                stop.putClientProperty("JButton.buttonType", "roundRect");
                JPanel row = new JPanel(new BorderLayout(8, 0));
                row.add(new JLabel("<html><tt>" + Ui.html(t) + "</tt></html>", Icons.get(Icons.RUN), JLabel.LEADING), BorderLayout.CENTER);
                row.add(stop, BorderLayout.EAST);
                runningForm.add("Running", row);
            }
            stack.push(runningForm, 4);
            stack.push(Ui.muted("Output in the log window (toolbar → Log). Up to " + GradleRunner.MAX_PER_LOADER + " tasks per loader can run at the same time."), 12);
        }
        // --- General (build, publish) and the game tests per loader
        Optional<GradleTasks.Cached> cached = GradleTasks.cached(p.path());
        boolean detecting = detectingTasks.contains(p.path());
        List<GradleTasks.Group> groups = GradleTasks.group(cached.map(GradleTasks.Cached::tasks).orElse(List.of()),
                rootTaskGroupName(p));
        for (GradleTasks.Group group : groups) {
            boolean general = group.name().equals("General");
            stack.push(Ui.heading(general ? "General" : group.name() + " · game tests", "h4"), 6);
            if (general) {
                int envCount = publishEnvironment().size();
                JPanel envRow = new JPanel(new BorderLayout(8, 0));
                envRow.add(Ui.muted(envCount == 0 ? "Publish tasks: no environment variables set (.env)"
                        : "Publish tasks use " + envCount + " variable(s) from .env"), BorderLayout.CENTER);
                JButton editEnv = button("Edit .env…", Icons.EDIT, this::editEnvironment);
                editEnv.putClientProperty("JButton.buttonType", "toolBarButton");
                envRow.add(editEnv, BorderLayout.EAST);
                stack.push(envRow, 6);
            }
            JPanel buttons = new JPanel(new GridLayout(0, 3, 6, 6));
            for (GradleTasks.RunTask t : group.tasks()) {
                JButton b = gradleButton(p, t.path(), t.label());
                b.setToolTipText("<html><tt>gradlew " + Ui.html(t.path()) + "</tt>"
                        + (t.description().isEmpty() ? "" : "<br>" + Ui.html(t.description())) + "</html>");
                buttons.add(b);
            }
            stack.push(buttons, 14);
        }
        if (cached.isEmpty()) {
            stack.push(Ui.muted(detecting
                    ? "<html>Asking Gradle for the available tasks… (output in the log window)</html>"
                    : "<html>Publish and game test tasks are read from Gradle – click “Refresh Gradle tasks”. "
                    + "The first run of a fresh project can take a while because Loom/NeoForge set up Minecraft.</html>"), 14);
        } else if (groups.size() == 1) {
            stack.push(Ui.muted("No game test tasks configured."), 14);
        }

        JPanel actions = new JPanel(new GridLayout(0, 2, 8, 8));
        actions.add(button("Open build/libs", Icons.FOLDER, () -> openBuildLibs(p)));
        long versionsOfMod = projects.stream()
                .filter(o -> o.name().equals(p.name()) && GradleRunner.wrapper(o.path()).isPresent())
                .count();
        if (versionsOfMod > 1) {
            actions.add(button("Build all " + versionsOfMod + " versions", Icons.BUILD, () -> buildAllVersions(p.name())));
        }
        JButton refreshTasks = button(detecting ? "Refreshing Gradle tasks…" : "Refresh Gradle tasks", Icons.REFRESH, () -> detectTasks(p));
        refreshTasks.setToolTipText("Reads the available tasks again (gradlew tasks --all)");
        refreshTasks.setEnabled(!detecting && gradle.canStart(p.path(), "tasks --all"));
        actions.add(refreshTasks);
        stack.push(actions, 6);
        if (cached.isPresent()) {
            String when = java.time.format.DateTimeFormatter.ofPattern("dd.MM.yyyy HH:mm")
                    .format(java.time.LocalDateTime.ofInstant(java.time.Instant.ofEpochMilli(cached.get().detected()),
                            java.time.ZoneId.systemDefault()));
            stack.push(Ui.muted("Tasks detected " + when + (cached.get().stale() ? " · build files changed since" : "")), 14);
        }
        // detect automatically when it is cheap (project was configured before) and nothing is known yet / outdated
        if (!detecting && running.isEmpty() && (cached.isEmpty() || cached.get().stale())
                && Files.isDirectory(p.path().resolve(".gradle")) && autoDetected.add(p.path())) {
            SwingUtilities.invokeLater(() -> detectTasks(p));
        }

        Ui.Form info = new Ui.Form();
        Dependencies.wrapperVersion(p.path()).ifPresent(v -> info.add("Gradle wrapper", Ui.copyable(v)));
        info.add("Wrapper", Ui.copyable(GradleRunner.wrapper(p.path()).map(Path::toString).orElse("–")));
        stack.push(info, 0);
        return stack;
    }

    private Ui.Stack dependenciesTab(ModProject p, List<Dependencies.Entry> deps) {
        Ui.Stack stack = new Ui.Stack();
        if (!deps.isEmpty()) {
            JPanel depHeader = new JPanel(new BorderLayout());
            depHeader.add(Ui.heading("Versions (gradle.properties)", "h4"), BorderLayout.WEST);
            JButton check = button("Check for updates", Icons.UPGRADE, () -> checkUpdates(p, deps));
            check.putClientProperty("JButton.buttonType", "toolBarButton");
            depHeader.add(check, BorderLayout.EAST);
            stack.push(depHeader, 4);
            Map<String, String> latest = latestVersions.getOrDefault(p.path(), Map.of());
            Ui.Form form = new Ui.Form();
            for (Dependencies.Entry d : deps) {
                JPanel row = new JPanel(new BorderLayout(8, 0));
                row.add(Ui.copyable(d.value()), BorderLayout.CENTER);
                String l = latest.get(dependencyKey(d));
                if (l != null) {
                    JPanel status = new JPanel(new FlowLayout(FlowLayout.RIGHT, 6, 0));
                    if (l.startsWith("!")) {
                        JLabel failed = Ui.muted("lookup failed"); failed.setIcon(Icons.get(Icons.WARNING));
                        failed.setToolTipText(l.substring(1));
                        status.add(failed);
                    } else if (Dependencies.isNewer(l, d.value())) {
                        status.add(new JLabel("<html><font color='#c77800'>" + Ui.html(l) + "</font></html>", Icons.get(Icons.UPGRADE), JLabel.LEADING));
                        if (d.updatable()) {
                            JButton update = button("Update", () -> updateDependency(p, d, l));
                            update.putClientProperty("JButton.buttonType", "roundRect");
                            status.add(update);
                        }
                    } else {
                        status.add(new JLabel("<html><font color='#2e7d32'>latest</font></html>", Icons.get(Icons.SUCCESS), JLabel.LEADING));
                    }
                    row.add(status, BorderLayout.EAST);
                }
                form.add(d.label(), row);
            }
            stack.push(form, 16);
        }
        ModMetadata meta = p.metadata();
        if (meta != null && !meta.dependencies().isEmpty()) {
            stack.push(Ui.heading("Declared in " + Path.of(meta.file()).getFileName(), "h4"), 4);
            Ui.Form form = new Ui.Form();
            for (ModMetadata.Dependency d : meta.dependencies()) {
                form.add(d.modId(), new JLabel("<html>" + Ui.html(d.version())
                        + (d.required() ? "" : " <font color='" + Ui.hex(UIManager.getColor("Label.disabledForeground")) + "'>(optional)</font>")
                        + "</html>"));
            }
            stack.push(form, 0);
        }
        return stack;
    }

    private static String statusHtml(ModProject p) {
        List<String> parts = new ArrayList<>();
        parts.add(p.changes() == 0 ? "<font color='#2e7d32'>no local changes</font>"
                : "<font color='#c77800'>● " + p.changes() + " changed file(s)</font>");
        if (p.ahead() > 0) parts.add("<font color='#2e7d32'>↑ " + p.ahead() + " not pushed</font>");
        if (p.behind() > 0) parts.add("<font color='#d32f2f'>↓ " + p.behind() + " behind remote</font>");
        return "<html>" + String.join("&nbsp;&nbsp;·&nbsp;&nbsp;", parts) + "</html>";
    }

    private JComponent versionDetails(VersionEntry v) {
        Ui.Stack stack = new Ui.Stack();
        stack.setBorder(BorderFactory.createEmptyBorder(4, 12, 8, 12));
        stack.push(Ui.heading("Minecraft " + v.version(), "h2"), 2);
        stack.push(Ui.muted(v.count() + " project(s)"), 16);

        Ui.Form info = new Ui.Form();
        info.add("Folder", Ui.copyable(v.path() == null ? "" : v.path().toString()));
        stack.push(info, 18);

        JPanel actions = new JPanel(new GridLayout(0, 2, 8, 8));
        actions.add(button("Clone project into this version…", Icons.CLONE, () -> cloneDialog(null, null, v.version())));
        actions.add(button("Import local project…", Icons.IMPORT, () -> importProject(v.version())));
        actions.add(button("Open folder", Icons.FOLDER, () -> run(() -> SystemActions.openExplorer(v.path()))));
        actions.add(button("Delete version folder (if empty)", Icons.DELETE, () -> deleteVersion(v)));
        stack.push(actions, 18);

        List<ModProject> list = projects.stream().filter(p -> p.mcVersion().equals(v.version())).toList();
        if (!list.isEmpty()) {
            stack.push(Ui.heading("Quick open", "h4"), 6);
            JPanel quick = new JPanel(new GridLayout(0, 2, 8, 8));
            for (ModProject p : list) {
                JButton open = button(p.name() + (p.branch() != null ? "  " + p.branch() : ""), Icons.INTELLIJ, () -> openInIntelliJ(p));
                open.setToolTipText("Open in IntelliJ");
                quick.add(open);
            }
            stack.push(quick, 0);
        }
        return stack.finish();
    }

    private JComponent overview() {
        Ui.Stack stack = new Ui.Stack();
        stack.setBorder(BorderFactory.createEmptyBorder(4, 12, 8, 12));
        stack.push(Ui.heading("MC Mod Project Manager", "h2"), 4);
        Optional<Path> master = config.masterPath();
        if (master.isEmpty()) {
            stack.push(new JLabel("No master folder has been set yet."), 12);
            JPanel row = new JPanel(new FlowLayout(FlowLayout.LEFT, 0, 0));
            JButton choose = Ui.primary("Choose master folder…");
            choose.addActionListener(e -> {
                chooseMaster();
                refresh();
            });
            row.add(choose);
            stack.push(row, 0);
            return stack.finish();
        }
        String sep = java.io.File.separator;
        stack.push(Ui.muted("Structure:  " + master.get() + sep + "<MC version>" + sep + "<project>"), 16);

        Ui.Form info = new Ui.Form();
        info.add("Master folder", Ui.copyable(master.get().toString()));
        info.add("Versions", Ui.copyable(versions.isEmpty() ? "–" : String.join(",  ", versions)));
        info.add("Projects", new JLabel(String.valueOf(projects.size())));
        long dirty = projects.stream().filter(p -> p.changes() > 0).count();
        long unpushed = projects.stream().filter(p -> p.ahead() > 0).count();
        long behind = projects.stream().filter(p -> p.behind() > 0).count();
        info.add("With changes", new JLabel(String.valueOf(dirty)));
        info.add("Not pushed", new JLabel(String.valueOf(unpushed)));
        info.add("Behind remote", new JLabel(String.valueOf(behind)));
        info.add("IntelliJ", Ui.copyable(ideaPath().map(Path::toString).orElse("not found – see settings")));
        stack.push(info, 18);

        JPanel actions = new JPanel(new GridLayout(0, 2, 8, 8));
        actions.add(button("Clone project…", Icons.CLONE, () -> cloneDialog(null, null, selectedVersion())));
        actions.add(button("Import local project…", Icons.IMPORT, () -> importProject(null)));
        actions.add(button("New Minecraft version…", Icons.NEW_FOLDER, this::createVersion));
        actions.add(button("Open master folder", Icons.FOLDER, () -> run(() -> SystemActions.openExplorer(master.get()))));
        actions.add(button("Environment variables (.env)…", Icons.CONFIG, this::editEnvironment));
        stack.push(actions, 24);

        stack.push(Ui.heading("Shared repositories (" + Worktrees.REPOS_DIR + ")", "h3"), 4);
        stack.push(Ui.muted("Each mod has one repository; the version folders are worktrees of it."), 10);
        if (repos.isEmpty()) {
            stack.push(Ui.muted("None yet – pick “Create as worktree” when cloning, or convert existing clones."), 10);
        }
        for (ProjectScanner.SharedRepo r : repos) {
            JPanel row = new JPanel(new BorderLayout(12, 0));
            String versionsText = r.worktrees().isEmpty() ? "<font color='#c77800'>no worktrees</font>"
                    : Ui.html(String.join(", ", r.worktrees().stream().map(ModProject::mcVersion).toList()));
            row.add(new JLabel("<html><b>" + Ui.html(r.name()) + "</b>&nbsp;&nbsp;" + versionsText
                    + "<br><font color='" + Ui.hex(UIManager.getColor("Label.disabledForeground")) + "'>"
                    + Ui.html(r.url().isEmpty() ? "(no remote)" : r.url()) + "</font></html>"), BorderLayout.CENTER);
            JPanel buttons = new JPanel(new FlowLayout(FlowLayout.RIGHT, 6, 0));
            buttons.add(button("Add version…", Icons.ADD, () -> addWorktree(r.path(), r.name(), null, null)));
            if (r.worktrees().isEmpty()) {
                JButton del = button("Delete…", Icons.DELETE, () -> deleteSharedRepo(r));
                del.setForeground(new java.awt.Color(0xD32F2F));
                buttons.add(del);
            }
            row.add(buttons, BorderLayout.EAST);
            stack.push(row, 10);
        }

        long clones = projects.stream().filter(p -> p.standaloneClone() && !p.remoteUrl().isEmpty()).count();
        JPanel maintenance = new JPanel(new GridLayout(0, 2, 8, 8));
        JButton convert = button("Convert all clones (" + clones + ")…", Icons.MERGE, this::convertAll);
        convert.setEnabled(clones > 0);
        convert.setToolTipText("Convert standalone clones into worktrees of shared repositories");
        maintenance.add(convert);
        JButton prune = button("Clean up stale worktree entries", Icons.CLEANUP, this::pruneAll);
        prune.setToolTipText("git worktree prune – removes entries of worktrees whose folder was deleted");
        maintenance.add(prune);
        stack.push(maintenance, 0);
        return stack.finish();
    }

    private void pruneAll() {
        java.util.Set<Path> dirs = new java.util.LinkedHashSet<>();
        projects.stream().map(ModProject::commonDir).filter(java.util.Objects::nonNull).forEach(dirs::add);
        repos.forEach(r -> dirs.add(r.path()));
        background("Cleaning up…", () -> {
            for (Path d : dirs) {
                git.run(d, this::logAsync, "worktree", "prune", "--verbose");
            }
            return dirs.size();
        }, n -> {
            log("✔ " + n + " repositories cleaned up");
            refresh();
        });
    }

    // =====================================================================  Actions

    private void openInIntelliJ(ModProject p) {
        openInIntelliJ(p.path(), p.name());
    }

    private void openInIntelliJ(Path folder, String name) {
        Optional<Path> ide = ideaPath();
        if (ide.isEmpty()) {
            JOptionPane.showMessageDialog(this,
                    "IntelliJ IDEA was not found.\nPlease set the IntelliJ launcher in the settings\n"
                            + "(idea64.exe, idea.sh or the Toolbox \"idea\" script).",
                    "IntelliJ not found", JOptionPane.WARNING_MESSAGE);
            openSettings();
            return;
        }
        log("Opening " + name + " in IntelliJ …");
        run(() -> SystemActions.openInIntelliJ(ide.get(), folder));
    }

    private Optional<Path> ideaPath() {
        Optional<Path> configured = config.ideaPath().filter(Files::isRegularFile);
        if (configured.isPresent()) return configured;
        Optional<Path> detected = SystemActions.detectIntelliJ();
        detected.ifPresent(path -> {
            config.setIdeaPath(path);
            config.save();
        });
        return detected;
    }

    private void cloneDialog(String url, String name, String version) {
        Optional<Path> master = requireMaster();
        if (master.isEmpty()) return;
        CloneDialog.show(this, git, master.get(), versions, url, name, version).ifPresent(req -> {
            Path versionDir = master.get().resolve(req.version());
            Path target = versionDir.resolve(req.name());
            Path repo = Worktrees.sharedRepo(master.get(), req.name());
            log((req.worktree() ? "Worktree " : "Clone ") + req.url() + " [" + req.branch() + "]  →  " + target);
            background("Cloning " + req.name() + " (" + req.branch() + ")…", () -> {
                Files.createDirectories(versionDir);
                if (req.worktree()) {
                    Worktrees.ensureShared(git, repo, req.url(), this::logAsync);
                    Worktrees.add(git, repo, target, req.branch(), null, this::logAsync);
                } else {
                    git.runOrThrow(versionDir, this::logAsync, "clone", "--branch", req.branch(), req.url(), req.name());
                }
                return target;
            }, t -> afterCreate(t, req.name() + " has been cloned."));
        });
    }

    /** Rescan, select the project and offer to open it in IntelliJ. */
    private void afterCreate(Path target, String message) {
        log("✔ " + message);
        reloadAndSelect(target, () -> {
            if (JOptionPane.showConfirmDialog(this, message + " Open it in IntelliJ now?",
                    "Done", JOptionPane.YES_NO_OPTION) == JOptionPane.YES_OPTION) {
                projects.stream().filter(p -> p.path().equals(target)).findFirst().ifPresent(this::openInIntelliJ);
            }
        });
    }

    private void addWorktree(ModProject p) {
        addWorktree(p.path(), p.name(), p.branch(), p.mcVersion());
    }

    /** @param gitDir worktree or repository from which the new worktree is created */
    private void addWorktree(Path gitDir, String name, String currentBranch, String currentVersion) {
        Optional<Path> master = requireMaster();
        if (master.isEmpty()) return;
        String suggestion = versions.stream().filter(v -> !v.equals(currentVersion)).findFirst().orElse(null);
        WorktreeDialog.show(this, git, gitDir, master.get(), name, currentBranch, versions, suggestion).ifPresent(req -> {
            Path target = master.get().resolve(req.version()).resolve(req.name());
            log("Worktree " + (req.newBranch() != null ? "new branch " + req.newBranch() + " (from " + req.branch() + ")" : req.branch())
                    + "  →  " + target);
            background("Creating worktree…", () -> {
                Worktrees.add(git, gitDir, target, req.branch(), req.newBranch(), this::logAsync);
                return target;
            }, t -> afterCreate(t, "Worktree for Minecraft " + req.version() + " has been created."));
        });
    }

    private void convertToShared(ModProject p) {
        Path repo = Worktrees.sharedRepo(config.masterPath().orElseThrow(), p.name());
        List<ModProject> sib = siblings(p);
        String text;
        if (Files.isDirectory(repo)) {
            text = "A shared repository for “" + Ui.html(p.name()) + "” already exists.<br><br>"
                    + "The local branches of this clone are transferred there and its <tt>.git</tt> folder "
                    + "is removed afterwards.<br>Local Git settings and hooks of this clone will be lost.";
        } else {
            text = "The <tt>.git</tt> folder is moved to<br><tt>" + Ui.html(repo.toString()) + "</tt><br>"
                    + "and the project becomes its first worktree."
                    + (sib.isEmpty() ? "" : "<br>The " + sib.size() + " linked worktree(s) are relinked automatically.");
        }
        text += "<br><br>All files in the project folder (including <tt>run/</tt>, <tt>build/</tt> …) stay untouched."
                + "<br><b>Please close the project in IntelliJ first.</b>";
        if (JOptionPane.showConfirmDialog(this, "<html>" + text + "</html>", "Convert to shared repository",
                JOptionPane.OK_CANCEL_OPTION, JOptionPane.QUESTION_MESSAGE) != JOptionPane.OK_OPTION) {
            return;
        }
        background("Converting " + p.name() + "…", () -> {
            Worktrees.convertToShared(git, p.path(), repo, this::logAsync);
            return p.path();
        }, t -> reloadAndSelect(t, null));
    }

    private void convertAll() {
        Path master = config.masterPath().orElseThrow();
        List<ModProject> clones = projects.stream()
                .filter(ModProject::standaloneClone)
                .filter(p -> !p.remoteUrl().isEmpty())
                .toList();
        if (clones.isEmpty()) {
            JOptionPane.showMessageDialog(this, "There are no standalone clones with a remote URL.");
            return;
        }
        if (JOptionPane.showConfirmDialog(this, "<html>Convert " + clones.size() + " clone(s) into shared repositories "
                        + "(one per mod name)?<br>Working files stay untouched.<br><br>"
                        + "<b>Please close all affected projects in IntelliJ first.</b></html>",
                "Convert all", JOptionPane.OK_CANCEL_OPTION, JOptionPane.QUESTION_MESSAGE) != JOptionPane.OK_OPTION) {
            return;
        }
        background("Converting " + clones.size() + " clone(s)…", () -> {
            List<String> failed = new ArrayList<>();
            for (ModProject p : clones) {
                logAsync("> " + p.mcVersion() + "/" + p.name());
                try {
                    Worktrees.convertToShared(git, p.path(), Worktrees.sharedRepo(master, p.name()), this::logAsync);
                } catch (Exception e) {
                    logAsync("✖ " + e.getMessage());
                    failed.add(p.mcVersion() + "/" + p.name() + ": " + e.getMessage().lines().findFirst().orElse(""));
                }
            }
            return failed;
        }, failed -> {
            if (failed.isEmpty()) {
                log("✔ All clones converted");
            } else {
                error("Not all converted", String.join("\n\n", failed) + "\n\nSee the Git log for details.");
            }
            refresh();
        });
    }

    private void deleteSharedRepo(ProjectScanner.SharedRepo r) {
        background("Checking " + r.name() + "…", () -> Worktrees.unpushedCommits(git, r.path()), unpushed -> {
            String warning = unpushed > 0
                    ? "<br><br><font color='#d32f2f'><b>Warning:</b> " + unpushed
                    + " commit(s) on local branches are not pushed and will be lost!</font>"
                    : "";
            if (JOptionPane.showConfirmDialog(this, "<html>Permanently delete the shared repository?<br><tt>"
                            + Ui.html(r.path().toString()) + "</tt>" + warning + "</html>",
                    "Delete repository", JOptionPane.YES_NO_OPTION, JOptionPane.WARNING_MESSAGE) != JOptionPane.YES_OPTION) {
                return;
            }
            background("Deleting " + r.name() + "…", () -> {
                SystemActions.deleteRecursively(r.path());
                return null;
            }, x -> {
                log("✔ Repository " + r.name() + " deleted");
                refresh();
            });
        });
    }

    private String repoDescription(ModProject p) {
        int others = siblings(p).size();
        if (p.linkedWorktree()) {
            if (isShared(p)) {
                return "<html>Worktree &nbsp;·&nbsp; shared repo <tt>" + Ui.html(Worktrees.REPOS_DIR + "/" + p.commonDir().getFileName()) + "</tt></html>";
            }
            Path main = p.commonDir() == null ? null : p.commonDir().getParent();
            return "<html>Worktree of <tt>" + Ui.html(String.valueOf(main)) + "</tt></html>";
        }
        return others > 0 ? "Standalone clone · main repository for " + others + " worktree(s)" : "Standalone clone";
    }

    private void importProject(String presetVersion) {
        Optional<Path> master = requireMaster();
        if (master.isEmpty()) return;

        JTextField source = new JTextField(32);
        JButton browse = new JButton("…");
        JComboBox<String> version = new JComboBox<>(versions.toArray(String[]::new));
        version.setEditable(true);
        if (presetVersion != null) version.setSelectedItem(presetVersion);
        JTextField name = new JTextField();
        JRadioButton move = new JRadioButton("Move", true);
        JRadioButton copy = new JRadioButton("Copy");
        ButtonGroup group = new ButtonGroup();
        group.add(move);
        group.add(copy);
        JPanel mode = new JPanel(new FlowLayout(FlowLayout.LEFT, 0, 0));
        mode.add(move);
        mode.add(javax.swing.Box.createHorizontalStrut(12));
        mode.add(copy);

        browse.addActionListener(e -> Ui.chooseDirectory(this, "Choose project folder", null).ifPresent(dir -> {
            source.setText(dir.toString());
            if (name.getText().isBlank()) name.setText(dir.getFileName().toString());
        }));

        Ui.Form form = new Ui.Form()
                .add("Project folder", source, browse)
                .add("Minecraft version", version)
                .add("Name", name)
                .add("Mode", mode);

        if (JOptionPane.showConfirmDialog(this, form, "Import local project",
                JOptionPane.OK_CANCEL_OPTION, JOptionPane.PLAIN_MESSAGE) != JOptionPane.OK_OPTION) {
            return;
        }
        String v = String.valueOf(version.getEditor().getItem()).trim();
        String n = name.getText().trim();
        Path src;
        try {
            src = Path.of(source.getText().trim());
        } catch (InvalidPathException e) {
            error("Invalid path", e.getMessage());
            return;
        }
        if (!Files.isDirectory(src) || v.isEmpty() || n.isEmpty()) {
            error("Import not possible", "Please specify project folder, version and name.");
            return;
        }
        Path target = master.get().resolve(v).resolve(n);
        if (Files.exists(target)) {
            error("Import not possible", "Target already exists:\n" + target);
            return;
        }
        if (target.startsWith(src)) {
            error("Import not possible", "The target is inside the source folder.");
            return;
        }
        boolean doMove = move.isSelected();
        log((doMove ? "Moving " : "Copying ") + src + "  →  " + target);
        background("Importing " + n + "…", () -> {
            Files.createDirectories(target.getParent());
            if (doMove) SystemActions.move(src, target);
            else SystemActions.copyRecursively(src, target);
            return target;
        }, t -> {
            log("✔ Import finished");
            reloadAndSelect(t, null);
        });
    }

    private void moveProject(ModProject p) {
        JComboBox<String> version = new JComboBox<>(versions.toArray(String[]::new));
        version.setEditable(true);
        version.setSelectedItem(p.mcVersion());
        JTextField name = new JTextField(p.name(), 24);
        Ui.Form form = new Ui.Form().add("Minecraft version", version).add("Name", name);
        if (JOptionPane.showConfirmDialog(this, form, "Move / rename project",
                JOptionPane.OK_CANCEL_OPTION, JOptionPane.PLAIN_MESSAGE) != JOptionPane.OK_OPTION) {
            return;
        }
        String v = String.valueOf(version.getEditor().getItem()).trim();
        String n = name.getText().trim();
        Path target = config.masterPath().orElseThrow().resolve(v).resolve(n);
        if (v.isEmpty() || n.isEmpty() || target.equals(p.path())) return;
        if (Files.exists(target)) {
            error("Not possible", "Target already exists:\n" + target);
            return;
        }
        boolean repairLinks = p.standaloneClone() && !siblings(p).isEmpty();
        log("Moving " + p.path() + "  →  " + target);
        background("Moving " + p.name() + "…", () -> {
            Files.createDirectories(target.getParent());
            if (p.linkedWorktree()) {
                Worktrees.move(git, p.commonDir(), p.path(), target, this::logAsync);
            } else {
                SystemActions.move(p.path(), target);
                if (repairLinks) {
                    git.runOrThrow(target, this::logAsync, "worktree", "repair");
                }
            }
            return target;
        }, t -> reloadAndSelect(t, null));
    }

    private void deleteProject(ModProject p) {
        List<ModProject> sib = siblings(p);
        if (p.standaloneClone() && !sib.isEmpty()) {
            error("Cannot delete", "This clone contains the repository for " + sib.size() + " other worktree(s):\n  "
                    + String.join("\n  ", sib.stream().map(s -> s.mcVersion() + "/" + s.name()).toList())
                    + "\n\nConvert it to a shared repository first (“Convert to shared repo…”)"
                    + "\nor remove the other worktrees first.");
            return;
        }
        // For worktrees, commits stay in the repository – only uncommitted changes are lost
        boolean atRisk = p.linkedWorktree() ? p.changes() > 0 : p.hasUnsavedWork();
        String warning = "";
        if (atRisk) {
            warning = "<br><br><font color='#d32f2f'><b>Warning:</b> "
                    + (p.changes() > 0 ? p.changes() + " uncommitted change(s). " : "")
                    + (!p.linkedWorktree() && p.ahead() > 0 ? p.ahead() + " commit(s) not pushed." : "")
                    + "<br>These will be lost!</font>";
        }
        String note = p.linkedWorktree() ? "<br><br>The branch “" + Ui.html(p.branch()) + "” and all commits stay in the repository." : "";
        int answer = JOptionPane.showConfirmDialog(this,
                "<html>Really <b>permanently delete</b> the project folder?<br><tt>" + Ui.html(p.path().toString()) + "</tt>"
                        + note + warning + "</html>",
                "Remove project", JOptionPane.YES_NO_OPTION, JOptionPane.WARNING_MESSAGE);
        if (answer != JOptionPane.YES_OPTION) return;
        if (atRisk) {
            String typed = JOptionPane.showInputDialog(this, "Type the project name to confirm:",
                    "Confirm deletion", JOptionPane.WARNING_MESSAGE);
            if (typed == null || !typed.trim().equals(p.name())) return;
        }
        boolean lastOfShared = p.linkedWorktree() && isShared(p) && sib.isEmpty();
        log("Deleting " + p.path());
        background("Deleting " + p.name() + "…", () -> {
            if (p.linkedWorktree()) {
                Worktrees.remove(git, p.commonDir(), p.path(), this::logAsync);
            } else {
                SystemActions.deleteRecursively(p.path());
            }
            return null;
        }, x -> {
            log("✔ deleted");
            refresh();
            if (lastOfShared) {
                repos.stream().filter(r -> r.path().equals(p.commonDir())).findFirst().ifPresent(r -> {
                    if (JOptionPane.showConfirmDialog(this, "<html>That was the last worktree of <b>" + Ui.html(r.name())
                                    + "</b>.<br>Delete the shared repository as well?</html>",
                            "Keep repository?", JOptionPane.YES_NO_OPTION) == JOptionPane.YES_OPTION) {
                        deleteSharedRepo(r);
                    }
                });
            }
        });
    }

    private void createVersion() {
        Optional<Path> master = requireMaster();
        if (master.isEmpty()) return;
        String version = JOptionPane.showInputDialog(this, "Minecraft version (e.g. 1.21.1):",
                "New version", JOptionPane.PLAIN_MESSAGE);
        if (version == null || version.isBlank()) return;
        version = version.trim();
        if (!version.matches("[\\w.\\-+]+")) {
            error("Invalid name", "Allowed are letters, digits, dot, minus, plus and underscore.");
            return;
        }
        Path dir = master.get().resolve(version);
        String key = "v:" + version;
        run(() -> Files.createDirectories(dir));
        background("Scanning…", () -> ProjectScanner.scan(master.get(), git), result -> {
            apply(result);
            rebuildTree();
            reselect(key);
            updateStatus();
        });
    }

    private void deleteVersion(VersionEntry v) {
        try {
            if (!SystemActions.isEmptyDirectory(v.path())) {
                error("Not empty", "The folder still contains projects or files.");
                return;
            }
            Files.delete(v.path());
            log("Version folder " + v.version() + " deleted");
            refresh();
        } catch (Exception e) {
            error("Error", e.getMessage());
        }
    }

    private record FetchTarget(Path dir, String label) {
    }

    /** Worktrees share one repository → each repository is fetched only once. */
    private List<FetchTarget> fetchTargets() {
        Map<Path, FetchTarget> targets = new LinkedHashMap<>();
        for (ModProject p : projects) {
            if (!p.git()) continue;
            Path key = p.commonDir() != null ? p.commonDir() : p.path();
            targets.putIfAbsent(key, new FetchTarget(p.path(), p.linkedWorktree() ? p.name() : p.mcVersion() + "/" + p.name()));
        }
        for (ProjectScanner.SharedRepo r : repos) {
            targets.putIfAbsent(r.path(), new FetchTarget(r.path(), r.name()));
        }
        return new ArrayList<>(targets.values());
    }

    private void fetchAll() {
        List<FetchTarget> targets = fetchTargets();
        if (targets.isEmpty()) return;
        background("Fetching " + targets.size() + " repositories…", () -> {
            int failed = 0;
            for (FetchTarget t : targets) {
                logAsync("> fetch " + t.label());
                if (!git.run(t.dir(), this::logAsync, "fetch", "--all", "--prune").ok()) failed++;
            }
            return failed;
        }, failed -> {
            log(failed == 0 ? "✔ All repositories fetched" : "✖ " + failed + " fetch(es) failed");
            refresh();
        });
    }

    /** (Re)starts the background fetch timer according to the settings. */
    private void scheduleAutoFetch() {
        if (autoFetchTimer != null) autoFetchTimer.stop();
        int minutes = config.autoFetchMinutes();
        if (minutes <= 0) {
            autoFetchTimer = null;
            return;
        }
        autoFetchTimer = new javax.swing.Timer(minutes * 60_000, e -> autoFetch());
        autoFetchTimer.start();
    }

    /** Quiet fetch: no busy indicator, no error dialogs – one summary line in the log. */
    private void autoFetch() {
        List<FetchTarget> targets = fetchTargets();
        if (targets.isEmpty() || runningTasks > 0 || autoFetchRunning) return;
        autoFetchRunning = true;
        new SwingWorker<Integer, Void>() {
            @Override
            protected Integer doInBackground() {
                int failed = 0;
                for (FetchTarget t : targets) {
                    if (!git.run(t.dir(), "fetch", "--all", "--prune", "--quiet").ok()) failed++;
                }
                return failed;
            }

            @Override
            protected void done() {
                autoFetchRunning = false;
                try {
                    int failed = get();
                    log("Auto-fetch: " + targets.size() + " repositories" + (failed > 0 ? " (" + failed + " failed)" : ""));
                } catch (Exception ignored) {
                    // logged as nothing – next run will try again
                }
                if (runningTasks == 0) refresh();
            }
        }.execute();
    }

    private void gitTask(String label, Path dir, String... args) {
        log("> git " + String.join(" ", args) + "    (" + dir + ")");
        background(label + "…", () -> git.run(dir, this::logAsync, args), result -> {
            log(result.ok() ? "✔ " + label + " done" : "✖ " + label + " failed (exit code " + result.exitCode() + ")");
            if (!result.ok()) error(label + " failed", result.output());
            refresh();
        });
    }

    private void commitDialog(ModProject p) {
        commitDialog(p, "");
    }

    private void commitDialog(ModProject p, String message) {
        CommitDialog.show(this, git, p.path(), p.mcVersion() + "/" + p.name(), message).ifPresent(req -> {
            log("Committing " + req.selected().size() + " file(s) in " + p.mcVersion() + "/" + p.name() + (req.push() ? " and pushing" : ""));
            background(req.push() ? "Committing and pushing…" : "Committing…", () -> {
                GitChanges.commit(git, p.path(), req.all(), req.selected(), req.message(), this::logAsync);
                if (req.push()) GitChanges.push(git, p.path(), this::logAsync);
                return p.path();
            }, path -> {
                log(req.push() ? "✔ Committed and pushed" : "✔ Committed");
                reloadAndSelect(path, null);
            });
        });
    }

    private void push(ModProject p) {
        log("> git push    (" + p.path() + ")");
        background("Pushing " + p.name() + "…", () -> {
            GitChanges.push(git, p.path(), this::logAsync);
            return p.path();
        }, path -> {
            log("✔ Pushed");
            reloadAndSelect(path, null);
        });
    }

    // =====================================================================  Dependencies

    private static String dependencyKey(Dependencies.Entry d) {
        return d.key() != null ? d.key() : "gradle-wrapper";
    }

    /** Looks up the latest versions; results (or "!error") are kept per project for this session. */
    private void checkUpdates(ModProject p, List<Dependencies.Entry> deps) {
        String mc = p.gradleMcVersion() != null ? p.gradleMcVersion() : p.mcVersion();
        String parchmentMc = Dependencies.parchmentMinecraft(p.path());
        background("Checking for updates…", () -> {
            Map<String, String> result = new LinkedHashMap<>();
            for (Dependencies.Entry d : deps) {
                if (!d.checkable()) continue;
                try {
                    Dependencies.latest(d, mc, parchmentMc).ifPresent(v -> result.put(dependencyKey(d), v));
                } catch (Exception e) {
                    result.put(dependencyKey(d), "!" + e.getMessage());
                }
            }
            return result;
        }, result -> {
            latestVersions.put(p.path(), result);
            long updates = deps.stream()
                    .filter(d -> Dependencies.isNewer(result.getOrDefault(dependencyKey(d), "!"), d.value()))
                    .count();
            log(p.mcVersion() + "/" + p.name() + ": " + (updates == 0 ? "all dependencies up to date" : updates + " update(s) available"));
            if (shownDetail instanceof ModProject shown && shown.path().equals(p.path())) showDetails(shown);
        });
    }

    private void updateDependency(ModProject p, Dependencies.Entry d, String version) {
        try {
            Dependencies.update(p.path(), d.key(), version);
            log("Updated " + d.key() + ": " + d.value() + " → " + version + "  (" + p.mcVersion() + "/" + p.name() + ")");
            reloadAndSelect(p.path(), null);
        } catch (Exception e) {
            error("Update failed", e.getMessage());
        }
    }

    // =====================================================================  Gradle

    private void runGradle(ModProject p, String task) {
        String name = task.substring(task.lastIndexOf(':') + 1);
        boolean publish = name.equals("publish") || name.equals("publishMods");
        Map<String, String> env = Map.of();
        if (publish) {
            env = publishEnvironment();
            Map<String, String> vars0 = env;
            List<String> missing = EnvFile.EXPECTED.stream().filter(k -> vars0.getOrDefault(k, "").isBlank()).toList();
            String vars = (env.isEmpty() ? "No variables in <tt>.env</tt>."
                    : "Environment from <tt>.env</tt>: " + Ui.html(String.join(", ", env.keySet())))
                    + (missing.isEmpty() ? "" : "<br><font color='#c77800'>Missing or empty: " + Ui.html(String.join(", ", missing)) + "</font>")
                    + changelogWarning(p);
            if (JOptionPane.showConfirmDialog(this,
                    "<html><b>gradlew " + Ui.html(task) + "</b><br>This publishes the mod (Maven / Modrinth / CurseForge …) for "
                            + Ui.html(p.mcVersion() + "/" + p.name()) + ".<br>" + vars + "<br><br>Run it now?</html>",
                    "Publish", JOptionPane.YES_NO_OPTION, JOptionPane.WARNING_MESSAGE) != JOptionPane.YES_OPTION) {
                return;
            }
        }
        String tag = "[" + p.mcVersion() + "/" + p.name() + "] ";
        // only the names are logged – values usually are secrets
        log("> gradlew " + task + (env.isEmpty() ? "" : "  (with " + env.size() + " variable(s) from .env)") + "    (" + p.path() + ")");
        try {
            gradle.start(p.path(), task, env, line -> logAsync(tag + line), exit -> SwingUtilities.invokeLater(() -> {
                log(exit == 0 ? "✔ " + tag + task + " finished" : "✖ " + tag + task + " ended (exit code " + exit + ")");
                gradleStateChanged(p);
            }));
        } catch (Exception e) {
            error("Gradle", e.getMessage());
        }
        gradleStateChanged(p);
    }

    /** HTML line for the publish confirmation if CHANGELOG.md exists but is not for the current mod_version. */
    private static String changelogWarning(ModProject p) {
        Path file = p.path().resolve(Changelog.FILE);
        if (p.modVersion() == null || !Files.isRegularFile(file)) return "";
        try {
            String expected = Changelog.displayVersion(p.modVersion(), p.gradleMcVersion() != null ? p.gradleMcVersion() : p.mcVersion());
            String found = Changelog.detectVersion(Files.readString(file)).orElse(null);
            if (found != null && found.replaceFirst("^[vV]", "").equals(expected)) return "";
            return "<br><font color='#c77800'>CHANGELOG.md is for " + Ui.html(found == null ? "an unknown version" : found)
                    + ", not " + Ui.html(expected) + " (see the Changelog tab)</font>";
        } catch (Exception e) {
            return "";
        }
    }

    /** Claude tokens from {@code CLAUDE_API} (.env, then system environment); empty = Claude Code login. */
    private List<String> claudeApiKeys() {
        return ClaudeKeys.resolve(envVariables(), System.getenv());
    }

    /** For the settings: which credentials Claude runs use. */
    private String claudeSignIn() {
        List<String> keys = claudeApiKeys();
        return keys.isEmpty()
                ? "Claude Code login – set CLAUDE_API (tokens from claude setup-token) to use other accounts"
                : keys.size() + " token(s) from CLAUDE_API, tried in order";
    }

    /** .env for the Gradle publish tasks – without the Claude tokens, Gradle does not need them. */
    private Map<String, String> publishEnvironment() {
        Map<String, String> env = new LinkedHashMap<>(envVariables());
        env.remove(ClaudeKeys.VARIABLE);
        return env;
    }

    /** Variables from {@code <master>/.env}; empty if there is none or it cannot be read. */
    private Map<String, String> envVariables() {
        Optional<Path> master = config.masterPath();
        if (master.isEmpty()) return Map.of();
        try {
            return EnvFile.load(master.get());
        } catch (Exception e) {
            log("✖ Could not read .env: " + e.getMessage());
            return Map.of();
        }
    }

    private void editEnvironment() {
        requireMaster().ifPresent(master -> {
            EnvDialog.show(this, master, this::log);
            if (shownDetail != null) showDetails(shownDetail);
        });
    }

    /** Root tasks: "General" for multiloader builds, otherwise the loader of the project. */
    private static String rootTaskGroupName(ModProject p) {
        boolean subprojects = List.of("common", "fabric", "quilt", "forge", "neoforge").stream()
                .anyMatch(s -> Files.isRegularFile(p.path().resolve(s).resolve("build.gradle"))
                        || Files.isRegularFile(p.path().resolve(s).resolve("build.gradle.kts")));
        if (subprojects || p.loader() == null || p.loader().startsWith("Multiloader") || p.loader().equals("Architectury")) {
            return "General";
        }
        return p.loader();
    }

    private void detectTasks(ModProject p) {
        if (!detectingTasks.add(p.path())) return;
        String tag = "[" + p.mcVersion() + "/" + p.name() + "] ";
        log("> gradlew tasks --all    (" + p.path() + ")");
        gradleStateChanged(p);
        background("Detecting run configurations…", () -> {
            try {
                return GradleTasks.detect(gradle, p.path(), line -> logAsync(tag + line));
            } finally {
                // also on failure (the error dialog is shown by background())
                SwingUtilities.invokeLater(() -> {
                    detectingTasks.remove(p.path());
                    gradleStateChanged(p);
                });
            }
        }, tasks -> log("✔ " + tag + tasks.size() + " run configuration(s) found"));
    }

    /** Starts the task, or – while it runs – stops it. Disabled when the loader already runs the maximum. */
    private JButton gradleButton(ModProject p, String task, String label) {
        if (gradle.isRunning(p.path(), task)) {
            JButton stop = button(label, Icons.STOP, () -> stopGradle(p, task));
            stop.setForeground(new java.awt.Color(0xD32F2F));
            stop.setToolTipText("Running – click to stop");
            return stop;
        }
        JButton b = button(label, gradleIcon(task), () -> runGradle(p, task));
        b.setEnabled(gradle.canStart(p.path(), task));
        if (!b.isEnabled()) b.setToolTipText(GradleRunner.MAX_PER_LOADER + " tasks of this loader are already running");
        return b;
    }

    private void stopGradle(ModProject p, String task) {
        log("Stopping " + task + " in " + p.mcVersion() + "/" + p.name() + " …");
        gradle.stop(p.path(), task);
    }

    /** Repaints the tree and refreshes the details if they show {@code p}. */
    private void gradleStateChanged(ModProject p) {
        tree.repaint();
        if (shownDetail instanceof ModProject shown && shown.path().equals(p.path())) {
            showDetails(shown);
        }
    }

    private void openBuildLibs(ModProject p) {
        // Multiloader projects put their jars into <subproject>/build/libs
        List<Path> candidates = new ArrayList<>(List.of(p.path().resolve("build/libs")));
        for (String sub : List.of("fabric", "neoforge", "forge", "quilt", "common")) {
            candidates.add(p.path().resolve(sub).resolve("build/libs"));
        }
        List<Path> existing = candidates.stream().filter(Files::isDirectory).toList();
        if (existing.isEmpty()) {
            error("No build output", "build/libs does not exist yet – run “Build” first.");
        } else if (existing.size() == 1) {
            run(() -> SystemActions.openExplorer(existing.getFirst()));
        } else {
            run(() -> SystemActions.openExplorer(p.path()));
            log("Build output: " + String.join(", ", existing.stream().map(d -> p.path().relativize(d).toString()).toList()));
        }
    }

    private void buildAllVersions(String modName) {
        List<ModProject> targets = projects.stream()
                .filter(o -> o.name().equals(modName) && GradleRunner.wrapper(o.path()).isPresent())
                .filter(o -> !gradle.isRunning(o.path()))
                .toList();
        if (targets.isEmpty()) return;
        background("Building " + targets.size() + " versions of " + modName + "…", () -> {
            List<String> results = new ArrayList<>();
            for (ModProject p : targets) {
                String tag = "[" + p.mcVersion() + "/" + p.name() + "] ";
                logAsync("> gradlew build    (" + p.path() + ")");
                SwingUtilities.invokeLater(() -> gradleStateChanged(p));
                int exit = gradle.run(p.path(), "build", line -> logAsync(tag + line));
                SwingUtilities.invokeLater(() -> gradleStateChanged(p));
                results.add((exit == 0 ? "✔ " : "✖ ") + p.mcVersion() + (exit == 0 ? "" : "  (exit code " + exit + ")"));
            }
            return results;
        }, results -> {
            results.forEach(this::log);
            boolean allOk = results.stream().allMatch(r -> r.startsWith("✔"));
            JOptionPane.showMessageDialog(this, String.join("\n", results), "Build results – " + modName,
                    allOk ? JOptionPane.INFORMATION_MESSAGE : JOptionPane.WARNING_MESSAGE);
        });
    }

    /** The tray icon is shown while "minimize to tray" is enabled. */
    private void applyTraySetting() {
        if (config.minimizeToTray() && TrayManager.supported()) tray.install();
        else tray.uninstall();
    }

    /** Close button: asks once whether to minimize or exit, then remembers the answer (changeable in the settings). */
    private void closeRequested() {
        Config.CloseAction action = config.closeAction();
        if (action == Config.CloseAction.ASK) {
            JCheckBox remember = new JCheckBox("Remember my choice (can be changed in the settings)", true);
            Object[] message = {"Should the Project Manager keep running in the background\nor exit completely?", remember};
            int choice = JOptionPane.showOptionDialog(this, message, "Close Project Manager",
                    JOptionPane.DEFAULT_OPTION, JOptionPane.QUESTION_MESSAGE, null,
                    new String[]{"Minimize", "Exit", "Cancel"}, "Minimize");
            if (choice != 0 && choice != 1) return;
            action = choice == 0 ? Config.CloseAction.MINIMIZE : Config.CloseAction.EXIT;
            if (remember.isSelected()) {
                config.setCloseAction(action);
                config.save();
            }
        }
        if (action == Config.CloseAction.EXIT) {
            exitApplication();
        } else if (TrayManager.supported()) {
            tray.hideToTray();
            if (!tray.installed()) setExtendedState(ICONIFIED);
        } else {
            setExtendedState(ICONIFIED);
        }
    }

    private void exitApplication() {
        int running = gradle.runningCount();
        if (running > 0 && JOptionPane.showConfirmDialog(this,
                running + " Gradle task(s) are still running. Stop them and exit?",
                "Exit", JOptionPane.YES_NO_OPTION, JOptionPane.WARNING_MESSAGE) != JOptionPane.YES_OPTION) {
            return;
        }
        gradle.stopAll();
        tray.uninstall();
        dispose();
        System.exit(0);
    }

    // =====================================================================  Settings

    private void chooseMaster() {
        Ui.chooseDirectory(this, "Choose master folder", config.masterPath().orElse(null)).ifPresent(dir -> {
            config.setMasterPath(dir);
            config.save();
        });
    }

    private Optional<Path> requireMaster() {
        Optional<Path> master = config.masterPath().filter(Files::isDirectory);
        if (master.isEmpty()) {
            chooseMaster();
            master = config.masterPath().filter(Files::isDirectory);
        }
        return master;
    }

    private void openSettings() {
        JTextField master = new JTextField(config.masterPath().map(Path::toString).orElse(""), 36);
        JButton masterBrowse = new JButton("…");
        masterBrowse.addActionListener(e -> Ui.chooseDirectory(this, "Choose master folder", null)
                .ifPresent(d -> master.setText(d.toString())));

        JTextField idea = new JTextField(config.ideaPath().map(Path::toString).orElse(""));
        JButton detect = new JButton("Detect");
        detect.addActionListener(e -> SystemActions.detectIntelliJ().ifPresentOrElse(
                d -> idea.setText(d.toString()),
                () -> JOptionPane.showMessageDialog(this, "No installation found.")));

        JTextField gitField = new JTextField(config.gitExecutable());
        javax.swing.JSpinner fetchMinutes = new javax.swing.JSpinner(
                new javax.swing.SpinnerNumberModel(config.autoFetchMinutes(), 0, 240, 5));
        JPanel fetchRow = new JPanel(new FlowLayout(FlowLayout.LEFT, 6, 0));
        fetchRow.add(fetchMinutes);
        fetchRow.add(Ui.muted("minutes (0 = off)"));
        JCheckBox tray = new JCheckBox("Minimize to system tray", config.minimizeToTray());

        JComboBox<String> closeAction = new JComboBox<>(new String[]{"Ask", "Minimize", "Exit"});
        closeAction.setSelectedIndex(config.closeAction().ordinal());

        JCheckBox autostart = new JCheckBox("Start minimized with the system", config.autostart());
        autostart.setEnabled(Autostart.supported());
        if (!Autostart.supported()) {
            autostart.setToolTipText("Only available when started from the jar file on Windows or Linux");
        }

        JTextField claudeField = new JTextField(config.claudeExecutable());
        JButton claudeDetect = new JButton("Detect");
        claudeDetect.addActionListener(e -> ClaudeRunner.find(null).ifPresentOrElse(
                p -> claudeField.setText(p.toString()),
                () -> JOptionPane.showMessageDialog(this, "Claude Code CLI not found on the PATH.")));
        JTextField claudeModel = new JTextField(config.claudeModel());
        claudeModel.putClientProperty("JTextField.placeholderText", "sonnet (default) or opus");
        JTextField backportModel = new JTextField(config.backportModel());
        backportModel.putClientProperty("JTextField.placeholderText", BackportClaude.DEFAULT_MODEL + " (default)");
        javax.swing.JSpinner claudeBudget = new javax.swing.JSpinner(
                new javax.swing.SpinnerNumberModel(config.claudeMaxBudgetUsd(), 0.0, 100.0, 0.5));
        javax.swing.JSpinner claudeTimeout = new javax.swing.JSpinner(
                new javax.swing.SpinnerNumberModel(config.claudeTimeoutMinutes(), 1, 240, 5));
        JPanel claudeLimits = new JPanel(new FlowLayout(FlowLayout.LEFT, 6, 0));
        claudeLimits.add(claudeBudget);
        claudeLimits.add(Ui.muted("USD (0 = no limit), stop after"));
        javax.swing.JSpinner backportBudget = new javax.swing.JSpinner(
                new javax.swing.SpinnerNumberModel(config.backportMaxBudgetUsd(), 0.0, 100.0, 1.0));
        JPanel backportModelRow = new JPanel(new BorderLayout(6, 0));
        backportModelRow.add(backportModel, BorderLayout.CENTER);
        JPanel backportBudgetRow = new JPanel(new FlowLayout(FlowLayout.LEFT, 6, 0));
        backportBudgetRow.add(backportBudget);
        backportBudgetRow.add(Ui.muted("USD per run"));
        backportModelRow.add(backportBudgetRow, BorderLayout.EAST);
        claudeLimits.add(claudeTimeout);
        claudeLimits.add(Ui.muted("minutes"));

        Ui.Form form = new Ui.Form()
                .add("Master folder", master, masterBrowse)
                .add("IntelliJ launcher", idea, detect)
                .add("Git executable", gitField)
                .add("Auto-fetch every", fetchRow)
                .add("", tray)
                .add("On close", closeAction)
                .add("", autostart)
                .add("Claude Code CLI", claudeField, claudeDetect)
                .add("Changelog model", claudeModel)
                .add("Backport model", backportModelRow)
                .add("Claude budget per run", claudeLimits)
                .add("Claude sign-in", Ui.muted(claudeSignIn()));

        if (JOptionPane.showConfirmDialog(this, form, "Settings",
                JOptionPane.OK_CANCEL_OPTION, JOptionPane.PLAIN_MESSAGE) != JOptionPane.OK_OPTION) {
            return;
        }
        try {
            config.setMasterPath(master.getText().isBlank() ? null : Path.of(master.getText().trim()));
            config.setIdeaPath(idea.getText().isBlank() ? null : Path.of(idea.getText().trim()));
        } catch (InvalidPathException e) {
            error("Invalid path", e.getMessage());
            return;
        }
        config.setGitExecutable(gitField.getText());
        git.setExecutable(config.gitExecutable());
        config.setAutoFetchMinutes((Integer) fetchMinutes.getValue());
        config.setMinimizeToTray(tray.isSelected());
        config.setCloseAction(Config.CloseAction.values()[closeAction.getSelectedIndex()]);
        if (autostart.isEnabled()) {
            try {
                Autostart.apply(autostart.isSelected());
                config.setAutostart(autostart.isSelected());
            } catch (java.io.IOException e) {
                error("Autostart", e.getMessage());
            }
        }
        config.setClaudeExecutable(claudeField.getText());
        config.setClaudeModel(claudeModel.getText());
        config.setBackportModel(backportModel.getText());
        config.setBackportMaxBudgetUsd(((Number) backportBudget.getValue()).doubleValue());
        config.setClaudeMaxBudgetUsd(((Number) claudeBudget.getValue()).doubleValue());
        config.setClaudeTimeoutMinutes((Integer) claudeTimeout.getValue());
        config.save();
        scheduleAutoFetch();
        applyTraySetting();
        refresh();
    }

    // =====================================================================  Helpers

    private void maybePopup(MouseEvent e) {
        if (!e.isPopupTrigger()) return;
        int row = tree.getRowForLocation(e.getX(), e.getY());
        if (row < 0) return;
        tree.setSelectionRow(row);
        Object o = selectedObject();
        JPopupMenu menu = new JPopupMenu();
        if (o instanceof ModProject p) {
            menu.add(menuItem("Open in IntelliJ", Icons.INTELLIJ, () -> openInIntelliJ(p)));
            menu.add(menuItem("Open in Explorer", Icons.FOLDER, () -> run(() -> SystemActions.openExplorer(p.path()))));
            menu.add(menuItem("Open terminal", Icons.TERMINAL, () -> run(() -> SystemActions.openTerminal(p.path()))));
            if (p.git()) {
                menu.addSeparator();
                menu.add(menuItem("Fetch", Icons.FETCH, () -> gitTask("Fetch " + p.name(), p.path(), "fetch", "--all", "--prune")));
                menu.add(menuItem("Pull", Icons.PULL, () -> gitTask("Pull " + p.name(), p.path(), "pull", "--ff-only")));
                menu.add(menuItem("Add version (worktree)…", Icons.ADD, () -> addWorktree(p)));
            }
            menu.addSeparator();
            menu.add(menuItem("Remove…", Icons.DELETE, () -> deleteProject(p)));
        } else if (o instanceof VersionEntry v) {
            menu.add(menuItem("Clone project here…", Icons.CLONE, () -> cloneDialog(null, null, v.version())));
            menu.add(menuItem("Import project…", Icons.IMPORT, () -> importProject(v.version())));
            menu.add(menuItem("Open folder", Icons.FOLDER, () -> run(() -> SystemActions.openExplorer(v.path()))));
        } else {
            menu.add(menuItem("New version…", Icons.NEW_FOLDER, this::createVersion));
            menu.add(menuItem("Change master folder…", Icons.SETTINGS, () -> {
                chooseMaster();
                refresh();
            }));
        }
        menu.show(tree, e.getX(), e.getY());
    }

    /** Rescan projects, then select {@code select} and optionally run {@code then}. */
    private void reloadAndSelect(Path select, Runnable then) {
        Path master = config.masterPath().orElseThrow();
        background("Scanning projects…", () -> ProjectScanner.scan(master, git), result -> {
            apply(result);
            rebuildTree();
            reselect(select);
            updateStatus();
            if (then != null) then.run();
        });
    }

    private <T> void background(String label, Callable<T> work, Consumer<T> onDone) {
        runningTasks++;
        busyBar.setVisible(true);
        busyBar.setToolTipText(label);
        statusLabel.setText(label);
        new SwingWorker<T, Void>() {
            @Override
            protected T doInBackground() throws Exception {
                return work.call();
            }

            @Override
            protected void done() {
                runningTasks--;
                busyBar.setVisible(runningTasks > 0);
                updateStatus();
                try {
                    onDone.accept(get());
                } catch (java.util.concurrent.ExecutionException e) {
                    Throwable cause = e.getCause();
                    log("✖ Error: " + cause);
                    error("Error", String.valueOf(cause.getMessage()));
                } catch (InterruptedException e) {
                    Thread.currentThread().interrupt();
                }
            }
        }.execute();
    }

    private interface IoAction {
        void run() throws Exception;
    }

    private void run(IoAction action) {
        try {
            action.run();
        } catch (Exception e) {
            log("✖ " + e.getMessage());
            error("Error", e.getMessage());
        }
    }

    private void log(String line) {
        // Keep the log bounded – runClient can produce a lot of output
        if (logArea.getDocument().getLength() > MAX_LOG_CHARS) {
            logArea.replaceRange("", 0, logArea.getDocument().getLength() - MAX_LOG_CHARS / 2);
        }
        logArea.append("[" + LocalTime.now().format(TIME) + "] " + line + "\n");
        logArea.setCaretPosition(logArea.getDocument().getLength());
        if (logWindow == null || !logWindow.isVisible()) {
            logButton.setText("Log ●"); // unread lines
        }
    }

    private void openLog() {
        if (logWindow == null) logWindow = new LogWindow(this, logArea);
        logWindow.open();
        logButton.setText("Log");
    }

    private void logAsync(String line) {
        SwingUtilities.invokeLater(() -> log("    " + line));
    }

    private void error(String title, String message) {
        String text = message == null || message.isBlank() ? "Unknown error" : message;
        if (text.length() > 1200) text = text.substring(text.length() - 1200);
        JTextArea area = new JTextArea(text, Math.min(12, (int) text.lines().count() + 1), 60);
        area.setEditable(false);
        area.setLineWrap(true);
        area.setWrapStyleWord(true);
        JOptionPane.showMessageDialog(this, new JScrollPane(area), title, JOptionPane.ERROR_MESSAGE);
    }

    private static JButton toolButton(String text, String icon, String tooltip, Runnable action) {
        JButton b = new JButton(text, Icons.get(icon));
        b.setToolTipText(tooltip);
        b.setFocusable(false);
        b.addActionListener(e -> action.run());
        return b;
    }

    private static JButton button(String text, Runnable action) {
        JButton b = new JButton(text);
        b.addActionListener(e -> action.run());
        return b;
    }

    private static JButton button(String text, String icon, Runnable action) {
        JButton b = button(text, action);
        b.setIcon(Icons.get(icon));
        return b;
    }

    private static JMenuItem menuItem(String text, Runnable action) {
        JMenuItem item = new JMenuItem(text);
        item.addActionListener(e -> action.run());
        return item;
    }

    private static JMenuItem menuItem(String text, String icon, Runnable action) {
        JMenuItem item = menuItem(text, action);
        item.setIcon(Icons.get(icon));
        return item;
    }

    /** Build, publish and game test tasks get matching icons. */
    private static String gradleIcon(String task) {
        String name = task.substring(task.lastIndexOf(':') + 1);
        if (name.equals("build")) return Icons.BUILD;
        if (name.equals("publish") || name.equals("publishMods")) return Icons.UPLOAD;
        return Icons.TEST;
    }

    private static javax.swing.Action action(Runnable r) {
        return new javax.swing.AbstractAction() {
            @Override
            public void actionPerformed(java.awt.event.ActionEvent e) {
                r.run();
            }
        };
    }
}
