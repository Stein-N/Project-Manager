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
