package net.morthen.core;

import org.junit.jupiter.api.Test;

import java.util.List;

import static org.junit.jupiter.api.Assertions.assertEquals;
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

            Fabric tasks
            ------------
            fabric:runClient - Starts the client run configuration.
            fabric:runGametest - not offered
            fabric:runGametests - Runs the game tests.
            fabric:runClientGametest - not offered

            ForgeGradle runs tasks
            ----------------------
            forge:runClient
            forge:runGameTestServer - Runs the game test server.

            Mod development tasks
            ---------------------
            neoforge:runClient - Runs the 'client' run.
            neoforge:runGameTestServer - Runs the 'gameTestServer' run.
            neoforge:runClientGameTest - not offered

            Publishing tasks
            ----------------
            publish - Publishes all publications produced by this project.
            publishMods - Publish all mods.
            neoforge:publishMods - Publish the mod.
            BUILD SUCCESSFUL in 4s
            """;

    @Test
    void generalTasksAndOneGametestRunPerLoader() {
        List<GradleTasks.Group> groups = GradleTasks.group(GradleTasks.parse(OUTPUT), "General");
        assertEquals(List.of("General", "Fabric", "Forge", "NeoForge"), groups.stream().map(GradleTasks.Group::name).toList());
        assertEquals(List.of("build", "publish", "publishMods"), paths(groups.get(0)));
        assertEquals(List.of(":fabric:runGametests"), paths(groups.get(1)));
        assertEquals(List.of(":forge:runGameTestServer"), paths(groups.get(2)));
        // NeoForge's ModDevGradle spells it runGameTestServer – matched case-insensitively
        assertEquals(List.of(":neoforge:runGameTestServer"), paths(groups.get(3)));
        assertTrue(groups.get(0).tasks().get(2).publishes());
    }

    @Test
    void singleLoaderProjectUsesRootGametestOfItsLoader() {
        List<GradleTasks.RunTask> tasks = List.of(new GradleTasks.RunTask(":runGametests", ""),
                new GradleTasks.RunTask(":runGameTestServer", ""), new GradleTasks.RunTask(":runClient", ""));
        List<GradleTasks.Group> groups = GradleTasks.group(tasks, "Fabric");
        assertEquals(List.of("General", "Fabric"), groups.stream().map(GradleTasks.Group::name).toList());
        assertEquals(List.of(":runGametests"), paths(groups.get(1)));
        assertEquals(List.of("build"), paths(groups.get(0)), "General always offers Build");
    }

    private static List<String> paths(GradleTasks.Group group) {
        return group.tasks().stream().map(GradleTasks.RunTask::path).toList();
    }
}
