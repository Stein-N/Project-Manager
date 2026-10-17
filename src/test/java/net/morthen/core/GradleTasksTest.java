package net.morthen.core;

import org.junit.jupiter.api.Test;

import java.util.List;

import static org.junit.jupiter.api.Assertions.assertEquals;

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
            fabric:runServer - Starts the server run configuration.
            fabric:runTestmodClient
            fabric:runGametest - Runs the game tests.
            fabric:runDatagen

            Mod development tasks
            ---------------------
            neoforge:runClient - Runs the 'client' run.
            neoforge:runGameTestServer - Runs the 'gameTestServer' run.
            neoforge:runData - Runs the 'data' run.
            neoforge:runServer - Runs the 'server' run.
            common:runIdeaSync

            Other tasks
            -----------
            runAll - custom root task
            run - not a run configuration
            BUILD SUCCESSFUL in 4s
            """;

    @Test
    void parsesAndGroupsRunTasksPerLoader() {
        List<GradleTasks.RunTask> tasks = GradleTasks.parse(OUTPUT);
        assertEquals(10, tasks.size()); // common:runIdeaSync is not a run configuration
        assertEquals(":fabric:runClient", tasks.getFirst().path());

        List<GradleTasks.Group> groups = GradleTasks.group(tasks, "General");
        assertEquals(List.of("General", "Fabric", "NeoForge"), groups.stream().map(GradleTasks.Group::name).toList());

        GradleTasks.Group fabric = groups.get(1);
        assertEquals(List.of("Client", "Server", "Testmod client", "Gametest", "Datagen"),
                fabric.tasks().stream().map(GradleTasks.RunTask::label).toList());
        GradleTasks.Group neo = groups.get(2);
        assertEquals(List.of("Client", "Server", "Game test server", "Data"),
                neo.tasks().stream().map(GradleTasks.RunTask::label).toList());
        assertEquals(":runAll", groups.getFirst().tasks().getFirst().path(), "root tasks keep an absolute path");
    }
}
