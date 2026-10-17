package net.morthen.core;

import java.util.Collection;
import java.util.Optional;

/** Finds the branch that best matches a Minecraft version. */
public final class BranchMatcher {
    private BranchMatcher() {
    }

    /** Priority: exact ("1.21.1", "mc1.21.1") &gt; starts with ("1.21.1-fabric") &gt; contains. */
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

    /** "1.21" must not be found inside "1.21.1". */
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
