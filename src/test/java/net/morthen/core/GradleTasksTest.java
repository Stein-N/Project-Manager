package net.morthen.core;

import org.junit.jupiter.api.Test;

import java.util.List;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

class GradleTasksTest {
    /** Shortened output of "gradlew tasks --all" for a MultiLoader project. */
    private static final String OUTPUT = """
            > Task :tasks

            ------------------------------------------------------------
            Tasks runnable from root project 'Resource-Library'
            ------------------------------------------------------------

            Build tasks
            -----------
            assemble - Assembles the outputs of this project.
            build - Assembles and tests this project.
            fabric:build - Assembles and tests this project.
            neoforge:build - Assembles and tests this project.

            Fabric tasks
            ------------
            fabric:runClient - Starts the client run configuration.
            fabric:runServer - Starts the server run configuration.
            fabric:runTestmodClient
            fabric:runGametest - Runs the game tests.
            fabric:runClientGametest
            fabric:runDatagen

            Mod development tasks
            ---------------------
            neoforge:runClient - Runs the 'client' run.
            neoforge:runGameTestServer - Runs the 'gameTestServer' run.
            neoforge:runData - Runs the 'data' run.

            Publishing tasks
            ----------------
            publish - Publishes all publications produced by this project.
            publishMods - Publish all mods.
            fabric:publish - Publishes all publications produced by this project.
            fabric:publishMods - Publish the mod.
            fabric:publishToMavenLocal - not offered
            neoforge:publishMods - Publish the mod.
            BUILD SUCCESSFUL in 4s
            """;

    @Test
    void buildAndPublishAreGeneralGametestsPerLoader() {
        List<GradleTasks.RunTask> tasks = GradleTasks.parse(OUTPUT);
        List<String> paths = tasks.stream().map(GradleTasks.RunTask::path).toList();
        assertEquals(List.of("build", ":fabric:runGametest", ":fabric:runClientGametest", ":neoforge:runGameTestServer",
                "publish", "publishMods"), paths);
        assertFalse(paths.stream().anyMatch(p -> p.endsWith("runClient") || p.endsWith("runServer")), "no client/server runs");

        List<GradleTasks.Group> groups = GradleTasks.group(tasks, "General");
        assertEquals(List.of("General", "Fabric", "NeoForge"), groups.stream().map(GradleTasks.Group::name).toList());
        assertEquals(List.of("Build", "Publish", "Publish mods"), labels(groups.get(0)));
        assertEquals(List.of("Client gametest", "Gametest"), labels(groups.get(1)));
        assertEquals(List.of("Game test server"), labels(groups.get(2)));
        assertTrue(groups.get(0).tasks().get(2).publishes());
    }

    @Test
    void olderCachesAreNormalized() {
        // cache of the previous version: client runs and per-loader build/publish tasks
        List<GradleTasks.RunTask> cached = List.of(new GradleTasks.RunTask(":fabric:runClient", ""),
                new GradleTasks.RunTask(":fabric:build", ""), new GradleTasks.RunTask(":fabric:publishMods", ""),
                new GradleTasks.RunTask(":neoforge:publishMods", ""), new GradleTasks.RunTask(":fabric:runGametest", ""));
        List<GradleTasks.Group> groups = GradleTasks.group(cached, "General");
        assertEquals(List.of("General", "Fabric"), groups.stream().map(GradleTasks.Group::name).toList());
        assertEquals(List.of("build", "publishMods"), groups.get(0).tasks().stream().map(GradleTasks.RunTask::path).toList());
        assertEquals(List.of("Gametest"), labels(groups.get(1)));
    }

    private static List<String> labels(GradleTasks.Group group) {
        return group.tasks().stream().map(GradleTasks.RunTask::label).toList();
    }
}
