package net.morthen.core;

import java.nio.file.Path;

/**
 * A mod project at {@code <master>/<mcVersion>/<name>}.
 *
 * @param linkedWorktree {@code .git} is a file, i.e. the project is a linked worktree
 * @param commonDir      canonical path of the shared git directory (null without Git)
 * @param metadata       mod metadata (fabric.mod.json, mods.toml, …) or null
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
        Path commonDir,
        ModMetadata metadata
) {
    /** MC version from gradle.properties does not match the version folder. */
    public boolean versionMismatch() {
        return gradleMcVersion != null && !gradleMcVersion.isBlank() && !gradleMcVersion.equals(mcVersion);
    }

    public boolean hasUnsavedWork() {
        return changes > 0 || ahead > 0;
    }

    /** Standalone clone with its own .git folder. */
    public boolean standaloneClone() {
        return git && !linkedWorktree;
    }
}
