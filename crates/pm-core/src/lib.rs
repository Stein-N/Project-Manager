//! Logic of the MC Mod Project Manager. Everything here is UI independent: the Tauri app calls it from its commands,
//! the tests call it directly.

pub mod autostart_flag;
pub mod backport;
pub mod backport_claude;
pub mod backport_session;
pub mod changelog;
pub mod changes;
pub mod claude;
pub mod cleanup;
pub mod commits;
pub mod config;
pub mod dependencies;
pub mod env_file;
pub mod git;
pub mod github;
pub mod gradle;
pub mod http;
pub mod metadata;
pub mod mod_files;
pub mod proc;
pub mod readme;
pub mod scanner;
pub mod system;
pub mod tags;
pub mod update;
pub mod util;
pub mod versions;
pub mod worktrees;
