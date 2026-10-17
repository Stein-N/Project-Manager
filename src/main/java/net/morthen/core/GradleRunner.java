package net.morthen.core;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStreamReader;
import java.nio.charset.Charset;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.concurrent.ConcurrentHashMap;
import java.util.function.Consumer;
import java.util.function.IntConsumer;

/**
 * Runs Gradle wrapper tasks (build, runClient, …) in project folders and keeps track of running ones.
 */
public final class GradleRunner {
    private record Running(Process process, String task) {
    }

    private final Map<Path, Running> running = new ConcurrentHashMap<>();
    private final Charset outputCharset = charset();

    public static Optional<Path> wrapper(Path project) {
        Path wrapper = project.resolve(SystemActions.isWindows() ? "gradlew.bat" : "gradlew");
        return Files.isRegularFile(wrapper) ? Optional.of(wrapper) : Optional.empty();
    }

    public boolean isRunning(Path project) {
        return running.containsKey(project);
    }

    /** Name of the running task in {@code project}, or null. */
    public String runningTask(Path project) {
        Running r = running.get(project);
        return r == null ? null : r.task();
    }

    public int runningCount() {
        return running.size();
    }

    /**
     * Starts {@code task} asynchronously. Every output line goes to {@code out}; {@code done} receives the exit code
     * (on a background thread).
     */
    public void start(Path project, String task, Consumer<String> out, IntConsumer done) throws IOException {
        Process process = launch(project, task);
        Thread reader = new Thread(() -> done.accept(pump(project, process, out)), "gradle-" + task);
        reader.setDaemon(true);
        reader.start();
    }

    /** Runs {@code task} and blocks until it has finished. */
    public int run(Path project, String task, Consumer<String> out) throws IOException {
        return pump(project, launch(project, task), out);
    }

    /** Stops the task in {@code project} including all child processes (e.g. a running Minecraft client). */
    public void stop(Path project) {
        Running r = running.get(project);
        if (r != null) {
            r.process().descendants().forEach(ProcessHandle::destroyForcibly);
            r.process().destroyForcibly();
        }
    }

    public void stopAll() {
        new ArrayList<>(running.keySet()).forEach(this::stop);
    }

    private synchronized Process launch(Path project, String task) throws IOException {
        if (running.containsKey(project)) {
            throw new IOException("A Gradle task is already running in this project: " + runningTask(project));
        }
        Path wrapper = wrapper(project).orElseThrow(() -> new IOException("No Gradle wrapper (gradlew) found in " + project));
        List<String> command = new ArrayList<>();
        if (SystemActions.isWindows()) command.addAll(List.of("cmd.exe", "/c"));
        command.add(wrapper.toAbsolutePath().toString());
        command.addAll(List.of(task.trim().split("\\s+"))); // e.g. "tasks --all"
        command.add("--console=plain");
        Process process = new ProcessBuilder(command)
                .directory(project.toFile())
                .redirectErrorStream(true)
                .start();
        process.getOutputStream().close();
        running.put(project, new Running(process, task));
        return process;
    }

    private int pump(Path project, Process process, Consumer<String> out) {
        try (BufferedReader reader = new BufferedReader(new InputStreamReader(process.getInputStream(), outputCharset))) {
            String line;
            while ((line = reader.readLine()) != null) {
                out.accept(line);
            }
            return process.waitFor();
        } catch (IOException e) {
            out.accept("Error reading Gradle output: " + e.getMessage());
            return -1;
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            return -1;
        } finally {
            running.remove(project);
        }
    }

    private static Charset charset() {
        try {
            return Charset.forName(System.getProperty("native.encoding", "UTF-8"));
        } catch (Exception e) {
            return Charset.defaultCharset();
        }
    }
}
