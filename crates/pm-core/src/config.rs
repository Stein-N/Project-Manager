//! Persistent settings in `~/.mc-project-manager/config.properties` – the same file and keys as the Java version,
//! so an existing setup is taken over unchanged.

use crate::util::{self, escape_property};
use pm_types::{CloseAction, Settings};
use std::collections::BTreeMap;
use std::fs;
use std::path::PathBuf;

/// Changelogs: Sonnet writes them as good as Opus for this task, faster and with less usage.
pub const DEFAULT_CLAUDE_MODEL: &str = "sonnet";
/// Backports are harder than changelogs: Opus by default.
pub const DEFAULT_BACKPORT_MODEL: &str = "claude-opus-5-5";

/// Folder of the settings and other per-user files.
pub fn dir() -> PathBuf {
    util::home().join(".mc-project-manager")
}

#[derive(Clone, Debug, Default)]
pub struct Config {
    props: BTreeMap<String, String>,
}

impl Config {
    pub fn load() -> Self {
        let file = dir().join("config.properties");
        Config { props: util::read_properties(&file) }
    }

    pub fn save(&self) -> std::io::Result<()> {
        fs::create_dir_all(dir())?;
        let mut text = String::from("#MC Mod Project Manager\n");
        for (k, v) in &self.props {
            text.push_str(&format!("{}={}\n", escape_property(k, true), escape_property(v, false)));
        }
        fs::write(dir().join("config.properties"), text)
    }

    fn string(&self, key: &str) -> String {
        self.props.get(key).map(|v| v.trim().to_string()).unwrap_or_default()
    }

    fn set_string(&mut self, key: &str, value: &str) {
        if value.trim().is_empty() {
            self.props.remove(key);
        } else {
            self.props.insert(key.into(), value.trim().into());
        }
    }

    fn path(&self, key: &str) -> Option<PathBuf> {
        Some(self.string(key)).filter(|s| !s.is_empty()).map(PathBuf::from)
    }

    fn bool(&self, key: &str, default: bool) -> bool {
        self.props.get(key).map(|v| v.trim().eq_ignore_ascii_case("true")).unwrap_or(default)
    }

    fn number<T: std::str::FromStr>(&self, key: &str, default: T) -> T {
        self.props.get(key).and_then(|v| v.trim().parse().ok()).unwrap_or(default)
    }

    pub fn master(&self) -> Option<PathBuf> {
        self.path("master")
    }

    pub fn set_master(&mut self, path: Option<PathBuf>) {
        self.set_string("master", &path.map(|p| util::absolute(&p).to_string_lossy().into_owned()).unwrap_or_default());
    }

    pub fn idea(&self) -> Option<PathBuf> {
        self.path("idea")
    }

    pub fn set_idea(&mut self, path: Option<PathBuf>) {
        self.set_string("idea", &path.map(|p| util::absolute(&p).to_string_lossy().into_owned()).unwrap_or_default());
    }

    pub fn git(&self) -> String {
        Some(self.string("git")).filter(|g| !g.is_empty()).unwrap_or_else(|| "git".into())
    }

    pub fn claude(&self) -> String {
        self.string("claude")
    }

    pub fn claude_model(&self) -> String {
        Some(self.string("claudeModel")).filter(|m| !m.is_empty()).unwrap_or_else(|| DEFAULT_CLAUDE_MODEL.into())
    }

    pub fn backport_model(&self) -> String {
        Some(self.string("backportModel")).filter(|m| !m.is_empty()).unwrap_or_else(|| DEFAULT_BACKPORT_MODEL.into())
    }

    pub fn claude_timeout_minutes(&self) -> u32 {
        self.number("claudeTimeoutMinutes", 15u32).max(1)
    }

    pub fn auto_fetch_minutes(&self) -> u32 {
        self.number("autoFetchMinutes", 10u32)
    }

    pub fn minimize_to_tray(&self) -> bool {
        self.bool("minimizeToTray", true)
    }

    pub fn close_action(&self) -> CloseAction {
        match self.string("closeAction").to_uppercase().as_str() {
            "MINIMIZE" => CloseAction::Minimize,
            "EXIT" => CloseAction::Exit,
            _ => CloseAction::Ask,
        }
    }

    pub fn set_close_action(&mut self, action: CloseAction) {
        let name = match action {
            CloseAction::Ask => "ASK",
            CloseAction::Minimize => "MINIMIZE",
            CloseAction::Exit => "EXIT",
        };
        self.props.insert("closeAction".into(), name.into());
    }

    pub fn autostart(&self) -> bool {
        self.bool("autostart", false)
    }

    pub fn set_autostart(&mut self, on: bool) {
        self.props.insert("autostart".into(), on.to_string());
    }

    pub fn check_updates(&self) -> bool {
        self.bool("checkUpdates", true)
    }

    pub fn skipped_version(&self) -> String {
        self.string("skippedVersion")
    }

