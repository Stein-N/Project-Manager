package net.morthen.core;

import org.junit.jupiter.api.Test;

import java.io.IOException;
import java.util.List;
import java.util.Map;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

class GithubReposTest {
    @Test
    void parsesRepos() throws IOException {
        List<GithubRepos.Repo> repos = GithubRepos.parse("""
                [{"name":"cool-mod","full_name":"me/cool-mod","description":"Adds things","private":true,"fork":false,
                  "archived":false,"clone_url":"https://github.com/me/cool-mod.git","html_url":"https://github.com/me/cool-mod",
                  "pushed_at":"2026-01-01T00:00:00Z","owner":{"login":"me"}},
                 {"name":"website","full_name":"org/website","description":null,"private":false,"fork":true,
                  "archived":true,"clone_url":"https://github.com/org/website.git","owner":{"login":"org"}}]""");
        assertEquals(2, repos.size());
        GithubRepos.Repo first = repos.get(0);
        assertEquals("me/cool-mod", first.fullName());
        assertEquals("me", first.owner());
        assertTrue(first.isPrivate());
        GithubRepos.Repo other = repos.get(1);
        assertTrue(other.fork());
        assertTrue(other.archived());
        assertNull(other.description());
    }

    @Test
    void modIsRecognizedByGradleProperty() {
        assertTrue(GithubRepos.definesModProperty("mod_id=x\nminecraft_version=1.21.1\n"));
        assertTrue(GithubRepos.definesModProperty("minecraft_version = 26.1"));
        assertFalse(GithubRepos.definesModProperty("# minecraft_version=1.21\norg.gradle.jvmargs=-Xmx2G"));
        assertFalse(GithubRepos.definesModProperty(""));
    }

    @Test
    void followsPagination() {
        assertEquals("https://api.github.com/user/repos?page=2",
                GithubRepos.next("<https://api.github.com/user/repos?page=2>; rel=\"next\", <https://x?page=5>; rel=\"last\""));
        assertNull(GithubRepos.next("<https://x?page=1>; rel=\"prev\""));
        assertNull(GithubRepos.next(null));
    }

    @Test
    void tokenPrefersEnvFile() {
        assertEquals("a", GithubRepos.token(Map.of("GITHUB_TOKEN", " a "), Map.of("GITHUB_TOKEN", "b")));
        assertEquals("b", GithubRepos.token(Map.of(), Map.of("GITHUB_TOKEN", "b")));
        assertEquals("c", GithubRepos.token(Map.of(), Map.of("GH_TOKEN", "c")));
        assertEquals("", GithubRepos.token(Map.of(), Map.of()));
    }
}
