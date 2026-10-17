package net.morthen.core;

import org.junit.jupiter.api.Test;

import java.util.List;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

class ConventionalCommitTest {
    @Test
    void parsesTypeScopeAndSubject() {
        ConventionalCommit c = ConventionalCommit.parse("Fix(render): Crash with empty slot\n\nLonger text.");
        assertEquals("fix", c.type());
        assertEquals("render", c.scope());
        assertEquals("Crash with empty slot", c.subject());
        assertEquals("Longer text.", c.body());
        assertFalse(c.breaking());
        assertTrue(c.conventional());
    }

    @Test
    void breakingByBangOrFooter() {
        assertTrue(ConventionalCommit.parse("feat!: new config format").breaking());
        assertTrue(ConventionalCommit.parse("feat(api)!: x").breaking());
        assertTrue(ConventionalCommit.parse("feat: x\n\nBREAKING CHANGE: old configs are ignored").breaking());
        assertFalse(ConventionalCommit.parse("feat: x\n\nNo breaking change here").breaking());
    }

    @Test
    void nonConventionalKeepsWholeHeader() {
        ConventionalCommit c = ConventionalCommit.parse("Update README");
        assertNull(c.type());
        assertFalse(c.conventional());
        assertEquals("Update README", c.subject());
    }

    @Test
    void backportMarkers() {
        ConventionalCommit all = ConventionalCommit.parse("fix: tooltip [no-backport]");
        assertTrue(all.excludedFrom("1.20.1"));
        assertEquals("tooltip", all.subject());

        ConventionalCommit one = ConventionalCommit.parse("fix: tooltip [no-backport: 1.20.1]");
        assertTrue(one.excludedFrom("1.20.1"));
        assertFalse(one.excludedFrom("1.21.1"));

        ConventionalCommit several = ConventionalCommit.parse("fix: sync\n\n[No-Backport: 1.20.1, 1.21.1]");
        assertEquals(List.of("1.20.1", "1.21.1"), several.noBackportTo());
        assertFalse(ConventionalCommit.parse("fix: sync").excludedFrom("1.20.1"));
    }

    @Test
    void cherryPickTrailers() {
        ConventionalCommit c = ConventionalCommit.parse("""
                fix: sync

                (cherry picked from commit 0123456789ABCDEF0123456789abcdef01234567)
                (cherry picked from commit abcdef1)
                """);
        assertTrue(c.isBackport());
        assertEquals(List.of("0123456789abcdef0123456789abcdef01234567", "abcdef1"), c.cherryPickedFrom());
        assertFalse(ConventionalCommit.parse("fix: sync").isBackport());
    }
}
