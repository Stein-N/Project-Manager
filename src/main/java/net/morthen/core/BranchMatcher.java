package net.morthen.core;

import java.util.Collection;
import java.util.Optional;

/** Findet die Branch, die am besten zu einer Minecraft-Version passt. */
public final class BranchMatcher {
    private BranchMatcher() {
    }

    /** Priorität: exakt ("1.21.1", "mc1.21.1") &gt; beginnt mit ("1.21.1-fabric") &gt; enthält. */
    public static Optional<String> bestMatch(Collection<String> branches, String version) {
        if (version == null || version.isBlank()) {
            return Optional.empty();
        }
        String exact = null, starts = null, contains = null;
        for (String b : branches) {
            String tail = b.substring(b.lastIndexOf('/') + 1);
            if (tail.equals(version) || tail.equals("mc" + version) || tail.equals("mc-" + version)) {
                if (exact == null) exact = b;
            } else if (tail.startsWith(version + "-") || tail.startsWith(version + "_")) {
                if (starts == null) starts = b;
            } else if (contains == null && containsVersion(b, version)) {
                contains = b;
            }
        }
        return Optional.ofNullable(exact != null ? exact : starts != null ? starts : contains);
    }

    /** "1.21" soll nicht in "1.21.1" gefunden werden. */
    private static boolean containsVersion(String branch, String version) {
        int i = branch.indexOf(version);
        while (i >= 0) {
            int end = i + version.length();
            boolean leftOk = i == 0 || !Character.isDigit(branch.charAt(i - 1)) && branch.charAt(i - 1) != '.';
            boolean rightOk = end == branch.length()
                    || !(Character.isDigit(branch.charAt(end)) || branch.charAt(end) == '.' && end + 1 < branch.length() && Character.isDigit(branch.charAt(end + 1)));
            if (leftOk && rightOk) return true;
            i = branch.indexOf(version, i + 1);
        }
        return false;
    }
}
