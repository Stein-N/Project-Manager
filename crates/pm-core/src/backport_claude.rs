//! Claude in the backport: resolves conflicts and build errors in the work worktree. The rules come from the backport
//! template, adapted to the local mode: nothing is committed, the tool applies the result as uncommitted changes. A
//! CLAUDE.md in the mod repository adds to them.

use crate::claude::Tools;
use pm_types::{Commit, Decision};
use regex::Regex;
use std::sync::LazyLock;

/// Git commands Claude may run; everything else (commit, checkout, reset, …) is denied.
const ALLOWED: [&str; 17] = [
    "Read",
    "Edit",
    "Write",
    "Glob",
    "Grep",
    "Bash(git status:*)",
    "Bash(git diff:*)",
    "Bash(git log:*)",
    "Bash(git show:*)",
    "Bash(git ls-files:*)",
    "Bash(git grep:*)",
    "Bash(git ls-tree:*)",
    "Bash(git cat-file:*)",
    "Bash(git blame:*)",
    "Bash(git rm:*)",
    "Bash(./gradlew build:*)",
    "Bash(./gradlew compileJava:*)",
];
/// Denied explicitly as well, so a broader allow rule can never let them through.
const DENIED: [&str; 23] = [
    "Bash(git commit:*)",
    "Bash(git push:*)",
    "Bash(git checkout:*)",
    "Bash(git switch:*)",
    "Bash(git branch:*)",
    "Bash(git reset:*)",
    "Bash(git rebase:*)",
    "Bash(git worktree:*)",
    "Bash(git cherry-pick:*)",
    "Bash(git merge:*)",
    "Bash(git stash:*)",
    "Bash(git tag:*)",
    "Bash(git restore:*)",
    "Bash(git clean:*)",
    "Bash(git update-ref:*)",
    "Bash(git config:*)",
    "Bash(git remote:*)",
    "Bash(git fetch:*)",
    "Bash(git pull:*)",
    "Bash(git add:*)",
    "Bash(rm:*)",
    "WebFetch",
    "WebSearch",
];

static DECISION: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?im)^\s*\**DECISION:?\**\s*:?\s*`?(backport|skip)`?\s*$").unwrap());

/// Read/edit files in the work folder, a few read-only git commands, `git rm` and the build; nothing is asked, what
/// is not allowed is denied. `--restricted` keeps the user's settings and hooks out and confines the file tools to
/// the work folder.
pub fn tools() -> Tools {
    Tools {
        available: ["Read", "Edit", "Write", "Glob", "Grep", "Bash"].map(String::from).to_vec(),
        allowed: ALLOWED.map(String::from).to_vec(),
        disallowed: DENIED.map(String::from).to_vec(),
        permission_mode: Some("dontAsk".into()),
        restricted: true,
    }
}

/// `claude_md`: CLAUDE.md of the mod repository (target).
#[derive(Clone, Debug)]
pub struct Context {
    pub mod_name: String,
    pub loader: Option<String>,
    pub source_name: String,
    pub source_mc: String,
    pub target_name: String,
    pub target_mc: String,
    pub claude_md: Option<String>,
}

