// Folder and file pickers of the system (the Tauri dialog plugin).

using Avalonia.Platform.Storage;

namespace ProjectManager.Ui;

public static class Pickers
{
    private static IStorageProvider? Provider =>
        App.Current.TopLevel()?.StorageProvider;

    public static async Task<string?> Folder(string title, string? start)
    {
        if (Provider is not { } provider) return null;
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (start is not null && Directory.Exists(start)) options.SuggestedStartLocation = await provider.TryGetFolderFromPathAsync(start);
        var result = await provider.OpenFolderPickerAsync(options);
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public static async Task<string?> File(string title)
    {
        if (Provider is not { } provider) return null;
        var result = await provider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }
}
