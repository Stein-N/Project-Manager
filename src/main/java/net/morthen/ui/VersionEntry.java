package net.morthen.ui;

import java.nio.file.Path;

/** Tree node for a Minecraft version folder. */
record VersionEntry(String version, Path path, int count) {
    @Override
    public String toString() {
        return version;
    }
}
