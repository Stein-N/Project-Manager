# MC Mod Project Manager

Desktop tool for Minecraft mod projects in `<master>/<Minecraft version>/<project>`: git worktrees (one shared
repository per mod), Gradle tasks, dependency updates, changelogs and backports between versions – with Claude Code
for changelog drafts, backport conflicts and build errors. Runs on Windows and Linux (including Wayland).

## Structure

| Crate | Content |
|-------|---------|
| `crates/pm-types` | Data types shared by the logic and the UI (also compiled to WebAssembly) |
| `crates/pm-core` | All logic: projects, git, worktrees, Gradle, changelog, backport, Claude, updates – with tests |
| `crates/pm-app` | The Tauri app: commands for the UI, tray icon, autostart, single instance, log window |
| `crates/pm-ui` | The user interface in Rust (Leptos), styled after the IntelliJ New UI |

Settings are stored in `~/.mc-project-manager/config.properties` (the same file the former Java version used).

## Development

Requirements: Rust (stable) with the `wasm32-unknown-unknown` target, `trunk` and the Tauri CLI:

```sh
rustup target add wasm32-unknown-unknown
cargo install --locked trunk tauri-cli@^2
```

On Linux additionally the Tauri system packages, e.g. on Ubuntu:

```sh
sudo apt install build-essential pkg-config libwebkit2gtk-4.1-dev libssl-dev libxdo-dev libayatana-appindicator3-dev librsvg2-dev
```

```sh
cargo test -p pm-core          # logic tests (need git on the PATH)
cd crates/pm-app
cargo tauri dev                # app with live-reloading UI
cargo tauri build              # release build and installers (target/release/bundle)
```

## Releases

Raising `version` in `[workspace.package]` of `Cargo.toml` on `main` builds Windows and Linux on GitHub Actions and
creates the release (`.github/workflows/release.yml`). The program files `Project-Manager-windows-x64.exe` and
`Project-Manager-linux-x64` are what the built-in updater downloads.
