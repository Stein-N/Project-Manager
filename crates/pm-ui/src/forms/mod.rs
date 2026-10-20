//! The dialogs with forms. Each `*_dialog` function opens its dialog and resolves to what the user entered.

mod cleanup;
mod clone;
mod commit;
mod env;
mod settings;
mod simple;
mod worktree;

pub use cleanup::cleanup_dialog;
pub use clone::clone_dialog;
pub use commit::commit_dialog;
pub use env::env_dialog;
pub use settings::settings_dialog;
pub use simple::{close_dialog, import_dialog, move_dialog, tag_dialog, types_dialog};
pub use worktree::worktree_dialog;
