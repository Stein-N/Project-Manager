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
 * Up to {@link #MAX_PER_LOADER} tasks may run at the same time per loader subproject (e.g. client and server).
 */
public final class GradleRunner {
    public static final int MAX_PER_LOADER = 2;

    private record Key(Path project, String task) {
    }

    private record Running(Process process) {
    }

    private final Map<Key, Running> running = new ConcurrentHashMap<>();
    private final Charset outputCharset = charset();

    public static Optional<Path> wrapper(Path project) {
        Path wrapper = project.resolve(SystemActions.isWindows() ? "gradlew.bat" : "gradlew");
        return Files.isRegularFile(wrapper) ? Optional.of(wrapper) : Optional.empty();
    }

    /** Subproject a task belongs to: ":fabric:runClient" → "fabric", "build" / ":runClient" → "". */
    public static String loaderOf(String task) {
        String path = task.trim().split("\\s+")[0];
        int last = path.lastIndexOf(':');
        return last <= 0 ? "" : path.substring(path.startsWith(":") ? 1 : 0, last);
    }

    public boolean isRunning(Path project) {
        return running.keySet().stream().anyMatch(k -> k.project().equals(project));
    }

    public boolean isRunning(Path project, String task) {
        return running.containsKey(new Key(project, task));
    }

    /** Tasks currently running in {@code project}. */
    public List<String> runningTasks(Path project) {
        return running.keySet().stream().filter(k -> k.project().equals(project)).map(Key::task).sorted().toList();
    }

    /** Whether {@code task} could be started now (not running yet and the loader has a free slot). */
    public boolean canStart(Path project, String task) {
        return !isRunning(project, task) && runningInLoader(project, loaderOf(task)) < MAX_PER_LOADER;
    }

    public int runningCount() {
        return running.size();
    }

    /**
     * Starts {@code task} asynchronously. Every output line goes to {@code out}; {@code done} receives the exit code
     * (on a background thread).
     */
    public void start(Path project, String task, Consumer<String> out, IntConsumer done) throws IOException {
        start(project, task, Map.of(), out, done);
    }

    /** Like {@link #start(Path, String, Consumer, IntConsumer)} with additional environment variables. */
    public void start(Path project, String task, Map<String, String> env, Consumer<String> out, IntConsumer done)
            throws IOException {
        Process process = launch(project, task, env);
        Thread reader = new Thread(() -> done.accept(pump(project, task, process, out)), "gradle-" + task);
        reader.setDaemon(true);
        reader.start();
    }

    /** Runs {@code task} and blocks until it has finished. */
    public int run(Path project, String task, Consumer<String> out) throws IOException {
        return pump(project, task, launch(project, task, Map.of()), out);
    }

    /** Stops one task including all child processes (e.g. a running Minecraft client). */
    public void stop(Path project, String task) {
        Running r = running.get(new Key(project, task));
        if (r != null) {
            r.process().descendants().forEach(ProcessHandle::destroyForcibly);
            r.process().destroyForcibly();
        }
    }

    public void stopAll() {
        new ArrayList<>(running.keySet()).forEach(k -> stop(k.project(), k.task()));
    }

    private int runningInLoader(Path project, String loader) {
        return (int) running.keySet().stream()
                .filter(k -> k.project().equals(project) && loaderOf(k.task()).equals(loader))
                .count();
    }

    private synchronized Process launch(Path project, String task, Map<String, String> env) throws IOException {
        if (isRunning(project, task)) {
            throw new IOException(task + " is already running.");
        }
        String loader = loaderOf(task);
        if (runningInLoader(project, loader) >= MAX_PER_LOADER) {
            throw new IOException("At most " + MAX_PER_LOADER + " Gradle tasks can run at the same time for "
                    + (loader.isEmpty() ? "the root project" : ":" + loader) + ". Stop one first.");
        }
        Path wrapper = wrapper(project).orElseThrow(() -> new IOException("No Gradle wrapper (gradlew) found in " + project));
        List<String> command = new ArrayList<>();
        if (SystemActions.isWindows()) command.addAll(List.of("cmd.exe", "/c"));
        else if (!Files.isExecutable(wrapper)) command.add("sh"); // gradlew without the executable bit (copied folders)
        command.add(wrapper.toAbsolutePath().toString());
        command.addAll(List.of(task.trim().split("\\s+"))); // e.g. "tasks --all"
        command.add("--console=plain");
        ProcessBuilder builder = new ProcessBuilder(command)
                .directory(project.toFile())
                .redirectErrorStream(true);
        builder.environment().putAll(env); // Gradle forwards the client environment to the daemon
        Process process = builder.start();
        process.getOutputStream().close();
        running.put(new Key(project, task), new Running(process));
        return process;
    }

    private int pump(Path project, String task, Process process, Consumer<String> out) {
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
            running.remove(new Key(project, task));
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