pub const RULES: &str = "Rules for backports:
- Keep the BEHAVIOR of the original commit, not its exact wording. Adapt the code to the Minecraft and
  loader APIs of the target version (look at how the target's code does similar things).
- Typical differences between versions (examples, check the actual code):
  NeoForge `net.neoforged.*` vs Forge `net.minecraftforge.*`; `NeoForge.EVENT_BUS` vs `MinecraftForge.EVENT_BUS`;
  `ResourceLocation.fromNamespaceAndPath(ns, path)` vs `new ResourceLocation(ns, path)`;
  data components (`stack.get(DataComponents.…)`) vs NBT (`stack.getTag()`);
  `CustomPacketPayload` + `StreamCodec` vs `SimpleChannel`; `neoforge.mods.toml` vs `mods.toml`.
- Never change: gradle.properties, mods.toml, neoforge.mods.toml, anything under src/generated/ or .github/,
  CHANGELOG.md, CHANGELOG_FULL.md. Versions, dependency ranges, datagen output and changelogs are handled by
  the tool. Do not add dependencies.
- Do not delete or disable tests to make something compile.
- A file the target does not have: move the change to where the equivalent code lives in the target, or
  remove the file with `git rm <path>` if the change does not apply to the target.
- If there is no reasonable equivalent in the target version, do not guess: answer with DECISION: skip and
  the reason.
- Do NOT commit, do not stage, do not switch branches, do not push. Leave all changes in the working tree.
  Commands that are not allowed are denied automatically – do not look for workarounds.
";

const ANSWER: &str = "When you are done, answer with exactly this format (the notes in German):
DECISION: backport      (or: DECISION: skip)

### Was wurde angepasst
- `<Datei/Klasse>`: <Änderung und Grund>

### Bitte prüfen
- <Stellen, bei denen du unsicher bist, oder \"nichts Besonderes\">

For DECISION: skip only write the reason below the decision line.
";

/// Prompt for a commit whose cherry-pick stopped with conflicts.
pub fn conflict_prompt(ctx: &Context, commit: &Commit, commit_diff: &str, conflicts: &[String]) -> String {
    let mut sb = intro(ctx);
    sb.push_str("The commit below was applied with `git cherry-pick --no-commit` and stopped with conflicts in:\n");
    for f in conflicts {
        sb.push_str(&format!("- {f}\n"));
    }
    sb.push_str(&format!(
        "\nTask: resolve these conflicts so that the result does what the original commit does, adapted to the
target version. No conflict markers may be left. Check related code of the target (e.g. callers,
other loaders) so the result compiles. `git status`, `git diff` and `git show` are available; the source
version is the branch \"{}\".

",
        ctx.source_name
    ));
    sb.push_str(RULES);
    sb.push('\n');
    append_claude_md(&mut sb, ctx);
    let _ = commit;
    sb.push_str(&format!("Original commit:\n<commit>\n{}\n</commit>\n\n", commit_diff.trim()));
    sb.push_str(ANSWER);
    sb
}

/// Prompt for a failed `gradlew build` after the backport.
pub fn build_prompt(ctx: &Context, ported: &[Commit], build_output: &str) -> String {
    let mut sb = intro(ctx);
    sb.push_str("These commits were backported into the working tree (not committed):\n");
    for c in ported {
        sb.push_str(&format!("- {} {}\n", c.short_sha, c.message.subject));
    }
    sb.push_str(
        "
`./gradlew build` fails afterwards. Task: fix the errors that come from the backport, so the build passes
again – adapt the backported code to the target version, do not change unrelated code. You may run
`./gradlew build` or `./gradlew compileJava` yourself to check. If an error cannot be fixed without
guessing, answer with DECISION: skip and explain it.

",
    );
    sb.push_str(RULES);
    sb.push('\n');
    append_claude_md(&mut sb, ctx);
    sb.push_str(&format!("Build output (end):\n<build>\n{}\n</build>\n\n", build_output.trim()));
    sb.push_str(ANSWER);
    sb
}

fn intro(ctx: &Context) -> String {
    format!(
        "You are backporting changes of the Minecraft mod \"{}\"{} from {} (Minecraft {}) to {} (Minecraft {}).\n\
         The current folder is a git worktree of the target with a detached HEAD; it is a scratch copy, the user \
         reviews the result before it is used.\n\n",
        ctx.mod_name,
        ctx.loader.as_ref().filter(|l| !l.trim().is_empty()).map(|l| format!(" ({l})")).unwrap_or_default(),
        ctx.source_name,
        ctx.source_mc,
        ctx.target_name,
        ctx.target_mc
    )
}

fn append_claude_md(sb: &mut String, ctx: &Context) {
    if let Some(md) = ctx.claude_md.as_ref().filter(|m| !m.trim().is_empty()) {
        sb.push_str(
            "Project instructions (CLAUDE.md of the mod; they take precedence, but ignore steps about \
            committing, cherry-pick -x, pull requests or .backport files – this run is local and the tool does that \
            part):\n<claude-md>\n",
        );
        sb.push_str(md.trim());
        sb.push_str("\n</claude-md>\n\n");
    }
}

/// The decision and the answer without the decision line.
pub fn parse(text: &str) -> (Decision, String) {
    let t = text.replace("\r\n", "\n");
    let t = t.trim();
    let Some(m) = DECISION.captures(t) else { return (Decision::Unknown, t.to_string()) };
    let whole = m.get(0).unwrap();
    let d = if m[1].eq_ignore_ascii_case("skip") { Decision::Skip } else { Decision::Backport };
    (d, format!("{}{}", &t[..whole.start()], &t[whole.end()..]).trim().to_string())
}

/// End of a long build log: errors are at the end, Claude does not need the whole configuration output.
pub fn tail(lines: &[String], max_lines: usize) -> String {
    lines[lines.len().saturating_sub(max_lines)..].join("\n")
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::claude;
    use crate::commits::commit;
    use std::path::Path;

    fn ctx() -> Context {
        Context {
            mod_name: "Resource Library".into(),
            loader: Some("NeoForge".into()),
            source_name: "main".into(),
            source_mc: "26.3".into(),
            target_name: "1.21.1".into(),
            target_mc: "1.21.1".into(),
            claude_md: Some("## Build\nUse Java 21.".into()),
        }
    }

    #[test]
    fn parses_decision_and_notes() {
        let (d, notes) = parse("DECISION: backport\n\n### Was wurde angepasst\n- `A`: x\n");
        assert_eq!(d, Decision::Backport);
        assert_eq!(notes, "### Was wurde angepasst\n- `A`: x");
        let (d, notes) = parse("Some text\n**DECISION:** `skip`\nNo API in 1.21.1");
        assert_eq!(d, Decision::Skip);
        assert_eq!(notes, "Some text\n\nNo API in 1.21.1");
        assert_eq!(parse("I fixed it.").0, Decision::Unknown);
    }

    #[test]
    fn conflict_prompt_contains_task_rules_and_commit() {
        let c = commit("86835d5aaa", "86835d5", "t", "d", false, "fix ForgeResourcePackRegistry");
        let p =
            conflict_prompt(&ctx(), &c, "commit 86835d5\n+code", &["forge/A.java".into(), "gradle.properties".into()]);
        assert!(p.starts_with("You are backporting changes of the Minecraft mod \"Resource Library\" (NeoForge) from main (Minecraft 26.3) to 1.21.1"));
        assert!(p.contains("- forge/A.java\n- gradle.properties\n"));
        assert!(p.contains(RULES));
        assert!(p.contains("<claude-md>\n## Build\nUse Java 21.\n</claude-md>"));
        assert!(p.contains("<commit>\ncommit 86835d5\n+code\n</commit>"));
        assert!(p.contains("DECISION: backport"));
    }

    #[test]
    fn build_prompt_contains_commits_and_output_tail() {
        let c = commit("aaaa", "aaaa", "t", "d", false, "feat: thing");
        let log: Vec<String> = (0..500).map(|i| format!("line {i}")).collect();
        let p = build_prompt(&ctx(), &[c], &tail(&log, 3));
        assert!(p.contains("- aaaa thing\n"));
        assert!(p.contains("<build>\nline 497\nline 498\nline 499\n</build>"));
        assert!(!p.contains("line 496"));
    }

    #[test]
    fn tools_are_restricted() {
        let r = claude::Request {
            work_dir: ".".into(),
            prompt: "p".into(),
            tools: tools(),
            model: None,
            max_budget_usd: 0.0,
            timeout: None,
            tokens: Vec::new(),
        };
        let cmd = claude::command(Path::new("claude"), &r, Path::new("m"), false);
        let after = |flag: &str| cmd[cmd.iter().position(|a| a == flag).unwrap() + 1].clone();
        assert!(cmd.iter().any(|a| a == "--restricted"));
        assert_eq!(after("--permission-mode"), "dontAsk");
        assert_eq!(after("--permission-prompts"), "none");
        let allowed = after("--allowedTools");
        let denied = after("--disallowedTools");
        assert!(allowed.contains("Bash(git rm:*)") && allowed.contains("Bash(./gradlew build:*)"));
        assert!(!(allowed.contains("git commit") || allowed.contains("git checkout") || allowed.contains("git add")));
        assert!(
            denied.contains("Bash(git commit:*)")
                && denied.contains("Bash(git push:*)")
                && denied.contains("Bash(rm:*)")
        );
    }
}
