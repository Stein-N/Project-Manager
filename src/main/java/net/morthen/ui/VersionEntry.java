package net.morthen.ui;

import java.nio.file.Path;

/** Knoten im Baum für einen Minecraft-Versionsordner. */
record VersionEntry(String version, Path path, int count) {
    @Override
    public String toString() {
        return version;
    }
}
