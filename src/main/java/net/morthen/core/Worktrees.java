package net.morthen.core;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.function.Consumer;

/**
 * git worktree management.
 * <p>
 * Model: one shared bare repository per mod at {@code <master>/.repos/<name>.git};
 * every version folder {@code <master>/<version>/<name>} is a worktree of it.
 * Regular clones (with their own .git folder) can have worktrees too, or be converted.
 */
public final class Worktrees {
    public static final String REPOS_DIR = ".repos";

    public record Worktree(Path path, String head, String branch, boolean bare, boolean prunable) {
    }

    private Worktrees() {
    }

    public static Path sharedRepo(Path master, String name) {
        return master.resolve(REPOS_DIR).resolve(name + ".git");
    }

    // ---------------------------------------------------------------- Queries

    /** All worktrees of a repository (including the main worktree or bare entry). */
    public static List<Worktree> list(Git git, Path anyDir) {
        Git.Result r = git.run(anyDir, "worktree", "list", "--porcelain");
        List<Worktree> result = new ArrayList<>();
        if (!r.ok()) {
            return result;
        }
        for (String block : r.output().split("\\n\\s*\\n")) {
            Path path = null;
            String head = null;
            String branch = null;
            boolean bare = false;
            boolean prunable = false;
            for (String line : block.split("\n")) {
                if (line.startsWith("worktree ")) path = Path.of(line.substring(9).trim());
                else if (line.startsWith("HEAD ")) head = line.substring(5).trim();
                else if (line.startsWith("branch ")) branch = line.substring(7).trim().replaceFirst("^refs/heads/", "");
                else if (line.equals("bare")) bare = true;
                else if (line.startsWith("prunable")) prunable = true;
            }
            if (path != null) {
                result.add(new Worktree(path, head, branch, bare, prunable));
            }
        }
        return result;
    }

    /** Branch → worktree path for all currently checked-out branches. */
    public static Map<String, Path> checkedOutBranches(Git git, Path anyDir) {
        Map<String, Path> map = new LinkedHashMap<>();
        for (Worktree wt : list(git, anyDir)) {
            if (wt.branch() != null && !wt.bare()) {
                map.put(wt.branch(), wt.path());
            }
        }
        return map;
    }

    /** Absolute, canonical path of the shared git directory. */
    public static Optional<Path> commonDir(Git git, Path dir) {
        Git.Result r = git.run(dir, "rev-parse", "--path-format=absolute", "--git-common-dir");
        return r.ok() ? Optional.of(real(Path.of(r.output().trim()))) : Optional.empty();
    }

    /** Commits on local branches that are not on any remote. */
    public static int unpushedCommits(Git git, Path anyDir) {
        Git.Result r = git.run(anyDir, "log", "--branches", "--not", "--remotes", "--oneline");
        return r.ok() && !r.output().isBlank() ? (int) r.output().lines().count() : 0;
    }

    public static boolean hasRef(Git git, Path dir, String ref) {
        return git.run(dir, "show-ref", "--verify", "--quiet", ref).ok();
    }

    // ---------------------------------------------------------------- Create / remove

    /** Creates the shared bare repo (or fetches it if it already exists). */
    public static void ensureShared(Git git, Path repo, String url, Consumer<String> log) throws IOException {
        if (Files.isDirectory(repo)) {
            String existing = git.run(repo, "config", "--get", "remote.origin.url").output().trim();
            if (!sameUrl(existing, url)) {
                throw new IOException("The repository " + repo.getFileName() + " already exists,\n"
                        + "but belongs to a different URL:\n" + existing
                        + "\n\nPlease choose a different project name.");
            }
            log.accept("Shared repository already exists – fetching …");
            git.runOrThrow(repo, log, "fetch", "origin", "--prune");
            return;
        }
        Files.createDirectories(repo.getParent());
        boolean ok = false;
        try {
            git.runOrThrow(repo.getParent(), log, "init", "--bare", repo.getFileName().toString());
            git.runOrThrow(repo, log, "remote", "add", "origin", url);
            git.runOrThrow(repo, log, "fetch", "origin", "--prune");
            git.run(repo, log, "remote", "set-head", "origin", "--auto");
            ok = true;
        } finally {
            if (!ok) {
                SystemActions.deleteRecursively(repo);
            }
        }
    }

