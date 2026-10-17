package net.morthen.core;

import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;

/**
 * Anthropic API keys from the variable {@code CLAUDE_API}: several keys separated by {@code ;}. When a key cannot be
 * used (invalid, no credit, rate limited …) the Claude call is repeated with the next one. Without keys Claude runs
 * with the login of the Claude Code CLI.
 */
public final class ClaudeKeys {
    public static final String VARIABLE = "CLAUDE_API";

    /** API errors that are tied to the key, so another key may work. */
    private static final Set<Integer> KEY_STATUSES = Set.of(401, 402, 403, 429);
    private static final Set<String> KEY_ERRORS = Set.of("authentication_failed", "billing_error", "permission_error", "rate_limit");

    private ClaudeKeys() {
    }

    /** Keys in order, trimmed, without empty entries and duplicates. */
    public static List<String> parse(String value) {
        if (value == null) return List.of();
        Set<String> keys = new LinkedHashSet<>();
        for (String part : value.split(";")) {
            String key = part.strip();
            if (!key.isEmpty()) keys.add(key);
        }
        return List.copyOf(keys);
    }

    /** {@code CLAUDE_API} from the master folder's .env, otherwise from the system environment. */
    public static List<String> resolve(Map<String, String> dotEnv, Map<String, String> systemEnv) {
        String value = dotEnv.get(VARIABLE);
        if (value == null || value.isBlank()) value = systemEnv.get(VARIABLE);
        return parse(value);
    }

    /** For logs: never the whole key, only its end ({@code …a1B2}). */
    public static String mask(String key) {
        return key.length() <= 8 ? "…" : "…" + key.substring(key.length() - 4);
    }

    /** The run failed because of the key (and not because of the prompt, a timeout or the budget). */
    public static boolean keyUnusable(ClaudeRunner.Outcome outcome) {
        if (outcome.status() != ClaudeRunner.Status.ERROR) return false;
        if (KEY_STATUSES.contains(outcome.apiErrorStatus())) return true;
        if (outcome.apiError() != null && KEY_ERRORS.contains(outcome.apiError())) return true;
        String text = outcome.text().toLowerCase(java.util.Locale.ROOT);
        return text.contains("invalid api key") || text.contains("credit balance") || text.contains("api key has been");
    }
}
