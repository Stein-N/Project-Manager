package net.morthen.ui;

import com.formdev.flatlaf.extras.FlatSVGIcon;

import javax.swing.Icon;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;

/**
 * SVG icons from /icons (IntelliJ Platform icons, Apache 2.0 – see icons/NOTICE.txt).
 * FlatSVGIcon scales with the UI and derives a grayed-out variant for disabled buttons automatically.
 */
final class Icons {
    static final String CLONE = "clone";
    static final String IMPORT = "import";
    static final String NEW_FOLDER = "newFolder";
    static final String REFRESH = "refresh";
    static final String FETCH = "fetch";
    static final String CLEANUP = "cleanup";
    static final String SETTINGS = "settings";
    static final String INTELLIJ = "intellij";
    static final String FOLDER = "folder";
    static final String TERMINAL = "terminal";
    static final String EDIT = "edit";
    static final String DELETE = "delete";
    static final String PULL = "pull";
    static final String COMMIT = "commit";
    static final String PUSH = "push";
    static final String ADD = "add";
    static final String MERGE = "merge";
    static final String BUILD = "build";
    static final String RUN = "run";
    static final String STOP = "stop";
    static final String TEST = "test";
    static final String UPLOAD = "upload";
    static final String CONFIG = "config";
    static final String SUCCESS = "success";
    static final String WARNING = "warning";
    static final String UPGRADE = "upgrade";
    static final String BRANCH = "branch";

    private static final Map<String, Icon> CACHE = new ConcurrentHashMap<>();

    private Icons() {
    }

    /** 16×16 icon (scaled with the UI scale factor). */
    static Icon get(String name) {
        return CACHE.computeIfAbsent(name, n -> new FlatSVGIcon("icons/" + n + ".svg", 16, 16));
    }
}
