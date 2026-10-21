# MC Mod Project Manager

Desktop tool for Minecraft mod projects in `<master>/<Minecraft version>/<project>`: git worktrees (one shared
repository per mod), Gradle tasks, dependency updates, changelogs and backports between versions – with Claude Code
for changelog drafts, backport conflicts and build errors. Runs on Windows and Linux (including Wayland).

## Structure

| Project | Content |
|---------|---------|
| `src/ProjectManager.Core` | All logic: projects, git, worktrees, Gradle, changelog, backport, Claude, updates |
| `src/ProjectManager` | The app ([Avalonia](https://avaloniaui.net/)): user interface in the IntelliJ New UI style, tray icon, autostart, single instance, log window |
| `tests/ProjectManager.Core.Tests` | Tests of the logic (xUnit; they need git on the PATH) |
| `packaging` | deb package, AppImage and Windows installer |

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
```

Debug builds can open a project tab or a dialog right at the start, to look at a view without clicking:
`PM_SELECT=<project folder> PM_TAB=Gradle` and `PM_DIALOG=settings` (also `clone`, `commit`, `cleanup`, `env`, `log` …).

## Releases

Raising `<Version>` in `Directory.Build.props` on `main` builds Windows and Linux on GitHub Actions and creates the
release (`.github/workflows/release.yml`). The program files `Project-Manager-windows-x64.exe` and
`Project-Manager-linux-x64`, the deb package and the AppImage are what the built-in updater downloads – the same file
names as before, so installations of the former Rust version update to this one.
