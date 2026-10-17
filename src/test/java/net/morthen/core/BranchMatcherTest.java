package net.morthen.core;

import org.junit.jupiter.api.Test;

import java.util.List;
import java.util.Optional;

import static org.junit.jupiter.api.Assertions.assertEquals;

class BranchMatcherTest {
    @Test
    void prefersExactThenPrefixThenContains() {
        List<String> branches = List.of("main", "1.21.1-fabric", "1.21.1", "port/1.20.1");
        assertEquals(Optional.of("1.21.1"), BranchMatcher.bestMatch(branches, "1.21.1"));
        assertEquals(Optional.of("1.21.1-fabric"), BranchMatcher.bestMatch(List.of("main", "1.21.1-fabric"), "1.21.1"));
        assertEquals(Optional.of("port/1.20.1"), BranchMatcher.bestMatch(branches, "1.20.1"));
    }

    @Test
    void shorterVersionDoesNotMatchLongerOne() {
        assertEquals(Optional.empty(), BranchMatcher.bestMatch(List.of("1.21.1", "mc1.21.4"), "1.21"));
        assertEquals(Optional.of("dev-1.21"), BranchMatcher.bestMatch(List.of("1.21.1", "dev-1.21"), "1.21"));
    }
}
