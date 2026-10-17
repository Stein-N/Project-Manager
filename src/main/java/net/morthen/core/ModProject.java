package net.morthen.core;

import java.nio.file.Path;

/**
 * Ein Mod-Projekt unter {@code <master>/<mcVersion>/<name>}.
 *
 * @param linkedWorktree {@code .git} ist eine Datei, d.h. das Projekt ist ein verknüpfter Worktree
 * @param commonDir      kanonischer Pfad des gemeinsamen git-Verzeichnisses (null ohne Git)
 */
public record ModProject(
        String name,
        String mcVersion,
        Path path,
        boolean git,
        String branch,
        String upstream,
        String remoteUrl,
        int changes,
        int ahead,
        int behind,
        String loader,
        String gradleMcVersion,
        String modVersion,
        boolean linkedWorktree,
        Path commonDir
) {
    /** MC-Version aus gradle.properties passt nicht zum Versionsordner. */
    public boolean versionMismatch() {
        return gradleMcVersion != null && !gradleMcVersion.isBlank() && !gradleMcVersion.equals(mcVersion);
    }

    public boolean hasUnsavedWork() {
        return changes > 0 || ahead > 0;
    }

    /** Eigenständiger Clone mit eigenem .git-Ordner. */
    public boolean standaloneClone() {
        return git && !linkedWorktree;
    }
}
