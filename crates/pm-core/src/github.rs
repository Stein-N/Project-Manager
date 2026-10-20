//! Lists every repository the signed-in GitHub account can see – its own, those of its organizations and the ones it
//! collaborates on – and recognizes which of them are Minecraft mods: their `gradle.properties` has
//! [`MOD_PROPERTY`].

use crate::http;
use crate::util::parse_properties;
use anyhow::{Result, bail};
use pm_types::GithubRepo;
use regex::Regex;
use serde_json::Value;
use std::collections::{BTreeMap, HashMap};
use std::sync::{LazyLock, Mutex};

/// Variable with the personal access token (.env in the master folder, then the system environment).
pub const VARIABLE: &str = "GITHUB_TOKEN";
/// A repository whose gradle.properties defines this is a Minecraft mod.
pub const MOD_PROPERTY: &str = "minecraft_version";
const MAX_PAGES: usize = 50;
const THREADS: usize = 8;

static NEXT: LazyLock<Regex> = LazyLock::new(|| Regex::new(r#"<([^>]+)>;\s*rel="next""#).unwrap());
/// Answers per repository and last push, so a reload only checks what changed.
static MOD_CACHE: LazyLock<Mutex<HashMap<String, bool>>> = LazyLock::new(Default::default);

/// The token from `vars` (the .env), else the system environment; empty if there is none.
pub fn token(vars: &BTreeMap<String, String>, system: &BTreeMap<String, String>) -> String {
    [vars.get(VARIABLE), system.get(VARIABLE), system.get("GH_TOKEN")]
        .into_iter()
        .flatten()
        .map(|t| t.trim())
        .find(|t| !t.is_empty())
        .unwrap_or("")
        .to_string()
}

fn get(url: &str, token: &str, accept: &str) -> Result<http::Response> {
    http::get(url, &[("Accept", accept), ("Authorization", &format!("Bearer {}", token.trim())),
        ("X-GitHub-Api-Version", "2022-11-28")])
}

fn check(status: u16) -> Result<()> {
    match status {
        200 => Ok(()),
        401 => bail!("GitHub rejected the token (401)."),
        403 | 429 => bail!("GitHub refused the request ({status}) – rate limit reached?"),
        s => bail!("GitHub answered {s}"),
    }
}

/// All repositories of the account behind `token`, most recently pushed first.
pub fn fetch_all(token: &str) -> Result<Vec<GithubRepo>> {
    if token.trim().is_empty() {
        bail!("No GitHub token – set {VARIABLE} in the .env.");
    }
    let mut repos = Vec::new();
    let mut url = Some("https://api.github.com/user/repos?per_page=100&sort=pushed&affiliation=owner,collaborator,organization_member".to_string());
    for _ in 0..MAX_PAGES {
        let Some(u) = url else { break };
        let r = get(&u, token, "application/vnd.github+json")?;
        check(r.status)?;
        repos.extend(parse(&r.body)?);
        url = next(r.link.as_deref());
    }
    Ok(repos)
}

/// Checks the repositories' gradle.properties in parallel and reports each mod as soon as it is known.
/// `progress` gets (repositories checked so far, the repository if it is a mod).
pub fn detect_mods(token: &str, repos: &[GithubRepo], progress: &(dyn Fn(usize, Option<&GithubRepo>) + Sync)) -> Result<()> {
    let next_index = Mutex::new(0usize);
    let done = Mutex::new(0usize);
    let error: Mutex<Option<anyhow::Error>> = Mutex::new(None);
    std::thread::scope(|s| {
        for _ in 0..THREADS.min(repos.len().max(1)) {
            s.spawn(|| loop {
                let i = {
                    let mut n = next_index.lock().unwrap();
                    if *n >= repos.len() || error.lock().unwrap().is_some() {
                        return;
                    }
                    *n += 1;
                    *n - 1
                };
                match is_mod(token, &repos[i]) {
                    Ok(m) => {
                        let mut d = done.lock().unwrap();
                        *d += 1;
                        progress(*d, m.then_some(&repos[i]));
                    }
                    Err(e) => {
                        error.lock().unwrap().get_or_insert(e);
                        return;
                    }
                }
            });
        }
    });
    match error.into_inner().unwrap() {
        Some(e) => Err(e),
        None => Ok(()),
    }
}

fn is_mod(token: &str, repo: &GithubRepo) -> Result<bool> {
    let key = format!("{}@{}", repo.full_name, repo.pushed_at.as_deref().unwrap_or(""));
    if let Some(&cached) = MOD_CACHE.lock().unwrap().get(&key) {
        return Ok(cached);
    }
    let r = get(&format!("https://api.github.com/repos/{}/contents/gradle.properties", repo.full_name), token,
        "application/vnd.github.raw+json")?;
    let m = match r.status {
        200 => defines_mod_property(&r.body),
        404 => false, // no gradle.properties (or an empty repository)
        s => {
            check(s)?;
            false
        }
    };
    MOD_CACHE.lock().unwrap().insert(key, m);
    Ok(m)
}

/// Whether the gradle.properties text defines [`MOD_PROPERTY`].
pub fn defines_mod_property(text: &str) -> bool {
    parse_properties(text).contains_key(MOD_PROPERTY)
}

pub fn next(link_header: Option<&str>) -> Option<String> {
    NEXT.captures(link_header?).map(|m| m[1].to_string())
}

pub fn parse(json: &str) -> Result<Vec<GithubRepo>> {
    let list: Value = serde_json::from_str(json).map_err(|e| anyhow::anyhow!("Unexpected answer from GitHub: {e}"))?;
    let Some(items) = list.as_array() else { bail!("Unexpected answer from GitHub") };
    let s = |v: &Value| v.as_str().map(str::to_string);
    Ok(items
        .iter()
        .map(|o| GithubRepo {
            full_name: s(&o["full_name"]).unwrap_or_default(),
            owner: s(&o["owner"]["login"]).unwrap_or_default(),
            name: s(&o["name"]).unwrap_or_default(),
            description: s(&o["description"]),
            clone_url: s(&o["clone_url"]).unwrap_or_default(),
            page_url: s(&o["html_url"]).unwrap_or_default(),
            private: o["private"].as_bool().unwrap_or(false),
            fork: o["fork"].as_bool().unwrap_or(false),
            archived: o["archived"].as_bool().unwrap_or(false),
            pushed_at: s(&o["pushed_at"]),
        })
        .collect())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_repos() {
        let repos = parse(r#"[{"name":"cool-mod","full_name":"me/cool-mod","description":"Adds things","private":true,"fork":false,
          "archived":false,"clone_url":"https://github.com/me/cool-mod.git","html_url":"https://github.com/me/cool-mod",
          "pushed_at":"2026-01-01T00:00:00Z","owner":{"login":"me"}},
         {"name":"website","full_name":"org/website","description":null,"private":false,"fork":true,
          "archived":true,"clone_url":"https://github.com/org/website.git","owner":{"login":"org"}}]"#).unwrap();
        assert_eq!(repos.len(), 2);
        assert_eq!(repos[0].full_name, "me/cool-mod");
        assert_eq!(repos[0].owner, "me");
        assert!(repos[0].private);
        assert!(repos[1].fork && repos[1].archived);
        assert_eq!(repos[1].description, None);
    }

    #[test]
    fn mod_is_recognized_by_gradle_property() {
        assert!(defines_mod_property("mod_id=x\nminecraft_version=1.21.1\n"));
        assert!(defines_mod_property("minecraft_version = 26.1"));
        assert!(!defines_mod_property("# minecraft_version=1.21\norg.gradle.jvmargs=-Xmx2G"));
        assert!(!defines_mod_property(""));
    }

    #[test]
    fn follows_pagination_and_prefers_env_file() {
        assert_eq!(next(Some(r#"<https://api.github.com/user/repos?page=2>; rel="next", <https://x?page=5>; rel="last""#)).as_deref(),
            Some("https://api.github.com/user/repos?page=2"));
        assert_eq!(next(Some(r#"<https://x?page=1>; rel="prev""#)), None);
        assert_eq!(next(None), None);
        let m = |k: &str, v: &str| -> BTreeMap<String, String> { [(k.to_string(), v.to_string())].into() };
        assert_eq!(token(&m(VARIABLE, " a "), &m(VARIABLE, "b")), "a");
        assert_eq!(token(&BTreeMap::new(), &m(VARIABLE, "b")), "b");
        assert_eq!(token(&BTreeMap::new(), &m("GH_TOKEN", "c")), "c");
        assert_eq!(token(&BTreeMap::new(), &BTreeMap::new()), "");
    }
}
