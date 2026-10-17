package net.morthen.core;

import java.io.IOException;
import java.io.Reader;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.Locale;
import java.util.Properties;
import java.util.stream.Stream;

/**
 * Liest die Ordnerstruktur {@code <master>/<mcVersion>/<projekt>} ein.
 */
public final class ProjectScanner {
    public record ScanResult(List<String> versions, List<ModProject> projects, List<SharedRepo> repos) {
    }

    /** Gemeinsames Repository unter <master>/.repos mit seinen Worktrees im Master-Ordner. */
    public record SharedRepo(String name, Path path, String url, List<ModProject> worktrees) {
    }

    private ProjectScanner() {
    }

    public static ScanResult scan(Path master, Git git) throws IOException {
        List<String> versions = versions(master);
        List<Path[]> candidates = new ArrayList<>();
        for (String version : versions) {
            for (Path dir : subdirectories(master.resolve(version))) {
                candidates.add(new Path[]{master.resolve(version), dir});
            }
        }
        List<ModProject> projects = candidates.parallelStream()
                .map(c -> inspect(c[0].getFileName().toString(), c[1], git))
                .sorted(Comparator.comparing(ModProject::mcVersion, VersionComparator.INSTANCE.reversed())
                        .thenComparing(ModProject::name, String.CASE_INSENSITIVE_ORDER))
                .toList();
        return new ScanResult(versions, projects, sharedRepos(master, projects, git));
    }

    private static List<SharedRepo> sharedRepos(Path master, List<ModProject> projects, Git git) throws IOException {
        List<SharedRepo> repos = new ArrayList<>();
        for (Path repo : subdirectories(master.resolve(Worktrees.REPOS_DIR))) {
            String file = repo.getFileName().toString();
            if (!file.endsWith(".git")) {
                continue;
            }
            Path real = Worktrees.real(repo);
            List<ModProject> worktrees = projects.stream()
                    .filter(p -> real.equals(p.commonDir()))
                    .toList();
            String url = git.run(repo, "config", "--get", "remote.origin.url").output().trim();
            repos.add(new SharedRepo(file.substring(0, file.length() - 4), real, url, worktrees));
        }
        repos.sort(Comparator.comparing(SharedRepo::name, String.CASE_INSENSITIVE_ORDER));
        return repos;
    }

    /** Versionsordner, neueste zuerst. */
    public static List<String> versions(Path master) throws IOException {
        return subdirectories(master).stream()
                .map(p -> p.getFileName().toString())
                .sorted(VersionComparator.INSTANCE.reversed())
                .toList();
    }

    public static ModProject inspect(String mcVersion, Path dir, Git git) {
        String name = dir.getFileName().toString();
        Path dotGit = dir.resolve(".git");
        boolean isGit = Files.exists(dotGit);
        boolean linked = Files.isRegularFile(dotGit);
        Path commonDir = null;
        String branch = null;
        String upstream = null;
        String remote = "";
        int changes = 0;
        int ahead = 0;
        int behind = 0;
        if (isGit) {
            var status = git.status(dir);
            if (status.isPresent()) {
                branch = status.get().branch();
                upstream = status.get().upstream();
                changes = status.get().changes();
                ahead = status.get().ahead();
                behind = status.get().behind();
            }
            remote = git.remoteUrl(dir);
            commonDir = Worktrees.commonDir(git, dir).orElse(null);
        }

        Properties gradle = readProperties(dir.resolve("gradle.properties"));
        String gradleMc = first(gradle, "minecraft_version", "minecraftVersion", "mc_version", "mcVersion");
        String modVersion = first(gradle, "mod_version", "modVersion", "version");

        return new ModProject(name, mcVersion, dir, isGit, branch, upstream, remote,
                changes, ahead, behind, detectLoader(dir, gradle), gradleMc, modVersion, linked, commonDir);
    }

    static String detectLoader(Path dir, Properties gradle) {
        String build = read(dir.resolve("build.gradle")) + read(dir.resolve("build.gradle.kts"))
                + read(dir.resolve("settings.gradle")) + read(dir.resolve("settings.gradle.kts"));
        String lower = build.toLowerCase(Locale.ROOT);

        if (lower.contains("architectury")) {
            return "Architectury";
        }
        boolean fabricDir = Files.isDirectory(dir.resolve("fabric"));
        boolean forgeDir = Files.isDirectory(dir.resolve("forge"));
        boolean neoDir = Files.isDirectory(dir.resolve("neoforge"));
        if (fabricDir && (forgeDir || neoDir) || forgeDir && neoDir) {
            List<String> loaders = new ArrayList<>();
            if (fabricDir) loaders.add("Fabric");
            if (forgeDir) loaders.add("Forge");
            if (neoDir) loaders.add("NeoForge");
            return "Multiloader (" + String.join(", ", loaders) + ")";
        }
        if (lower.contains("net.neoforged") || gradle.containsKey("neo_version")) {
            return "NeoForge";
        }
        if (lower.contains("quilt-loom") || lower.contains("org.quiltmc")) {
            return "Quilt";
        }
        if (lower.contains("fabric-loom") || lower.contains("net.fabricmc")) {
            return "Fabric";
        }
        if (lower.contains("net.minecraftforge") || gradle.containsKey("forge_version")) {
            return "Forge";
        }
        return null;
    }

    private static List<Path> subdirectories(Path dir) throws IOException {
        if (!Files.isDirectory(dir)) {
            return List.of();
        }
        try (Stream<Path> stream = Files.list(dir)) {
            return stream.filter(Files::isDirectory)
                    .filter(p -> !p.getFileName().toString().startsWith("."))
                    .toList();
        }
    }

    private static Properties readProperties(Path file) {
        Properties props = new Properties();
        if (Files.isRegularFile(file)) {
            try (Reader reader = Files.newBufferedReader(file)) {
                props.load(reader);
            } catch (IOException | IllegalArgumentException ignored) {
                // unlesbare Datei -> keine Infos
            }
        }
        return props;
    }

    private static String first(Properties props, String... keys) {
        for (String key : keys) {
            String value = props.getProperty(key);
            if (value != null && !value.isBlank()) {
                return value.trim();
            }
        }
        return null;
    }

    private static String read(Path file) {
        try {
            return Files.isRegularFile(file) ? Files.readString(file) : "";
        } catch (IOException e) {
            return "";
        }
    }
}
