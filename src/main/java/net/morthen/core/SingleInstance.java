package net.morthen.core;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.channels.FileChannel;
import java.nio.channels.FileLock;
import java.nio.channels.OverlappingFileLockException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.util.List;
import java.util.Optional;
import java.util.UUID;

/**
 * Only one Project Manager runs at a time. The first start holds a lock file and listens on a loopback port; a second
 * start asks it to show its window and ends. This also brings back a minimized window on systems without the tray
 * icon (it is Windows only).
 */
public final class SingleInstance {
    private static final String LOCK_FILE = "instance.lock";
    private static final String PORT_FILE = "instance.port";
    private static final String SHOW = "show";
    private static final String OK = "ok";
    private static final int TIMEOUT_MS = 2000;

    private final Path dir;
    private final FileChannel channel;
    private final FileLock lock;
    private final ServerSocket server;
    private final String token;
    private volatile Runnable onShow;

    private SingleInstance(Path dir, FileChannel channel, FileLock lock, ServerSocket server, String token) {
        this.dir = dir;
        this.channel = channel;
        this.lock = lock;
        this.server = server;
        this.token = token;
    }

    /**
     * Becomes the running instance, or – if another one runs already – asks it to show its window.
     *
     * @param dir  folder of the lock and port file (the settings folder)
     * @param show whether the running instance should show its window (false for a start with the system)
     * @return the instance, or empty if another instance runs (this start should end then)
     */
    public static Optional<SingleInstance> acquire(Path dir, boolean show) {
        FileChannel channel = null;
        try {
            Files.createDirectories(dir);
            channel = FileChannel.open(dir.resolve(LOCK_FILE), StandardOpenOption.CREATE, StandardOpenOption.WRITE);
            FileLock lock = tryLock(channel);
            if (lock == null) {
                channel.close();
                if (show) signal(dir);
                return Optional.empty();
            }
            ServerSocket server = new ServerSocket(0, 10, InetAddress.getLoopbackAddress());
            String token = UUID.randomUUID().toString();
            SingleInstance instance = new SingleInstance(dir, channel, lock, server, token);
            instance.writePortFile();
            instance.listen();
            return Optional.of(instance);
        } catch (IOException e) {
            // no single-instance protection, but the tool still starts
            System.err.println("Single instance check failed: " + e.getMessage());
            closeQuietly(channel);
            return Optional.of(new SingleInstance(dir, null, null, null, ""));
        }
    }

    private static FileLock tryLock(FileChannel channel) throws IOException {
        try {
            return channel.tryLock();
        } catch (OverlappingFileLockException e) { // held by this JVM already
            return null;
        }
    }

    /** Asks the running instance to show its window; it may still be starting, so this retries for a moment. */
    static boolean signal(Path dir) {
        for (int attempt = 0; attempt < 10; attempt++) {
            try {
                List<String> lines = Files.readAllLines(dir.resolve(PORT_FILE), StandardCharsets.UTF_8);
                if (lines.size() >= 2) {
                    int port = Integer.parseInt(lines.get(0).strip());
                    String token = lines.get(1).strip();
                    try (Socket socket = new Socket()) {
                        socket.connect(new InetSocketAddress(InetAddress.getLoopbackAddress(), port), TIMEOUT_MS);
                        socket.setSoTimeout(TIMEOUT_MS);
                        OutputStream out = socket.getOutputStream();
                        out.write((token + "\n" + SHOW + "\n").getBytes(StandardCharsets.UTF_8));
                        out.flush();
                        BufferedReader in = new BufferedReader(new InputStreamReader(socket.getInputStream(), StandardCharsets.UTF_8));
                        if (OK.equals(in.readLine())) return true;
                    }
                }
            } catch (IOException | RuntimeException e) {
                // port file not written yet or outdated – try again
            }
            try {
                Thread.sleep(300);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                return false;
            }
        }
        return false;
    }

    /** Called (on a background thread) when another start asks for the window. */
    public void onShow(Runnable action) {
        this.onShow = action;
    }

    /** Gives up being the running instance, e.g. right before an update starts the new version. */
    public void release() {
        closeQuietly(server);
        try {
            Files.deleteIfExists(dir.resolve(PORT_FILE));
        } catch (IOException ignored) {
            // the lock decides, a stale port file does no harm
        }
        try {
            if (lock != null) lock.release();
        } catch (IOException ignored) {
            // closing the channel releases it as well
        }
        closeQuietly(channel);
    }

    private void writePortFile() throws IOException {
        Path file = dir.resolve(PORT_FILE);
        Path tmp = dir.resolve(PORT_FILE + ".tmp");
        Files.writeString(tmp, server.getLocalPort() + "\n" + token + "\n", StandardCharsets.UTF_8);
        Files.move(tmp, file, java.nio.file.StandardCopyOption.REPLACE_EXISTING);
    }

    private void listen() {
        Thread thread = new Thread(() -> {
            while (!server.isClosed()) {
                try (Socket socket = server.accept()) {
                    socket.setSoTimeout(TIMEOUT_MS);
                    BufferedReader in = new BufferedReader(new InputStreamReader(socket.getInputStream(), StandardCharsets.UTF_8));
                    if (!token.equals(in.readLine()) || !SHOW.equals(in.readLine())) continue;
                    Runnable action = onShow;
                    if (action != null) action.run();
                    socket.getOutputStream().write((OK + "\n").getBytes(StandardCharsets.UTF_8));
                } catch (IOException e) {
                    // closed by release(), or a client that went away
                }
            }
        }, "single-instance");
        thread.setDaemon(true);
        thread.start();
    }

    private static void closeQuietly(AutoCloseable closeable) {
        try {
            if (closeable != null) closeable.close();
        } catch (Exception ignored) {
            // nothing to do
        }
    }
}
