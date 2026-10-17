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
 * git-worktree-Verwaltung.
 * <p>
 * Modell: pro Mod ein gemeinsames Bare-Repository unter {@code <master>/.repos/<name>.git};
 * jeder Versionsordner {@code <master>/<version>/<name>} ist ein Worktree davon.
 * Normale Clones (mit eigenem .git-Ordner) können ebenfalls Worktrees haben bzw. umgewandelt werden.
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

    // ---------------------------------------------------------------- Abfragen

    /** Alle Worktrees eines Repositories (inkl. Haupt-Worktree bzw. Bare-Eintrag). */
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

    /** Branch → Worktree-Pfad für alle aktuell ausgecheckten Branches. */
    public static Map<String, Path> checkedOutBranches(Git git, Path anyDir) {
        Map<String, Path> map = new LinkedHashMap<>();
        for (Worktree wt : list(git, anyDir)) {
            if (wt.branch() != null && !wt.bare()) {
                map.put(wt.branch(), wt.path());
            }
        }
        return map;
    }

    /** Absoluter, kanonischer Pfad des gemeinsamen git-Verzeichnisses. */
    public static Optional<Path> commonDir(Git git, Path dir) {
        Git.Result r = git.run(dir, "rev-parse", "--path-format=absolute", "--git-common-dir");
        return r.ok() ? Optional.of(real(Path.of(r.output().trim()))) : Optional.empty();
    }

    /** Commits auf lokalen Branches, die auf keinem Remote liegen. */
    public static int unpushedCommits(Git git, Path anyDir) {
        Git.Result r = git.run(anyDir, "log", "--branches", "--not", "--remotes", "--oneline");
        return r.ok() && !r.output().isBlank() ? (int) r.output().lines().count() : 0;
    }

    public static boolean hasRef(Git git, Path dir, String ref) {
        return git.run(dir, "show-ref", "--verify", "--quiet", ref).ok();
    }

    // ---------------------------------------------------------------- Anlegen / Entfernen

    /** Legt das gemeinsame Bare-Repo an (oder fetcht es, falls schon vorhanden). */
    public static void ensureShared(Git git, Path repo, String url, Consumer<String> log) throws IOException {
        if (Files.isDirectory(repo)) {
            String existing = git.run(repo, "config", "--get", "remote.origin.url").output().trim();
            if (!sameUrl(existing, url)) {
                throw new IOException("Das Repository " + repo.getFileName() + " existiert bereits,\n"
                        + "gehört aber zu einer anderen URL:\n" + existing
                        + "\n\nBitte einen anderen Projektnamen wählen.");
            }
            log.accept("Gemeinsames Repository existiert bereits – fetche …");
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
     * Neuer Worktree unter {@code target}.
     *
     * @param branch    auszucheckende Branch (lokal oder origin/…); bei {@code newBranch != null} die Basis
     * @param newBranch Name einer neu zu erstellenden Branch oder {@code null}
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
            throw new IOException("Branch „" + branch + "“ wurde weder lokal noch auf origin gefunden.");
        }
    }

    /** Entfernt einen verknüpften Worktree (inkl. aller Dateien darin). */
    public static void remove(Git git, Path commonDir, Path worktree, Consumer<String> log) throws IOException {
        Git.Result r = git.run(commonDir, log, "worktree", "remove", "--force", worktree.toString());
        if (!r.ok()) {
            // z.B. wenn Dateien gesperrt waren und git aufgegeben hat
            SystemActions.deleteRecursively(worktree);
            git.run(commonDir, log, "worktree", "prune");
        }
    }

    public static void move(Git git, Path commonDir, Path from, Path to, Consumer<String> log) throws IOException {
        Files.createDirectories(to.getParent());
        git.runOrThrow(commonDir, log, "worktree", "move", from.toString(), to.toString());
    }

    // ---------------------------------------------------------------- Umwandlung

    /**
     * Wandelt einen normalen Clone (eigener .git-Ordner) in einen Worktree des gemeinsamen Repos {@code repo} um.
     * Die Arbeitsdateien (inkl. ignorierter wie run/ oder build/) bleiben unangetastet.
     * <ul>
     *     <li>Existiert {@code repo} noch nicht, wird der .git-Ordner dorthin verschoben (alles bleibt erhalten,
     *     vorhandene Worktrees werden repariert).</li>
     *     <li>Existiert {@code repo} schon (gleiche Mod, andere Version), werden die lokalen Branches in das
     *     gemeinsame Repo übernommen und der .git-Ordner danach entfernt.</li>
     * </ul>
     */
    public static void convertToShared(Git git, Path project, Path repo, Consumer<String> log) throws IOException {
        Path dotGit = project.resolve(".git");
        if (!Files.isDirectory(dotGit)) {
            throw new IOException("Das Projekt ist kein eigenständiger Clone.");
        }
        for (String marker : List.of("MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "BISECT_LOG",
                "rebase-merge", "rebase-apply")) {
            if (Files.exists(dotGit.resolve(marker))) {
                throw new IOException("Im Projekt läuft gerade eine Git-Operation (" + marker + ").\n"
                        + "Bitte erst abschließen oder abbrechen.");
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
        log.accept("✔ " + project.getFileName() + " ist jetzt ein Worktree von " + repo.getFileName());
    }

    private static void convertByMove(Git git, Path project, Path dotGit, Path repo, String headRef,
                                      String headBefore, String id, Consumer<String> log) throws IOException {
        List<Path> linked = list(git, project).stream()
                .map(Worktree::path)
                .filter(p -> !samePath(p, project))
                .toList();
        Files.createDirectories(repo.getParent());
        log.accept("Verschiebe .git nach " + repo);
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
            log.accept("✖ Umwandlung fehlgeschlagen – stelle Originalzustand wieder her …");
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
            throw new IOException("Dieser Clone hat eigene Worktrees und es gibt bereits ein gemeinsames Repository.\n"
                    + "Bitte die Worktrees dieses Clones zuerst entfernen.");
        }
        if (hasRef(git, project, "refs/stash")) {
            throw new IOException("Im Projekt gibt es Stash-Einträge (git stash).\n"
                    + "Bitte erst anwenden oder löschen – sie würden sonst verloren gehen.");
        }
        String ownUrl = git.run(project, "config", "--get", "remote.origin.url").output().trim();
        String repoUrl = git.run(repo, "config", "--get", "remote.origin.url").output().trim();
        if (!sameUrl(ownUrl, repoUrl)) {
            throw new IOException("Das gemeinsame Repository gehört zu einer anderen URL:\n" + repoUrl
                    + "\nDieses Projekt: " + (ownUrl.isEmpty() ? "(kein origin)" : ownUrl));
        }
        String currentBranch = headRef.startsWith("ref: refs/heads/") ? headRef.substring("ref: refs/heads/".length()) : null;
        Map<String, Path> busy = checkedOutBranches(git, repo);
        if (currentBranch != null && busy.containsKey(currentBranch)) {
            throw new IOException("Die Branch „" + currentBranch + "“ ist im gemeinsamen Repository bereits ausgecheckt:\n"
                    + busy.get(currentBranch));
        }

        // Alle lokalen Branches + HEAD in einen temporären Namensraum holen und dann abgleichen
        log.accept("Übernehme lokale Branches in " + repo.getFileName() + " …");
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
                    // gemeinsames Repo ist schon weiter – nichts verloren
                } else {
                    conflicts.add(name + (busy.containsKey(name) ? " (ausgecheckt in " + busy.get(name) + ")" : " (abweichende Commits)"));
                }
            }
            if (!conflicts.isEmpty()) {
                throw new IOException("Folgende Branches lassen sich nicht automatisch übernehmen:\n  "
                        + String.join("\n  ", conflicts)
                        + "\n\nBitte erst pushen/pullen, damit beide Seiten gleich sind.");
            }
            for (var u : updates.entrySet()) {
                git.runOrThrow(repo, log, "update-ref", "refs/heads/" + u.getKey(), u.getValue());
            }
            // Upstream-Einstellungen (branch.<x>.remote/merge) übernehmen, falls im gemeinsamen Repo nicht gesetzt
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
            log.accept("✖ Umwandlung fehlgeschlagen – stelle Originalzustand wieder her …");
            if (Files.isRegularFile(dotGit)) Files.delete(dotGit);
            if (Files.isDirectory(backup)) SystemActions.move(backup, dotGit);
            SystemActions.deleteRecursively(admin);
            throw e;
        }
        log.accept("Entferne alten .git-Ordner …");
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
            throw new IOException("Prüfung fehlgeschlagen: HEAD ist " + head + " statt " + headBefore);
        }
        Path common = commonDir(git, project).orElseThrow(() -> new IOException("Prüfung fehlgeschlagen: kein Git-Verzeichnis"));
        if (!samePath(common, repo)) {
            throw new IOException("Prüfung fehlgeschlagen: Git-Verzeichnis ist " + common);
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

    // ---------------------------------------------------------------- Pfade / URLs

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