    pub fn set_skipped_version(&mut self, version: &str) {
        self.set_string("skippedVersion", version);
    }

    pub fn claude_max_budget_usd(&self) -> f64 {
        self.number("claudeMaxBudgetUsd", 1.0f64).max(0.0)
    }

    pub fn backport_max_budget_usd(&self) -> f64 {
        self.number("backportMaxBudgetUsd", 5.0f64).max(0.0)
    }

    /// Commit types backported from `mod_name` into `target`; Some(empty) = all types, None = not configured.
    pub fn backport_types(&self, mod_name: &str, target: &str) -> Option<Vec<String>> {
        let value = self.props.get(&backport_key(mod_name, target))?;
        let v = value.trim();
        if v.is_empty() || v == "*" {
            return Some(Vec::new());
        }
        Some(v.split(',').map(str::trim).filter(|t| !t.is_empty()).map(str::to_string).collect())
    }

    /// `types` empty = all types.
    pub fn set_backport_types(&mut self, mod_name: &str, target: &str, types: &[String]) {
        let value = if types.is_empty() { "*".to_string() } else { types.join(",") };
        self.props.insert(backport_key(mod_name, target), value);
    }

    /// All settings at once (settings dialog).
    pub fn settings(&self) -> Settings {
        Settings {
            master: self.master(),
            idea: self.idea(),
            git: self.git(),
            auto_fetch_minutes: self.auto_fetch_minutes(),
            minimize_to_tray: self.minimize_to_tray(),
            close_action: self.close_action(),
            autostart: self.autostart(),
            check_updates: self.check_updates(),
            skipped_version: self.skipped_version(),
            claude: self.claude(),
            claude_model: self.claude_model(),
            backport_model: self.backport_model(),
            claude_max_budget_usd: self.claude_max_budget_usd(),
            backport_max_budget_usd: self.backport_max_budget_usd(),
            claude_timeout_minutes: self.claude_timeout_minutes(),
        }
    }

    pub fn apply(&mut self, s: &Settings) {
        self.set_master(s.master.clone());
        self.set_idea(s.idea.clone());
        let git = if s.git.trim() == "git" { "" } else { s.git.as_str() };
        self.set_string("git", git);
        self.props.insert("autoFetchMinutes".into(), s.auto_fetch_minutes.to_string());
        self.props.insert("minimizeToTray".into(), s.minimize_to_tray.to_string());
        self.set_close_action(s.close_action);
        self.set_autostart(s.autostart);
        self.props.insert("checkUpdates".into(), s.check_updates.to_string());
        self.set_skipped_version(&s.skipped_version);
        self.set_string("claude", &s.claude);
        let model = if s.claude_model.trim() == DEFAULT_CLAUDE_MODEL { "" } else { s.claude_model.as_str() };
        self.set_string("claudeModel", model);
        let model = if s.backport_model.trim() == DEFAULT_BACKPORT_MODEL { "" } else { s.backport_model.as_str() };
        self.set_string("backportModel", model);
        self.props.insert("claudeMaxBudgetUsd".into(), s.claude_max_budget_usd.max(0.0).to_string());
        self.props.insert("backportMaxBudgetUsd".into(), s.backport_max_budget_usd.max(0.0).to_string());
        self.props.insert("claudeTimeoutMinutes".into(), s.claude_timeout_minutes.max(1).to_string());
    }
}

fn backport_key(mod_name: &str, target: &str) -> String {
    let clean = |s: &str| -> String {
        s.chars().map(|c| if c.is_ascii_alphanumeric() || c == '_' || c == '.' || c == '-' { c } else { '_' }).collect()
    };
    format!("backport.types.{}.{}", clean(mod_name), clean(target))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn settings_round_trip_and_defaults() {
        let mut c = Config::default();
        assert_eq!(c.git(), "git");
        assert_eq!(c.auto_fetch_minutes(), 10);
        assert!(c.minimize_to_tray());
        assert_eq!(c.claude_model(), DEFAULT_CLAUDE_MODEL);
        let mut s = c.settings();
        s.auto_fetch_minutes = 0;
        s.close_action = CloseAction::Exit;
        s.claude_model = "opus".into();
        c.apply(&s);
        assert_eq!(c.settings(), s);
        // defaults are not written, so a later default change reaches the user
        assert!(!c.props.contains_key("git"));
    }

    #[test]
    fn backport_types() {
        let mut c = Config::default();
        assert_eq!(c.backport_types("My Mod", "1.20.1"), None);
        c.set_backport_types("My Mod", "1.20.1", &["fix".into()]);
        assert_eq!(c.backport_types("My Mod", "1.20.1"), Some(vec!["fix".to_string()]));
        c.set_backport_types("My Mod", "1.20.1", &[]);
        assert_eq!(c.backport_types("My Mod", "1.20.1"), Some(vec![]));
        assert!(c.props.contains_key("backport.types.My_Mod.1.20.1"));
    }
}