    /**
     * New worktree at {@code target}.
     *
     * @param branch    branch to check out (local or origin/…); the base if {@code newBranch != null}
     * @param newBranch name of a branch to create, or {@code null}
     */
    public static void add(Git git, Path from, Path target, String branch, String newBranch, Consumer<String> log)
            throws IOException {
        Files.createDirectories(target.getParent());
        String t = target.toString();
        boolean local = hasRef(git, from, "refs/heads/" + branch);
        boolean remote = hasRef(git, from, "refs/remotes/origin/" + branch);
        if (newBranch != null) {
            String base = local ? branch : remote ? "origin/" + branch : branch;
            git.runOrThrow(from, log, "worktree", "add", "--no-track", "-b", newBranch, t, base);
        } else if (local) {
            git.runOrThrow(from, log, "worktree", "add", t, branch);
        } else if (remote) {
            git.runOrThrow(from, log, "worktree", "add", "--track", "-b", branch, t, "origin/" + branch);
        } else {
            throw new IOException("Branch “" + branch + "” was found neither locally nor on origin.");
        }
    }

    /** Removes a linked worktree (including all files in it). */
    public static void remove(Git git, Path commonDir, Path worktree, Consumer<String> log) throws IOException {
        Git.Result r = git.run(commonDir, log, "worktree", "remove", "--force", worktree.toString());
        if (!r.ok()) {
            // e.g. when files were locked and git gave up
            SystemActions.deleteRecursively(worktree);
            git.run(commonDir, log, "worktree", "prune");
        }
    }

    public static void move(Git git, Path commonDir, Path from, Path to, Consumer<String> log) throws IOException {
        Files.createDirectories(to.getParent());
        git.runOrThrow(commonDir, log, "worktree", "move", from.toString(), to.toString());
    }

    // ---------------------------------------------------------------- Conversion

    /**
     * Converts a regular clone (own .git folder) into a worktree of the shared repo {@code repo}.
     * Working files (including ignored ones like run/ or build/) stay untouched.
     * <ul>
     *     <li>If {@code repo} does not exist yet, the .git folder is moved there (everything is kept,
     *     existing worktrees are repaired).</li>
     *     <li>If {@code repo} already exists (same mod, other version), the local branches are transferred
     *     into the shared repo and the .git folder is removed afterwards.</li>
     * </ul>
     */
    public static void convertToShared(Git git, Path project, Path repo, Consumer<String> log) throws IOException {
        Path dotGit = project.resolve(".git");
        if (!Files.isDirectory(dotGit)) {
            throw new IOException("The project is not a standalone clone.");
        }
        for (String marker : List.of("MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "BISECT_LOG",
                "rebase-merge", "rebase-apply")) {
            if (Files.exists(dotGit.resolve(marker))) {
                throw new IOException("A Git operation is in progress in the project (" + marker + ").\n"
                        + "Please finish or abort it first.");
            }
        }
        String headBefore = git.runOrThrow(project, null, "rev-parse", "HEAD").output().trim();
        String headRef = Files.readString(dotGit.resolve("HEAD")).trim();
        String id = uniqueAdminId(repo, project);

        if (!Files.exists(repo)) {
            convertByMove(git, project, dotGit, repo, headRef, headBefore, id, log);
        } else {
            convertIntoExisting(git, project, dotGit, repo, headRef, headBefore, id, log);
        }
        log.accept("✔ " + project.getFileName() + " is now a worktree of " + repo.getFileName());
    }

