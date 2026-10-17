package net.morthen.core;

import java.util.Comparator;

/**
 * Vergleicht Versionsstrings wie "1.20.1", "1.21" oder "1.8.9" numerisch (aufsteigend).
 */
public final class VersionComparator implements Comparator<String> {
    public static final VersionComparator INSTANCE = new VersionComparator();

    private VersionComparator() {
    }

    @Override
    public int compare(String a, String b) {
        String[] pa = a.split("[^0-9A-Za-z]+");
        String[] pb = b.split("[^0-9A-Za-z]+");
        for (int i = 0; i < Math.min(pa.length, pb.length); i++) {
            int cmp = comparePart(pa[i], pb[i]);
            if (cmp != 0) {
                return cmp;
            }
        }
        return Integer.compare(pa.length, pb.length);
    }

    private static int comparePart(String a, String b) {
        boolean na = a.chars().allMatch(Character::isDigit) && !a.isEmpty();
        boolean nb = b.chars().allMatch(Character::isDigit) && !b.isEmpty();
        if (na && nb) {
            return Long.compare(Long.parseLong(a), Long.parseLong(b));
        }
        if (na != nb) {
            return na ? 1 : -1; // Zahlen vor Text (z.B. "1.21" > "1.x")
        }
        return a.compareToIgnoreCase(b);
    }
}
