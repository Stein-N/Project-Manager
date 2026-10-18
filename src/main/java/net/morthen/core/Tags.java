package net.morthen.core;

import java.io.IOException;
import java.nio.file.Path;
import java.util.List;
import java.util.Optional;
import java.util.function.Consumer;

/**
 * Release tags: create, push and find the last one of a branch (the backport only offers commits since then).
 */
public final class Tags {
    private Tags() {
    }

    /** The newest tag reachable from {@code ref}. */
    public static Optional<String> latest(Git git, Path repo, String ref) {
        Git.Result r = git.run(repo, "describe", "--tags", "--abbrev=0", ref);
        return r.ok() && !r.output().isBlank() ? Optional.of(r.output().strip()) : Optional.empty();
    }

    /** Number of commits in {@code ref} after {@code tag}. */
    public static int commitsSince(Git git, Path repo, String tag, String ref) {
        Git.Result r = git.run(repo, "rev-list", "--count", tag + ".." + ref);
        try {
            return r.ok() ? Integer.parseInt(r.output().strip()) : 0;
        } catch (NumberFormatException e) {
            return 0;
        }
    }

    /** Local tags, newest first. */
    public static List<String> list(Git git, Path repo) {
        Git.Result r = git.run(repo, "tag", "--list", "--sort=-creatordate");
        return r.ok() && !r.output().isBlank() ? List.of(r.output().split("\n")) : List.of();
    }

    public static boolean exists(Git git, Path repo, String name) {
        return git.run(repo, "show-ref", "--verify", "--quiet", "refs/tags/" + name).ok();
    }

    public static boolean validName(Git git, Path repo, String name) {
        return !name.isBlank() && git.run(repo, "check-ref-format", "refs/tags/" + name).ok();
    }

    /** Suggested tag for a mod version: {@code v2.1.7}. */
    public static String suggest(String modVersion, String mcVersion) {
        String v = Changelog.displayVersion(modVersion, mcVersion);
        return v == null || v.isBlank() ? "" : v.startsWith("v") ? v : "v" + v;
    }

    /** Creates a tag at HEAD (annotated if a message is given). */
    public static void create(Git git, Path repo, String name, String message, Consumer<String> log) throws IOException {
        if (!validName(git, repo, name)) throw new IOException("Not a valid tag name: " + name);
        if (exists(git, repo, name)) throw new IOException("Tag " + name + " already exists.");
        if (message == null || message.isBlank()) {
            git.runOrThrow(repo, log, "tag", name);
        } else {
            git.runOrThrow(repo, log, "tag", "-a", name, "-m", message.strip());
        }
    }

    public static void push(Git git, Path repo, String name, Consumer<String> log) throws IOException {
        git.runOrThrow(repo, log, "push", "origin", "refs/tags/" + name);
    }
}
