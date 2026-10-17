package net.morthen.core;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.function.Consumer;
import java.util.stream.Stream;

/**
 * Working tree changes, commit and push.
 */
public final class GitChanges {
    /**
     * One changed file.
     *
     * @param origPath previous path for renames/copies, otherwise null
     * @param index    status letter in the index (porcelain X)
     * @param worktree status letter in the working tree (porcelain Y)
     */
    public record Change(String path, String origPath, char index, char worktree) {
        public boolean untracked() {
            return index == '?';
        }

        public String label() {
            if (untracked()) return "new";
            char c = index != ' ' ? index : worktree;
            return switch (c) {
                case 'M' -> "modified";
                case 'A' -> "added";
                case 'D' -> "deleted";
                case 'R' -> "renamed";
                case 'C' -> "copied";
                case 'U' -> "conflict";
                case 'T' -> "type changed";
                default -> String.valueOf(c);
            };
        }
    }

    private GitChanges() {
    }

    public static List<Change> changes(Git git, Path repo) throws IOException {
        String out = git.runOrThrow(repo, null, "status", "--porcelain=v1", "-z", "-uall").output();
        List<Change> changes = new ArrayList<>();
        String[] fields = out.split("\0");
        for (int i = 0; i < fields.length; i++) {
            String f = fields[i];
            if (f.length() < 4) continue;
            char x = f.charAt(0);
            char y = f.charAt(1);
            String path = f.substring(3);
            String orig = null;
            if ((x == 'R' || x == 'C') && i + 1 < fields.length) {
                orig = fields[++i];
            }
            changes.add(new Change(path, orig, x, y));
        }
        return changes;
    }

    /**
     * Commits exactly the selected files. Staged changes of files that are not selected are unstaged first,
     * so they stay as local changes.
     */
    public static void commit(Git git, Path repo, List<Change> all, List<Change> selected, String message,
                              Consumer<String> log) throws IOException {
        if (selected.isEmpty()) throw new IOException("No files selected.");
        if (message.isBlank()) throw new IOException("The commit message is empty.");

        List<String> present = new ArrayList<>();
        List<String> removed = new ArrayList<>();
        for (Change c : selected) {
            (Files.exists(repo.resolve(c.path())) ? present : removed).add(c.path());
            if (c.origPath() != null) removed.add(c.origPath());
        }
        List<String> unstage = new ArrayList<>();
        for (Change c : all) {
            if (!selected.contains(c) && !c.untracked() && c.index() != ' ') {
                unstage.add(c.path());
                if (c.origPath() != null) unstage.add(c.origPath());
            }
        }

        Path messageFile = Files.createTempFile("mcpm-message", ".txt");
        try {
            withPathspec(unstage, spec -> git.runOrThrow(repo, log, "reset", "-q", spec, "--pathspec-file-nul"));
            withPathspec(present, spec -> git.runOrThrow(repo, log, "add", "-A", spec, "--pathspec-file-nul"));
            withPathspec(removed, spec -> git.runOrThrow(repo, log, "rm", "--cached", "-q", "--ignore-unmatch", spec, "--pathspec-file-nul"));
            Files.writeString(messageFile, message.strip() + "\n", StandardCharsets.UTF_8);
            git.runOrThrow(repo, log, "commit", "-F", messageFile.toString());
        } finally {
            Files.deleteIfExists(messageFile);
        }
    }

    private interface PathspecAction {
        void run(String pathspecArg) throws IOException;
    }

    /** Passes the paths via a NUL-separated file (no command line length limits, no quoting issues). */
    private static void withPathspec(List<String> paths, PathspecAction action) throws IOException {
        if (paths.isEmpty()) return;
        Path file = Files.createTempFile("mcpm-pathspec", ".txt");
        try {
            Files.writeString(file, String.join("\0", paths) + "\0", StandardCharsets.UTF_8);
            action.run("--pathspec-from-file=" + file);
        } finally {
            Files.deleteIfExists(file);
        }
    }

    /** Pushes the current branch; sets the upstream on the first push of a new branch. */
    public static void push(Git git, Path repo, Consumer<String> log) throws IOException {
        Git.Status status = git.status(repo).orElseThrow(() -> new IOException("Could not read git status."));
        if (status.branch() == null || status.branch().startsWith("(")) {
            throw new IOException("Not on a branch (detached HEAD) – nothing to push.");
        }
        if (status.upstream() == null) {
            git.runOrThrow(repo, log, "push", "-u", "origin", status.branch());
        } else {
            git.runOrThrow(repo, log, "push");
        }
    }

    /** Diff of one file against HEAD (staged + unstaged); new files are shown completely. */
    public static String diff(Git git, Path repo, Change change) {
        if (change.untracked()) {
            Path file = repo.resolve(change.path());
            try (Stream<String> lines = Files.lines(file, StandardCharsets.UTF_8)) {
                List<String> head = lines.limit(500).map(l -> "+" + l).toList();
                return "new file: " + change.path() + "\n\n" + String.join("\n", head);
            } catch (Exception e) {
                return "new file: " + change.path() + "\n\n(binary or unreadable)";
            }
        }
        Git.Result r = git.run(repo, "diff", "HEAD", "--", change.path());
        if (!r.ok()) r = git.run(repo, "diff", "--cached", "--", change.path()); // no commits yet
        // git prints line-ending warnings on stderr, which is merged into the output
        String text = r.output().lines()
                .dropWhile(l -> l.startsWith("warning: ") || l.startsWith("the next time Git touches it"))
                .collect(java.util.stream.Collectors.joining("\n"));
        return text.isBlank() ? "(no textual changes)" : text;
    }
}
