// SVG icons (IntelliJ Platform icons, Apache 2.0 – see Assets/icons/NOTICE.txt), embedded into the program.

using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Svg.Skia;

namespace ProjectManager.Ui;

public static class Icons
{
    private static readonly Dictionary<string, SvgSource?> Cache = [];

    private static SvgSource? Source(string name)
    {
        if (Cache.TryGetValue(name, out var cached)) return cached;
        SvgSource? source = null;
        try
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri($"avares://project-manager/Assets/icons/{name}.svg"));
            source = SvgSource.LoadFromStream(stream);
        }
        catch (Exception)
        {
        }
        Cache[name] = source;
        return source;
    }

    /// <summary>A 16×16 icon (or smaller).</summary>
    public static Control Icon(string name, double size = 16)
    {
        var image = new Image { Width = size, Height = size, Stretch = Stretch.Uniform };
        if (Source(name) is { } source) image.Source = new SvgImage { Source = source };
        return image;
    }
}
