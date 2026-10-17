package net.morthen.core;

import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * A commit message parsed as a Conventional Commit ({@code type(scope)!: subject}) including the backport markers
 * {@code [no-backport]} / {@code [no-backport: 1.20.1]} and the {@code (cherry picked from commit …)} trailers.
 *
 * @param type             lower-case type ({@code fix}, {@code feat}, …) or null if the header is not conventional
 * @param scope            scope or null
 * @param breaking         {@code !} after the type/scope or a {@code BREAKING CHANGE:} footer
 * @param subject          subject without type, scope and markers (the whole first line if not conventional)
 * @param body             everything after the first line, trimmed
 * @param noBackportAll    {@code [no-backport]} without a version
 * @param noBackportTo     versions from {@code [no-backport: …]}
 * @param cherryPickedFrom full or abbreviated SHAs from {@code (cherry picked from commit …)}
 */
public record ConventionalCommit(
        String type,
        String scope,
        boolean breaking,
        String subject,
        String body,
        boolean noBackportAll,
        List<String> noBackportTo,
        List<String> cherryPickedFrom
) {
    private static final Pattern HEADER = Pattern.compile("^([A-Za-z]+)(?:\\(([^)]*)\\))?(!)?:\\s*(.*)$");
    private static final Pattern MARKER = Pattern.compile("\\[no-backport(?::\\s*([^]]*))?]", Pattern.CASE_INSENSITIVE);
    private static final Pattern CHERRY = Pattern.compile("\\(cherry picked from commit ([0-9a-fA-F]{7,40})\\)");
    private static final Pattern BREAKING_FOOTER = Pattern.compile("^BREAKING[ -]CHANGE:", Pattern.MULTILINE);

    public static ConventionalCommit parse(String message) {
        String text = message == null ? "" : message.replace("\r\n", "\n").strip();
        int newline = text.indexOf('\n');
        String header = newline < 0 ? text : text.substring(0, newline).strip();
        String body = newline < 0 ? "" : text.substring(newline + 1).strip();

        boolean noBackportAll = false;
        List<String> noBackportTo = new ArrayList<>();
        Matcher marker = MARKER.matcher(text);
        while (marker.find()) {
            String versions = marker.group(1);
            if (versions == null || versions.isBlank()) {
                noBackportAll = true;
            } else {
                for (String v : versions.split("[,\\s]+")) {
                    if (!v.isBlank()) noBackportTo.add(v.strip());
                }
            }
        }
        List<String> cherryPickedFrom = new ArrayList<>();
        Matcher cherry = CHERRY.matcher(body);
        while (cherry.find()) {
            cherryPickedFrom.add(cherry.group(1).toLowerCase(Locale.ROOT));
        }

        String type = null;
        String scope = null;
        boolean breaking = BREAKING_FOOTER.matcher(body).find();
        String subject = header;
        Matcher m = HEADER.matcher(header);
        if (m.matches()) {
            type = m.group(1).toLowerCase(Locale.ROOT);
            scope = m.group(2) == null || m.group(2).isBlank() ? null : m.group(2).strip();
            breaking |= m.group(3) != null;
            subject = m.group(4);
        }
        subject = MARKER.matcher(subject).replaceAll("").replaceAll("\\s{2,}", " ").strip();
        return new ConventionalCommit(type, scope, breaking, subject, body, noBackportAll,
                List.copyOf(noBackportTo), List.copyOf(cherryPickedFrom));
    }

    public boolean conventional() {
        return type != null;
    }

    /** {@code [no-backport]} or {@code [no-backport: <target>]} forbids porting into {@code target}. */
    public boolean excludedFrom(String target) {
        return noBackportAll || noBackportTo.contains(target);
    }

    /** The commit itself is a backport of another commit. */
    public boolean isBackport() {
        return !cherryPickedFrom.isEmpty();
    }
}
