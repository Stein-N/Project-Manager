package net.morthen.core;

import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;

/**
 * Claude credentials from the variable {@code CLAUDE_API}: several tokens separated by {@code ;}. When a token cannot
 * be used (invalid, expired, usage limit reached …) the Claude call is repeated with the next one. Without tokens
 * Claude runs with the login of the Claude Code CLI.
 * <p>
 * A token is normally an OAuth token of a Claude Pro/Max subscription ({@code claude setup-token}, passed as
 * {@code CLAUDE_CODE_OAUTH_TOKEN}); values starting with {@code sk-ant-api} are Claude Platform API keys
 * ({@code ANTHROPIC_API_KEY}).
 */
public final class ClaudeKeys {
    public static final String VARIABLE = "CLAUDE_API";

    public enum Kind {
        /** Subscription token from {@code claude setup-token} → {@code CLAUDE_CODE_OAUTH_TOKEN}. */
        OAUTH_TOKEN,
        /** Claude Platform API key → {@code ANTHROPIC_API_KEY} (then {@code --bare} is possible). */
        API_KEY
    }

    /** API errors that are tied to the token, so another token may work. */
    private static final Set<Integer> KEY_STATUSES = Set.of(401, 402, 403, 429);
    private static final Set<String> KEY_ERRORS = Set.of("authentication_failed", "billing_error", "permission_error", "rate_limit");
    private static final List<String> KEY_TEXTS = List.of("invalid api key", "credit balance", "api key has been",
            "oauth", "usage limit", "limit reached", "not logged in");

    private ClaudeKeys() {
    }

    /** Tokens in order, trimmed, without empty entries and duplicates. */
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

    public static Kind kind(String key) {
        return key.startsWith("sk-ant-api") ? Kind.API_KEY : Kind.OAUTH_TOKEN;
    }

    /** For logs: never the whole token, only its end ({@code …a1B2}). */
    public static String mask(String key) {
        return key.length() <= 8 ? "…" : "…" + key.substring(key.length() - 4);
    }

    /** The run failed because of the token (and not because of the prompt, a timeout or the budget). */
    public static boolean keyUnusable(ClaudeRunner.Outcome outcome) {
        if (outcome.status() != ClaudeRunner.Status.ERROR) return false;
        if (KEY_STATUSES.contains(outcome.apiErrorStatus())) return true;
        if (outcome.apiError() != null && KEY_ERRORS.contains(outcome.apiError())) return true;
        String text = outcome.text().toLowerCase(Locale.ROOT);
        return KEY_TEXTS.stream().anyMatch(text::contains);
    }
}
