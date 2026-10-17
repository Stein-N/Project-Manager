package net.morthen.core;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStreamReader;
import java.nio.charset.StandardCharsets;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Optional;
import java.util.Set;
import java.util.function.Consumer;

/**
 * Thin wrapper around the git command line.
 */
public final class Git {
    public record Result(int exitCode, String output) {
        public boolean ok() {
            return exitCode == 0;
        }
    }

    public record Status(String branch, String upstream, int ahead, int behind, int changes) {
    }

    public record RemoteBranches(List<String> branches, String defaultBranch) {
    }

    private volatile String executable;

    public Git(String executable) {
        this.executable = executable;
    }

    public void setExecutable(String executable) {
        this.executable = executable;
    }

    public Result run(Path dir, String... args) {
        return run(dir, null, args);
    }

    /**
     * Runs git; every output line is also passed to {@code lines} (if not null).
     */
    public Result run(Path dir, Consumer<String> lines, String... args) {
        List<String> command = new ArrayList<>();
        command.add(executable);
        command.addAll(List.of(args));
        ProcessBuilder pb = new ProcessBuilder(command).redirectErrorStream(true);
        if (dir != null) {
            pb.directory(dir.toFile());
        }
        // Never wait for a password prompt in the (non-existent) terminal
        pb.environment().put("GIT_TERMINAL_PROMPT", "0");
        try {
            Process process = pb.start();
            process.getOutputStream().close();
            StringBuilder out = new StringBuilder();
            try (BufferedReader reader = new BufferedReader(
                    new InputStreamReader(process.getInputStream(), StandardCharsets.UTF_8))) {
                String line;
                while ((line = reader.readLine()) != null) {
                    out.append(line).append('\n');
                    if (lines != null) {
                        lines.accept(line);
                    }
                }
            }
            return new Result(process.waitFor(), out.toString().stripTrailing());
        } catch (IOException e) {
            return new Result(-1, "could not start git: " + e.getMessage());
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            return new Result(-1, "interrupted");
        }
    }

    /** Like {@link #run}, but throws an IOException with the git output on a non-zero exit code. */
    public Result runOrThrow(Path dir, Consumer<String> lines, String... args) throws IOException {
        Result result = run(dir, lines, args);
        if (!result.ok()) {
            throw new IOException("git " + String.join(" ", args) + " failed:\n" + result.output());
        }
        return result;
    }

    public Optional<Status> status(Path repo) {
        Result result = run(repo, "status", "--porcelain=v2", "--branch");
        if (!result.ok()) {
            return Optional.empty();
        }
        String branch = null;
        String upstream = null;
        int ahead = 0;
        int behind = 0;
        int changes = 0;
        for (String line : result.output().split("\n")) {
            if (line.isBlank()) {
                continue;
            }
            if (line.startsWith("# branch.head ")) {
                branch = line.substring("# branch.head ".length()).trim();
            } else if (line.startsWith("# branch.upstream ")) {
                upstream = line.substring("# branch.upstream ".length()).trim();
            } else if (line.startsWith("# branch.ab ")) {
                String[] parts = line.substring("# branch.ab ".length()).trim().split("\\s+");
                ahead = parseCount(parts[0]);
                behind = parts.length > 1 ? parseCount(parts[1]) : 0;
            } else if (!line.startsWith("#")) {
                changes++;
            }
        }
        return Optional.of(new Status(branch, upstream, ahead, behind, changes));
    }

    public String remoteUrl(Path repo) {
        Result result = run(repo, "config", "--get", "remote.origin.url");
        return result.ok() ? result.output() : "";
    }

    /**
     * All local and remote branches of a repo (remote prefix stripped, no duplicates).
     */
    public List<String> branches(Path repo) {
        Result result = run(repo, "for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes");
        Set<String> names = new LinkedHashSet<>();
        if (result.ok()) {
            for (String ref : result.output().split("\n")) {
                if (ref.startsWith("refs/heads/")) {
                    names.add(ref.substring("refs/heads/".length()));
                } else if (ref.startsWith("refs/remotes/")) {
                    String rest = ref.substring("refs/remotes/".length());
                    int slash = rest.indexOf('/');
                    if (slash > 0) {
                        String name = rest.substring(slash + 1);
                        if (!name.equals("HEAD")) {
                            names.add(name);
                        }
                    }
                }
            }
        }
        return names.stream().sorted(VersionComparator.INSTANCE.reversed()).toList();
    }

    /**
     * The main branch of a repo: the default branch of {@code origin} ({@code origin/HEAD}), otherwise a local
     * {@code main} or {@code master}; null if none of them exists.
     */
    public String defaultBranch(Path repo) {
        Result head = run(repo, "symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD");
        if (head.ok() && head.output().startsWith("origin/")) {
            return head.output().substring("origin/".length()).strip();
        }
        for (String name : List.of("main", "master")) {
            if (run(repo, "show-ref", "--verify", "--quiet", "refs/heads/" + name).ok()) return name;
        }
        return null;
    }

    /**
     * Branches of a remote repository, without cloning it.
     */
    public RemoteBranches remoteBranches(String url) throws IOException {
        Result result = run(null, "ls-remote", "--symref", url);
        if (!result.ok()) {
            throw new IOException(result.output());
        }
        List<String> branches = new ArrayList<>();
        String defaultBranch = null;
        for (String line : result.output().split("\n")) {
            if (line.startsWith("ref: refs/heads/") && line.endsWith("HEAD")) {
                defaultBranch = line.substring("ref: refs/heads/".length(), line.indexOf('\t')).trim();
                continue;
            }
            int tab = line.indexOf('\t');
            if (tab < 0) {
                continue;
            }
            String ref = line.substring(tab + 1).trim();
            if (ref.startsWith("refs/heads/")) {
                branches.add(ref.substring("refs/heads/".length()));
            }
        }
        branches.sort(VersionComparator.INSTANCE.reversed());
        return new RemoteBranches(branches, defaultBranch);
    }

    private static int parseCount(String value) {
        try {
            return Math.abs(Integer.parseInt(value));
        } catch (NumberFormatException e) {
            return 0;
        }
    }
}
