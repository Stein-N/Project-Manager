# C# example (Avalonia)

A small example of the manager in C# with [Avalonia](https://avaloniaui.net/), to compare it with the Tauri version:

- main window in the IntelliJ style: Minecraft versions → projects with git branch and changes, details on the right
- the master folder comes from the same settings file (`~/.mc-project-manager/config.properties`)
- system tray icon with a menu (Show, Rescan projects, Exit); the close button hides the window to the tray

## Build and run

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
cd csharp/ProjectManager
dotnet run
```

Single program file, no .NET installation needed on the target machine:

```sh
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Linux: the tray icon is a StatusNotifierItem (KDE, XFCE, Cinnamon show it; GNOME needs the AppIndicator extension).
