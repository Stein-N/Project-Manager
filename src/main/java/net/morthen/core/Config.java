package net.morthen.core;

import java.io.IOException;
import java.io.Reader;
import java.io.Writer;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Optional;
import java.util.Properties;

/**
 * Persistent settings in ~/.mc-project-manager/config.properties.
 */
public final class Config {
    private static final Path DIR = Path.of(System.getProperty("user.home"), ".mc-project-manager");
    private static final Path FILE = DIR.resolve("config.properties");

    private final Properties props = new Properties();

    private Config() {
    }

    public static Config load() {
        Config config = new Config();
        if (Files.isRegularFile(FILE)) {
            try (Reader reader = Files.newBufferedReader(FILE)) {
                config.props.load(reader);
            } catch (IOException e) {
                System.err.println("Could not load configuration: " + e.getMessage());
            }
        }
        return config;
    }

    public void save() {
        try {
            Files.createDirectories(DIR);
            try (Writer writer = Files.newBufferedWriter(FILE)) {
                props.store(writer, "MC Mod Project Manager");
            }
        } catch (IOException e) {
            System.err.println("Could not save configuration: " + e.getMessage());
        }
    }

    public Optional<Path> masterPath() {
        return path("master");
    }

    public void setMasterPath(Path path) {
        setPath("master", path);
    }

    public Optional<Path> ideaPath() {
        return path("idea");
    }

    public void setIdeaPath(Path path) {
        setPath("idea", path);
    }

    public String gitExecutable() {
        String git = props.getProperty("git", "").trim();
        return git.isEmpty() ? "git" : git;
    }

    public void setGitExecutable(String git) {
        if (git == null || git.isBlank()) {
            props.remove("git");
        } else {
            props.setProperty("git", git.trim());
        }
    }

    /** Interval of the background fetch in minutes; 0 = off. */
    public int autoFetchMinutes() {
        try {
            return Math.max(0, Integer.parseInt(props.getProperty("autoFetchMinutes", "10").trim()));
        } catch (NumberFormatException e) {
            return 10;
        }
    }

    public void setAutoFetchMinutes(int minutes) {
        props.setProperty("autoFetchMinutes", Integer.toString(Math.max(0, minutes)));
    }

    public boolean minimizeToTray() {
        return Boolean.parseBoolean(props.getProperty("minimizeToTray", "true"));
    }

    public void setMinimizeToTray(boolean value) {
        props.setProperty("minimizeToTray", Boolean.toString(value));
    }

    /** Path of the Claude Code CLI; empty = search the PATH. */
    public String claudeExecutable() {
        return props.getProperty("claude", "").trim();
    }

    public void setClaudeExecutable(String claude) {
        setString("claude", claude);
    }

    /** Default model: Sonnet writes changelogs as good as Opus for this task, faster and with less usage. */
    public static final String DEFAULT_CLAUDE_MODEL = "sonnet";

    /** Model for Claude runs ({@code --model}, an alias like {@code sonnet}/{@code opus} or a full model id). */
    public String claudeModel() {
        String model = props.getProperty("claudeModel", "").trim();
        return model.isEmpty() ? DEFAULT_CLAUDE_MODEL : model;
    }

    public void setClaudeModel(String model) {
        setString("claudeModel", model);
    }

    /** Spending limit per Claude run in US dollars; 0 = no limit. */
    public double claudeMaxBudgetUsd() {
        try {
            return Math.max(0, Double.parseDouble(props.getProperty("claudeMaxBudgetUsd", "1.0").trim()));
        } catch (NumberFormatException e) {
            return 1.0;
        }
    }

    public void setClaudeMaxBudgetUsd(double usd) {
        props.setProperty("claudeMaxBudgetUsd", Double.toString(Math.max(0, usd)));
    }

    /** A Claude run is stopped after this many minutes. */
    public int claudeTimeoutMinutes() {
        try {
            return Math.max(1, Integer.parseInt(props.getProperty("claudeTimeoutMinutes", "15").trim()));
        } catch (NumberFormatException e) {
            return 15;
        }
    }

    public void setClaudeTimeoutMinutes(int minutes) {
        props.setProperty("claudeTimeoutMinutes", Integer.toString(Math.max(1, minutes)));
    }

    /**
     * Commit types that are backported from {@code mod} into {@code target}; empty set = all types,
     * empty Optional = not configured yet.
     */
    public Optional<java.util.Set<String>> backportTypes(String mod, String target) {
        String value = props.getProperty(backportKey(mod, target));
        if (value == null) return Optional.empty();
        if (value.isBlank() || value.trim().equals("*")) return Optional.of(java.util.Set.of());
        java.util.Set<String> types = new java.util.LinkedHashSet<>();
        for (String t : value.split(",")) {
            if (!t.isBlank()) types.add(t.trim());
        }
        return Optional.of(types);
    }

    /** @param types empty = all types */
    public void setBackportTypes(String mod, String target, java.util.Collection<String> types) {
        props.setProperty(backportKey(mod, target), types.isEmpty() ? "*" : String.join(",", types));
    }

    private static String backportKey(String mod, String target) {
        return "backport.types." + mod.replaceAll("[^\\w.-]", "_") + "." + target.replaceAll("[^\\w.-]", "_");
    }

    private void setString(String key, String value) {
        if (value == null || value.isBlank()) {
            props.remove(key);
        } else {
            props.setProperty(key, value.trim());
        }
    }

    private Optional<Path> path(String key) {
        String value = props.getProperty(key, "").trim();
        return value.isEmpty() ? Optional.empty() : Optional.of(Path.of(value));
    }

    private void setPath(String key, Path path) {
        if (path == null) {
            props.remove(key);
        } else {
            props.setProperty(key, path.toAbsolutePath().toString());
        }
    }
}
