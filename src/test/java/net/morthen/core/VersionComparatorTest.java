package net.morthen.core;

import org.junit.jupiter.api.Test;

import java.util.ArrayList;
import java.util.List;

import static org.junit.jupiter.api.Assertions.assertEquals;

class VersionComparatorTest {
    @Test
    void sortsMinecraftVersionsNumerically() {
        List<String> versions = new ArrayList<>(List.of("1.20.1", "1.8.9", "1.21", "1.21.1", "1.12.2"));
        versions.sort(VersionComparator.INSTANCE.reversed());
        assertEquals(List.of("1.21.1", "1.21", "1.20.1", "1.12.2", "1.8.9"), versions);
    }
}
