# MC Mod Project Manager

Desktop tool for Minecraft mod projects in `<master>/<Minecraft version>/<project>`: git worktrees (one shared
repository per mod), changelogs (drafts by Claude Code) and a cleanup of the Gradle caches. Runs on Windows and Linux
(including Wayland).

## Structure

| Project | Content |
|---------|---------|
| `src/ProjectManager.Core` | All logic: projects, git, worktrees, changelog, Gradle cache cleanup, Claude, updates |
| `src/ProjectManager` | The app ([Avalonia](https://avaloniaui.net/)): user interface in the IntelliJ New UI style, tray icon, autostart, single instance, log window |
| `tests/ProjectManager.Core.Tests` | Tests of the logic (xUnit; they need git on the PATH) |
| `packaging` | deb package, AppImage, tar.gz, Flatpak and Windows installer |

Settings are stored in `~/.mc-project-manager/config.properties` (the same file the former Java and Rust versions
used, so an existing setup is taken over).

## Development

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download) and git.

```sh
dotnet test                                   # logic tests
dotnet run --project src/ProjectManager       # the app
```

Release build of one self-contained program file (no .NET installation needed on the target):

```sh
dotnet publish src/ProjectManager -r linux-x64 -c Release --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish    # or -r win-x64
packaging/linux/build-deb.sh publish/project-manager <version> dist
packaging/linux/build-appimage.sh publish/project-manager <version> dist
packaging/linux/build-tarball.sh publish/project-manager <version> dist
```

Debug builds can open a project tab or a dialog right at the start, to look at a view without clicking:
`PM_SELECT=<project folder> PM_TAB=Changelog` and `PM_DIALOG=settings` (also `clone`, `commit`, `cleanup`, `env`, `log` …);
`PM_NO_SINGLE_INSTANCE=1` runs it beside an already running instance.

## Releases

Raising `<Version>` in `Directory.Build.props` on `main` builds Windows and Linux on GitHub Actions and creates the
release (`.github/workflows/release.yml`). The program files `Project-Manager-windows-x64.exe` and
`Project-Manager-linux-x64`, the deb package and the AppImage are what the built-in updater downloads – the same file
names as before, so installations of the former Rust version update to this one.

The tar.gz archive is for distributions without dpkg (Arch, Fedora …). It is laid out like an install prefix, so it is
unpacked straight into one: `tar -xzf <archive> -C ~/.local` (updates without a password) or
`sudo tar -xzf <archive> -C /usr/local` (updates ask for the password via pkexec). The updater then replaces only
`bin/project-manager` with `Project-Manager-linux-x64`.

### Flatpak

```sh
flatpak install --user https://stein-n.github.io/Project-Manager/project-manager.flatpakref
```

The Flatpak repository lives on GitHub Pages; every release replaces it with the new version (the release also has the
bundle `…_x86_64.flatpak`, which adds the repository for updates as well). Updates come with `flatpak update`, the
software center or the update dialog of the app (it runs `flatpak update` on the host and restarts).

The sandbox is open on purpose: git, Gradle, Claude, IntelliJ, the terminal and links run on the host through
`flatpak-spawn --host` (`src/ProjectManager.Core/Host.cs`), with the PATH of a login shell, and the app reads and
writes project folders anywhere. Settings stay in `~/.mc-project-manager`, so switching from another package keeps them.

Build it locally (needs `flatpak-builder` and `org.freedesktop.Platform`/`Sdk` 25.08 from Flathub):
`packaging/flatpak/build-flatpak.sh publish/project-manager <version> site dist`.

One-time setup for the release workflow:

1. A signing key without a passphrase, stored as the repository secret `FLATPAK_GPG_PRIVATE_KEY`:
   ```sh
   gpg --batch --passphrase '' --quick-gen-key "MC Mod Project Manager Flatpak" ed25519 sign never
   gpg --armor --export-secret-keys "MC Mod Project Manager Flatpak"   # → the secret
   ```
   Keep a backup: installations only accept updates signed with this key.
2. Settings → Pages → Build and deployment → Source: **GitHub Actions**.

Without the secret the workflow builds no Flatpak and the release comes without it.