    private static void convertByMove(Git git, Path project, Path dotGit, Path repo, String headRef,
                                      String headBefore, String id, Consumer<String> log) throws IOException {
        List<Path> linked = list(git, project).stream()
                .map(Worktree::path)
                .filter(p -> !samePath(p, project))
                .toList();
        Files.createDirectories(repo.getParent());
        log.accept("Moving .git to " + repo);
        SystemActions.move(dotGit, repo);
        Path admin = repo.resolve("worktrees").resolve(id);
        try {
            git.runOrThrow(repo, log, "config", "core.bare", "true");
            Files.createDirectories(admin);
            writeAdmin(admin, headRef, project);
            if (Files.exists(repo.resolve("index"))) {
                Files.move(repo.resolve("index"), admin.resolve("index"));
            }
            Path reflog = repo.resolve("logs").resolve("HEAD");
            if (Files.exists(reflog)) {
                Files.createDirectories(admin.resolve("logs"));
                Files.move(reflog, admin.resolve("logs").resolve("HEAD"));
            }
            Files.writeString(dotGit, "gitdir: " + slash(admin) + "\n");
            if (!linked.isEmpty()) {
                List<String> args = new ArrayList<>(List.of("worktree", "repair"));
                linked.forEach(p -> args.add(p.toString()));
                git.runOrThrow(repo, log, args.toArray(String[]::new));
            }
            verify(git, project, repo, headBefore);
        } catch (IOException | RuntimeException e) {
            log.accept("✖ Conversion failed – restoring original state …");
            if (Files.isRegularFile(dotGit)) Files.delete(dotGit);
            if (Files.exists(admin.resolve("index"))) Files.move(admin.resolve("index"), repo.resolve("index"));
            Path reflog = admin.resolve("logs").resolve("HEAD");
            if (Files.exists(reflog)) Files.move(reflog, repo.resolve("logs").resolve("HEAD"));
            SystemActions.deleteRecursively(admin);
            git.run(repo, "config", "core.bare", "false");
            SystemActions.move(repo, dotGit);
            if (!linked.isEmpty()) {
                List<String> args = new ArrayList<>(List.of("worktree", "repair"));
                linked.forEach(p -> args.add(p.toString()));
                git.run(project, args.toArray(String[]::new));
            }
            throw e;
        }
    }

    private static void convertIntoExisting(Git git, Path project, Path dotGit, Path repo, String headRef,
                                            String headBefore, String id, Consumer<String> log) throws IOException {
        if (list(git, project).size() > 1) {
            throw new IOException("This clone has its own worktrees and a shared repository already exists.\n"
                    + "Please remove this clone's worktrees first.");
        }
        if (hasRef(git, project, "refs/stash")) {
            throw new IOException("The project has stash entries (git stash).\n"
                    + "Please apply or drop them first – they would be lost otherwise.");
        }
        String ownUrl = git.run(project, "config", "--get", "remote.origin.url").output().trim();
        String repoUrl = git.run(repo, "config", "--get", "remote.origin.url").output().trim();
        if (!sameUrl(ownUrl, repoUrl)) {
            throw new IOException("The shared repository belongs to a different URL:\n" + repoUrl
                    + "\nThis project: " + (ownUrl.isEmpty() ? "(no origin)" : ownUrl));
        }
        String currentBranch = headRef.startsWith("ref: refs/heads/") ? headRef.substring("ref: refs/heads/".length()) : null;
        Map<String, Path> busy = checkedOutBranches(git, repo);
        if (currentBranch != null && busy.containsKey(currentBranch)) {
            throw new IOException("The branch “" + currentBranch + "” is already checked out in the shared repository:\n"
                    + busy.get(currentBranch));
        }

        // Fetch all local branches + HEAD into a temporary namespace, then reconcile
        log.accept("Transferring local branches into " + repo.getFileName() + " …");
        git.runOrThrow(repo, log, "fetch", "--no-tags", slash(dotGit),
                "+refs/heads/*:refs/pm-import/heads/*", "+HEAD:refs/pm-import/HEAD");
        try {
            Map<String, String> updates = new LinkedHashMap<>();
            List<String> conflicts = new ArrayList<>();
            Git.Result refs = git.run(repo, "for-each-ref", "--format=%(refname) %(objectname)", "refs/pm-import/heads");
            for (String line : refs.output().split("\n")) {
                if (line.isBlank()) continue;
                String[] parts = line.trim().split(" ");
                String name = parts[0].substring("refs/pm-import/heads/".length());
                String sha = parts[1];
                Git.Result existing = git.run(repo, "rev-parse", "--verify", "--quiet", "refs/heads/" + name);
                if (!existing.ok()) {
                    updates.put(name, sha);
                    continue;
                }
                String repoSha = existing.output().trim();
                if (repoSha.equals(sha)) continue;
                boolean fastForward = git.run(repo, "merge-base", "--is-ancestor", repoSha, sha).ok();
                boolean repoAhead = git.run(repo, "merge-base", "--is-ancestor", sha, repoSha).ok();
                if (fastForward && !busy.containsKey(name)) {
                    updates.put(name, sha);
                } else if (repoAhead && !name.equals(currentBranch)) {
                    // shared repo is already ahead – nothing lost
                } else {
                    conflicts.add(name + (busy.containsKey(name) ? " (checked out in " + busy.get(name) + ")" : " (diverged commits)"));
                }
            }
            if (!conflicts.isEmpty()) {
                throw new IOException("The following branches cannot be transferred automatically:\n  "
                        + String.join("\n  ", conflicts)
                        + "\n\nPlease push/pull first so both sides match.");
            }
            for (var u : updates.entrySet()) {
                git.runOrThrow(repo, log, "update-ref", "refs/heads/" + u.getKey(), u.getValue());
            }
            // Copy upstream settings (branch.<x>.remote/merge) unless already set in the shared repo
            Git.Result cfg = git.run(project, "config", "--local", "--get-regexp", "^branch\\.");
            for (String line : cfg.output().split("\n")) {
                int sp = line.indexOf(' ');
                if (sp > 0 && git.run(repo, "config", "--get", line.substring(0, sp)).output().isBlank()) {
                    git.run(repo, "config", line.substring(0, sp), line.substring(sp + 1));
                }
            }
        } finally {
            Git.Result tmp = git.run(repo, "for-each-ref", "--format=%(refname)", "refs/pm-import");
            for (String ref : tmp.output().split("\n")) {
                if (!ref.isBlank()) git.run(repo, "update-ref", "-d", ref.trim());
            }
        }

        Path admin = repo.resolve("worktrees").resolve(id);
        Path backup = project.resolve(".git-pm-backup");
        Files.createDirectories(admin);
        try {
            writeAdmin(admin, headRef, project);
            if (Files.exists(dotGit.resolve("index"))) {
                Files.copy(dotGit.resolve("index"), admin.resolve("index"));
            }
            SystemActions.move(dotGit, backup);
            Files.writeString(dotGit, "gitdir: " + slash(admin) + "\n");
            verify(git, project, repo, headBefore);
        } catch (IOException | RuntimeException e) {
            log.accept("✖ Conversion failed – restoring original state …");
            if (Files.isRegularFile(dotGit)) Files.delete(dotGit);
            if (Files.isDirectory(backup)) SystemActions.move(backup, dotGit);
            SystemActions.deleteRecursively(admin);
            throw e;
        }
        log.accept("Removing old .git folder …");
        SystemActions.deleteRecursively(backup);
    }

