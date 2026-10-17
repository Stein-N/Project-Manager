package net.morthen.core;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.Map;
import java.util.stream.Collectors;

import static org.junit.jupiter.api.Assertions.assertEquals;

class GradleCacheTest {
    @TempDir
    Path tmp;

    @Test
    void marksDataNoProjectUses() throws IOException {
        Path home = tmp.resolve("gradle-home");
        for (String dir : List.of("wrapper/dists/gradle-8.10.2-bin/abc", "wrapper/dists/gradle-8.8-all/def",
                "caches/8.10.2", "caches/8.8", "caches/modules-2", "caches/fabric-loom/1.21.1",
                "caches/fabric-loom/1.20.1", "caches/fabric-loom/assets", "daemon/8.8")) {
            Files.createDirectories(home.resolve(dir));
        }
        Files.writeString(home.resolve("caches/8.8/file.bin"), "12345");

        Path project = tmp.resolve("master/1.21.1/Mod");
        Files.createDirectories(project.resolve("gradle/wrapper"));
        Files.createDirectories(project.resolve("build/libs"));
        Files.createDirectories(project.resolve("fabric/build"));
        Files.writeString(project.resolve("gradle/wrapper/gradle-wrapper.properties"),
                "distributionUrl=https\\://services.gradle.org/distributions/gradle-8.10.2-bin.zip\n");
        ModProject mod = new ModProject("Mod", "1.21.1", project, false, null, null, "", 0, 0, 0,
                null, "1.21.1", null, false, null, null);

        List<GradleCache.Item> items = GradleCache.scan(home, List.of(mod));
        Map<String, GradleCache.Status> status = items.stream()
                .collect(Collectors.toMap(i -> i.category() + ":" + i.name(), GradleCache.Item::status));

        assertEquals(GradleCache.Status.USED, status.get("Gradle distribution:gradle-8.10.2-bin"));
        assertEquals(GradleCache.Status.UNUSED, status.get("Gradle distribution:gradle-8.8-all"));
        assertEquals(GradleCache.Status.USED, status.get("Gradle 8.10.2 cache:8.10.2"));
        assertEquals(GradleCache.Status.UNUSED, status.get("Gradle 8.8 cache:8.8"));
        assertEquals(GradleCache.Status.UNUSED, status.get("Gradle 8.8 daemon logs:8.8"));
        assertEquals(GradleCache.Status.USED, status.get("Loom (Minecraft 1.21.1):1.21.1"));
        assertEquals(GradleCache.Status.UNUSED, status.get("Loom (Minecraft 1.20.1):1.20.1"));
        assertEquals(GradleCache.Status.SHARED, status.get("Loom shared:fabric-loom/assets"));
        assertEquals(GradleCache.Status.SHARED, status.get("Dependency cache:caches/modules-2"));
        assertEquals(GradleCache.Status.BUILD_OUTPUT, status.get("Project build output:1.21.1/Mod/build"));
        assertEquals(GradleCache.Status.BUILD_OUTPUT, status.get("Project build output:1.21.1/Mod/fabric/build"));
        assertEquals(GradleCache.Status.UNUSED, items.getFirst().status(), "unused items come first");
        assertEquals(5, GradleCache.size(home.resolve("caches/8.8")));
        assertEquals("1.5 KB", GradleCache.formatSize(1536));
    }
}
