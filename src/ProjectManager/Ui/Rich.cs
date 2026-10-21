// Messages with a little markup, written like in the Rust version: <b>, <code>, <br>, <span class="warn|error|ok">.

using System.Net;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace ProjectManager.Ui;

public static partial class Rich
{
    [GeneratedRegex(@"<(/?)(b|code|br|span|i)(?:\s+class=""([^""]*)"")?\s*/?>")]
    private static partial Regex Tag();

    /// <summary>HTML-escapes text for messages with markup.</summary>
    public static string Html(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Selectable text from the markup.</summary>
    public static SelectableTextBlock Text(string html)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 19.5 };
        Fill(block.Inlines!, html);
        return block;
    }

    public static void Fill(InlineCollection inlines, string html)
    {
        var bold = 0;
        var code = 0;
        var italic = 0;
        var colors = new Stack<IBrush?>();
        var pos = 0;
        void Add(string text)
        {
            if (text.Length == 0) return;
            var run = new Run(WebUtility.HtmlDecode(text));
            if (bold > 0) run.FontWeight = FontWeight.SemiBold;
            if (italic > 0) run.FontStyle = FontStyle.Italic;
            if (code > 0)
            {
                run.FontFamily = Res.Mono;
                run.FontSize = 12;
            }
            if (colors.Count > 0 && colors.Peek() is { } brush) run.Foreground = brush;
            inlines.Add(run);
        }
        foreach (Match m in Tag().Matches(html))
        {
            Add(html[pos..m.Index]);
            pos = m.Index + m.Length;
            var closing = m.Groups[1].Value == "/";
            switch (m.Groups[2].Value)
            {
                case "br":
                    inlines.Add(new LineBreak());
                    break;
                case "b":
                    bold += closing ? -1 : 1;
                    break;
                case "i":
                    italic += closing ? -1 : 1;
                    break;
                case "code":
                    code += closing ? -1 : 1;
                    break;
                case "span":
                    if (closing)
                    {
                        if (colors.Count > 0) colors.Pop();
                    }
                    else
                    {
                        colors.Push(m.Groups[3].Value switch
                        {
                            "warn" => Res.Brush("Orange"),
                            "error" => Res.Brush("Red"),
                            "ok" => Res.Brush("Green"),
                            _ => colors.Count > 0 ? colors.Peek() : null,
                        });
                    }
                    break;
            }
        }
        Add(html[pos..]);
    }
}

/// <summary>Brushes and fonts of the theme (App.axaml).</summary>
public static class Res
{
    public static IBrush Brush(string key) =>
        Avalonia.Application.Current!.TryGetResource(key, null, out var value) && value is IBrush b ? b : Brushes.Black;

    public static FontFamily Mono =>
        Avalonia.Application.Current!.TryGetResource("Mono", null, out var value) && value is FontFamily f ? f : FontFamily.Default;
}
