package net.morthen.ui;

import net.morthen.core.Config;
import net.morthen.core.Git;
import net.morthen.core.GradleRunner;
import net.morthen.core.ModProject;
import net.morthen.core.ProjectScanner;
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
    private static final int LOG_HEIGHT = 120;
    private static final int MAX_LOG_CHARS = 400_000;

    private final Config config;
    private final Git git;
    private final GradleRunner gradle = new GradleRunner();

    private List<String> versions = List.of();
    private List<ModProject> projects = List.of();
    private List<ProjectScanner.SharedRepo> repos = List.of();

    private final DefaultMutableTreeNode root = new DefaultMutableTreeNode("Master");
    private final DefaultTreeModel treeModel = new DefaultTreeModel(root);
    private final JTree tree = new JTree(treeModel);
    private final JTextField filterField = new JTextField();
    private final JPanel detailPanel = new JPanel(new BorderLayout());
    private final JTextArea logArea = new JTextArea();
    private final JLabel statusLabel = new JLabel();
    private final JProgressBar busyBar = new JProgressBar();
    private int runningTasks;
    private Object shownDetail;

    public MainFrame(Config config) {
        super("MC Mod Project Manager");
        this.config = config;
        this.git = new Git(config.gitExecutable());

        setDefaultCloseOperation(DO_NOTHING_ON_CLOSE);
        addWindowListener(new java.awt.event.WindowAdapter() {
            @Override
            public void windowClosing(java.awt.event.WindowEvent e) {
                exitApplication();
            }
        });
        setSize(WINDOW_SIZE);
        setResizable(false);
        setLocationRelativeTo(null);

        buildUi();
        SwingUtilities.invokeLater(this::firstStart);
    }

    // =====================================================================  Layout

    private void buildUi() {
        JToolBar toolbar = new JToolBar();
        toolbar.setFloatable(false);
        toolbar.setBorder(BorderFactory.createEmptyBorder(6, 8, 6, 8));
        toolbar.add(toolButton("➕ Clone project", "Clone a Git repository into a version folder", () -> cloneDialog(null, null, selectedVersion())));
        toolbar.add(toolButton("📥 Import", "Take over an existing local project folder", () -> importProject(selectedVersion())));
        toolbar.add(toolButton("📁 New version", "Create a new Minecraft version folder", this::createVersion));
        toolbar.addSeparator();
        toolbar.add(toolButton("⟳ Refresh", "Rescan folders (F5)", this::refresh));
        toolbar.add(toolButton("⇣ Fetch all", "git fetch for all projects", this::fetchAll));
        toolbar.add(javax.swing.Box.createHorizontalGlue());
        toolbar.add(toolButton("⚙ Settings", "Master path, IntelliJ, Git", this::openSettings));

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

        JPanel top = new JPanel(new BorderLayout());
        top.add(left, BorderLayout.WEST);
        top.add(detailPanel, BorderLayout.CENTER);

        // --- bottom: log
        logArea.setEditable(false);
        logArea.setFont(new Font(Font.MONOSPACED, Font.PLAIN, 11));
        JScrollPane logScroll = new JScrollPane(logArea);
        JPanel logPanel = new JPanel(new BorderLayout(0, 2));
        logPanel.setBorder(BorderFactory.createEmptyBorder(0, 8, 0, 8));
        logPanel.setPreferredSize(new Dimension(0, LOG_HEIGHT));
        JPanel logHeader = new JPanel(new BorderLayout());
        logHeader.add(Ui.heading("Git log", "h4"), BorderLayout.WEST);
        JButton clearLog = new JButton("Clear");
        clearLog.putClientProperty("JButton.buttonType", "toolBarButton");
        clearLog.addActionListener(e -> logArea.setText(""));
        logHeader.add(clearLog, BorderLayout.EAST);
        logPanel.add(logHeader, BorderLayout.NORTH);
        logPanel.add(logScroll, BorderLayout.CENTER);

        JPanel center = new JPanel(new BorderLayout());
        center.add(top, BorderLayout.CENTER);
        center.add(logPanel, BorderLayout.SOUTH);

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
        JComponent content;
        if (obj instanceof ModProject p) {
            content = projectDetails(p);
        } else if (obj instanceof VersionEntry v) {
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

    private JComponent projectDetails(ModProject p) {
        Ui.Stack stack = new Ui.Stack();
        stack.setBorder(BorderFactory.createEmptyBorder(4, 12, 8, 12));

        stack.push(Ui.heading(p.name(), "h2"), 2);
        stack.push(Ui.muted("Minecraft " + p.mcVersion() + (p.loader() != null ? "  ·  " + p.loader() : "")), 16);

        JButton openIde = Ui.primary("  Open in IntelliJ  ");
        openIde.addActionListener(e -> openInIntelliJ(p));
        JPanel primaryRow = new JPanel(new FlowLayout(FlowLayout.LEFT, 0, 0));
        primaryRow.add(openIde);
        stack.push(primaryRow, 18);

        Ui.Form info = new Ui.Form();
        info.add("Path", Ui.copyable(p.path().toString()));
        if (p.git()) {
            info.add("Branch", Ui.copyable(p.branch()));
            info.add("Upstream", Ui.copyable(p.upstream() == null ? "– (no tracking branch)" : p.upstream()));
            info.add("Status", new JLabel(statusHtml(p)));
            info.add("Remote", Ui.copyable(p.remoteUrl().isEmpty() ? "–" : p.remoteUrl()));
            info.add("Repository", new JLabel(repoDescription(p)));
            List<ModProject> siblings = siblings(p);
            if (!siblings.isEmpty()) {
                JPanel links = new JPanel(new FlowLayout(FlowLayout.LEFT, 6, 0));
                for (ModProject s : siblings) {
                    JButton link = new JButton(s.mcVersion() + (s.branch() != null ? "  ⎇ " + s.branch() : ""));
                    link.putClientProperty("JButton.buttonType", "roundRect");
                    link.setToolTipText(s.path().toString());
                    link.addActionListener(e -> reselect(s.path()));
                    links.add(link);
                }
                info.add("Other versions", links);
            }
        } else {
            info.add("Git", new JLabel("Not a Git repository"));
        }
        if (p.modVersion() != null) info.add("Mod version", Ui.copyable(p.modVersion()));
        if (p.gradleMcVersion() != null) {
            String text = p.gradleMcVersion();
            JLabel label = new JLabel(p.versionMismatch()
                    ? "<html>" + Ui.html(text) + " <font color='#e57373'>⚠ does not match folder “" + Ui.html(p.mcVersion()) + "”</font></html>"
                    : text);
            info.add("MC (gradle)", label);
        }
        stack.push(info, 18);

        stack.push(Ui.heading("Actions", "h4"), 6);
        JPanel actions = new JPanel(new GridLayout(0, 2, 8, 8));
        actions.add(button("Open in Explorer", () -> run(() -> SystemActions.openExplorer(p.path()))));
        actions.add(button("Open terminal", () -> run(() -> SystemActions.openTerminal(p.path()))));
        if (p.git()) {
            actions.add(button("Fetch", () -> gitTask("Fetch " + p.name(), p.path(), "fetch", "--all", "--prune")));
            actions.add(button("Pull (fast-forward)", () -> gitTask("Pull " + p.name(), p.path(), "pull", "--ff-only")));
            actions.add(button("Add version (worktree)…", () -> addWorktree(p)));
            if (p.standaloneClone()) {
                actions.add(button("Convert to shared repo…", () -> convertToShared(p)));
            }
        }
        actions.add(button("Move / rename…", () -> moveProject(p)));
        JButton delete = button("Remove…", () -> deleteProject(p));
        delete.setForeground(new java.awt.Color(0xE57373));
        actions.add(delete);
        stack.push(actions, 18);

        if (GradleRunner.wrapper(p.path()).isPresent()) {
            stack.push(Ui.heading("Gradle", "h4"), 6);
            JPanel gradleActions = new JPanel(new GridLayout(0, 2, 8, 8));
            String task = gradle.runningTask(p.path());
            if (task != null) {
                JButton stop = button("■ Stop " + task, () -> stopGradle(p));
                stop.setForeground(new java.awt.Color(0xE57373));
                gradleActions.add(stop);
            } else {
                gradleActions.add(button("Build", () -> runGradle(p, "build")));
                gradleActions.add(button("Run client", () -> runGradle(p, "runClient")));
                gradleActions.add(button("Run server", () -> runGradle(p, "runServer")));
            }
            gradleActions.add(button("Open build/libs", () -> openBuildLibs(p)));
            long versionsOfMod = projects.stream()
                    .filter(o -> o.name().equals(p.name()) && GradleRunner.wrapper(o.path()).isPresent())
                    .count();
            if (versionsOfMod > 1) {
                gradleActions.add(button("Build all " + versionsOfMod + " versions", () -> buildAllVersions(p.name())));
            }
            stack.push(gradleActions, 18);
        }

        if (p.git()) {
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
        }
        return stack.finish();
    }

    private static String statusHtml(ModProject p) {
        List<String> parts = new ArrayList<>();
        parts.add(p.changes() == 0 ? "<font color='#4caf50'>✔ no local changes</font>"
                : "<font color='#e0a030'>● " + p.changes() + " changed file(s)</font>");
        if (p.ahead() > 0) parts.add("<font color='#4caf50'>↑ " + p.ahead() + " not pushed</font>");
        if (p.behind() > 0) parts.add("<font color='#e57373'>↓ " + p.behind() + " behind remote</font>");
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
        actions.add(button("Clone project into this version…", () -> cloneDialog(null, null, v.version())));
        actions.add(button("Import local project…", () -> importProject(v.version())));
        actions.add(button("Open folder", () -> run(() -> SystemActions.openExplorer(v.path()))));
        actions.add(button("Delete version folder (if empty)", () -> deleteVersion(v)));
        stack.push(actions, 18);

        List<ModProject> list = projects.stream().filter(p -> p.mcVersion().equals(v.version())).toList();
        if (!list.isEmpty()) {
            stack.push(Ui.heading("Quick open", "h4"), 6);
            JPanel quick = new JPanel(new GridLayout(0, 2, 8, 8));
            for (ModProject p : list) {
                quick.add(button(p.name() + (p.branch() != null ? "  ⎇ " + p.branch() : ""), () -> openInIntelliJ(p)));
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
        stack.push(Ui.muted("Structure:  " + master.get() + "\\<MC version>\\<project>"), 16);

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
        actions.add(button("Clone project…", () -> cloneDialog(null, null, selectedVersion())));
        actions.add(button("Import local project…", () -> importProject(null)));
        actions.add(button("New Minecraft version…", this::createVersion));
        actions.add(button("Open master folder", () -> run(() -> SystemActions.openExplorer(master.get()))));
        stack.push(actions, 24);

        stack.push(Ui.heading("Shared repositories (" + Worktrees.REPOS_DIR + ")", "h3"), 4);
        stack.push(Ui.muted("Each mod has one repository; the version folders are worktrees of it."), 10);
        if (repos.isEmpty()) {
            stack.push(Ui.muted("None yet – pick “Create as worktree” when cloning, or convert existing clones."), 10);
        }
        for (ProjectScanner.SharedRepo r : repos) {
            JPanel row = new JPanel(new BorderLayout(12, 0));
            String versionsText = r.worktrees().isEmpty() ? "<font color='#e0a030'>no worktrees</font>"
                    : Ui.html(String.join(", ", r.worktrees().stream().map(ModProject::mcVersion).toList()));
            row.add(new JLabel("<html><b>" + Ui.html(r.name()) + "</b>&nbsp;&nbsp;" + versionsText
                    + "<br><font color='" + Ui.hex(UIManager.getColor("Label.disabledForeground")) + "'>"
                    + Ui.html(r.url().isEmpty() ? "(no remote)" : r.url()) + "</font></html>"), BorderLayout.CENTER);
            JPanel buttons = new JPanel(new FlowLayout(FlowLayout.RIGHT, 6, 0));
            buttons.add(button("Add version…", () -> addWorktree(r.path(), r.name(), null, null)));
            if (r.worktrees().isEmpty()) {
                JButton del = button("Delete…", () -> deleteSharedRepo(r));
                del.setForeground(new java.awt.Color(0xE57373));
                buttons.add(del);
            }
            row.add(buttons, BorderLayout.EAST);
            stack.push(row, 10);
        }

        long clones = projects.stream().filter(p -> p.standaloneClone() && !p.remoteUrl().isEmpty()).count();
        JPanel maintenance = new JPanel(new GridLayout(0, 2, 8, 8));
        JButton convert = button("Convert all clones (" + clones + ")…", this::convertAll);
        convert.setEnabled(clones > 0);
        convert.setToolTipText("Convert standalone clones into worktrees of shared repositories");
        maintenance.add(convert);
        JButton prune = button("Clean up stale worktree entries", this::pruneAll);
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
        Optional<Path> ide = ideaPath();
        if (ide.isEmpty()) {
            JOptionPane.showMessageDialog(this,
                    "IntelliJ IDEA was not found.\nPlease set the path to idea64.exe in the settings.",
                    "IntelliJ not found", JOptionPane.WARNING_MESSAGE);
            openSettings();
            return;
        }
        log("Opening " + p.name() + " in IntelliJ …");
        run(() -> SystemActions.openInIntelliJ(ide.get(), p.path()));
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
                    ? "<br><br><font color='#e57373'><b>Warning:</b> " + unpushed
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
            warning = "<br><br><font color='#e57373'><b>Warning:</b> "
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

    private void fetchAll() {
        // Worktrees share one repository → fetch each repository only once
        Map<Path, Path> targets = new LinkedHashMap<>();
        Map<Path, String> labels = new LinkedHashMap<>();
        for (ModProject p : projects) {
            if (!p.git()) continue;
            Path key = p.commonDir() != null ? p.commonDir() : p.path();
            if (targets.putIfAbsent(key, p.path()) == null) {
                labels.put(key, p.linkedWorktree() ? p.name() : p.mcVersion() + "/" + p.name());
            }
        }
        for (ProjectScanner.SharedRepo r : repos) {
            if (targets.putIfAbsent(r.path(), r.path()) == null) labels.put(r.path(), r.name());
        }
        if (targets.isEmpty()) return;
        background("Fetching " + targets.size() + " repositories…", () -> {
            int failed = 0;
            for (var e : targets.entrySet()) {
                logAsync("> fetch " + labels.get(e.getKey()));
                if (!git.run(e.getValue(), this::logAsync, "fetch", "--all", "--prune").ok()) failed++;
            }
            return failed;
        }, failed -> {
            log(failed == 0 ? "✔ All repositories fetched" : "✖ " + failed + " fetch(es) failed");
            refresh();
        });
    }

    private void gitTask(String label, Path dir, String... args) {
        log("> git " + String.join(" ", args) + "    (" + dir + ")");
        background(label + "…", () -> git.run(dir, this::logAsync, args), result -> {
            log(result.ok() ? "✔ " + label + " done" : "✖ " + label + " failed (exit code " + result.exitCode() + ")");
            if (!result.ok()) error(label + " failed", result.output());
            refresh();
        });
    }

    // =====================================================================  Gradle

    private void runGradle(ModProject p, String task) {
        String tag = "[" + p.mcVersion() + "/" + p.name() + "] ";
        log("> gradlew " + task + "    (" + p.path() + ")");
        try {
            gradle.start(p.path(), task, line -> logAsync(tag + line), exit -> SwingUtilities.invokeLater(() -> {
                log(exit == 0 ? "✔ " + tag + task + " finished" : "✖ " + tag + task + " ended (exit code " + exit + ")");
                gradleStateChanged(p);
            }));
        } catch (Exception e) {
            error("Gradle", e.getMessage());
        }
        gradleStateChanged(p);
    }

    private void stopGradle(ModProject p) {
        log("Stopping " + gradle.runningTask(p.path()) + " in " + p.mcVersion() + "/" + p.name() + " …");
        gradle.stop(p.path());
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

    private void exitApplication() {
        int running = gradle.runningCount();
        if (running > 0 && JOptionPane.showConfirmDialog(this,
                running + " Gradle task(s) are still running. Stop them and exit?",
                "Exit", JOptionPane.YES_NO_OPTION, JOptionPane.WARNING_MESSAGE) != JOptionPane.YES_OPTION) {
            return;
        }
        gradle.stopAll();
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

        Ui.Form form = new Ui.Form()
                .add("Master folder", master, masterBrowse)
                .add("IntelliJ (idea64.exe)", idea, detect)
                .add("Git executable", gitField);

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
        config.save();
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
            menu.add(menuItem("Open in IntelliJ", () -> openInIntelliJ(p)));
            menu.add(menuItem("Open in Explorer", () -> run(() -> SystemActions.openExplorer(p.path()))));
            menu.add(menuItem("Open terminal", () -> run(() -> SystemActions.openTerminal(p.path()))));
            if (p.git()) {
                menu.addSeparator();
                menu.add(menuItem("Fetch", () -> gitTask("Fetch " + p.name(), p.path(), "fetch", "--all", "--prune")));
                menu.add(menuItem("Pull", () -> gitTask("Pull " + p.name(), p.path(), "pull", "--ff-only")));
                menu.add(menuItem("Add version (worktree)…", () -> addWorktree(p)));
            }
            menu.addSeparator();
            menu.add(menuItem("Remove…", () -> deleteProject(p)));
        } else if (o instanceof VersionEntry v) {
            menu.add(menuItem("Clone project here…", () -> cloneDialog(null, null, v.version())));
            menu.add(menuItem("Import project…", () -> importProject(v.version())));
            menu.add(menuItem("Open folder", () -> run(() -> SystemActions.openExplorer(v.path()))));
        } else {
            menu.add(menuItem("New version…", this::createVersion));
            menu.add(menuItem("Change master folder…", () -> {
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

    private static JButton toolButton(String text, String tooltip, Runnable action) {
        JButton b = new JButton(text);
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

    private static JMenuItem menuItem(String text, Runnable action) {
        JMenuItem item = new JMenuItem(text);
        item.addActionListener(e -> action.run());
        return item;
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
