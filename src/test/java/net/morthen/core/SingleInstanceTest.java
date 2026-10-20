package net.morthen.core;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.nio.file.Path;
import java.util.Optional;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

class SingleInstanceTest {
    @TempDir
    Path dir;

    @Test
    void secondStartShowsTheRunningInstance() throws InterruptedException {
        Optional<SingleInstance> first = SingleInstance.acquire(dir, true);
        assertTrue(first.isPresent());
        CountDownLatch shown = new CountDownLatch(1);
        first.get().onShow(shown::countDown);
        try {
            assertFalse(SingleInstance.acquire(dir, true).isPresent());
            assertTrue(shown.await(5, TimeUnit.SECONDS));
        } finally {
            first.get().release();
        }
    }

    @Test
    void startWithTheSystemDoesNotShowTheRunningInstance() throws InterruptedException {
        SingleInstance first = SingleInstance.acquire(dir, true).orElseThrow();
        CountDownLatch shown = new CountDownLatch(1);
        first.onShow(shown::countDown);
        try {
            assertFalse(SingleInstance.acquire(dir, false).isPresent());
            assertFalse(shown.await(500, TimeUnit.MILLISECONDS));
        } finally {
            first.release();
        }
    }

    @Test
    void afterReleaseTheNextStartRuns() {
        SingleInstance.acquire(dir, true).orElseThrow().release();
        Optional<SingleInstance> next = SingleInstance.acquire(dir, true);
        assertTrue(next.isPresent());
        next.get().release();
    }

    @Test
    void wrongTokenIsIgnored() throws Exception {
        SingleInstance first = SingleInstance.acquire(dir, true).orElseThrow();
        CountDownLatch shown = new CountDownLatch(1);
        first.onShow(shown::countDown);
        try {
            Path portFile = dir.resolve("instance.port");
            String port = java.nio.file.Files.readAllLines(portFile).getFirst();
            java.nio.file.Files.writeString(portFile, port + "\nwrong-token\n");
            assertFalse(SingleInstance.signal(dir));
            assertEquals(1, shown.getCount());
        } finally {
            first.release();
        }
    }
}