    private static void writeAdmin(Path admin, String headRef, Path project) throws IOException {
        Files.writeString(admin.resolve("HEAD"), headRef + "\n");
        Files.writeString(admin.resolve("commondir"), "../..\n");
        Files.writeString(admin.resolve("gitdir"), slash(project.resolve(".git")) + "\n");
    }

    private static void verify(Git git, Path project, Path repo, String headBefore) throws IOException {
        String head = git.runOrThrow(project, null, "rev-parse", "HEAD").output().trim();
        if (!head.equals(headBefore)) {
            throw new IOException("Verification failed: HEAD is " + head + " instead of " + headBefore);
        }
        Path common = commonDir(git, project).orElseThrow(() -> new IOException("Verification failed: no Git directory"));
        if (!samePath(common, repo)) {
            throw new IOException("Verification failed: Git directory is " + common);
        }
        git.runOrThrow(project, null, "status", "--porcelain");
    }

    private static String uniqueAdminId(Path repo, Path project) {
        String version = project.getParent() == null ? "" : project.getParent().getFileName().toString();
        String base = (project.getFileName() + "-" + version).replaceAll("[^A-Za-z0-9._-]", "_");
        String id = base;
        for (int i = 2; Files.exists(repo.resolve("worktrees").resolve(id)); i++) {
            id = base + i;
        }
        return id;
    }

    // ---------------------------------------------------------------- Paths / URLs

    public static Path real(Path path) {
        try {
            return path.toRealPath();
        } catch (IOException e) {
            return path.toAbsolutePath().normalize();
        }
    }

    public static boolean samePath(Path a, Path b) {
        return a != null && b != null && real(a).equals(real(b));
    }

    static String slash(Path path) {
        return path.toAbsolutePath().normalize().toString().replace('\\', '/');
    }

    static boolean sameUrl(String a, String b) {
        return normalizeUrl(a).equals(normalizeUrl(b));
    }

    private static String normalizeUrl(String url) {
        String u = url == null ? "" : url.trim().replace('\\', '/').toLowerCase(java.util.Locale.ROOT);
        u = u.replaceAll("/+$", "");
        return u.endsWith(".git") ? u.substring(0, u.length() - 4) : u;
    }
}
