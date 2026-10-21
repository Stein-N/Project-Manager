// Markdown like on GitHub (README, changelog preview, notes, release notes), parsed with Markdig and built from native
// controls: headings, paragraphs, lists and task lists, code, quotes, tables, images (also SVG badges from the web and
// animated GIFs) and the HTML READMEs often contain (<img>, <p align="center">, <br>, <details> spoilers as expanders).
// Links open in the browser, relative images and links resolve against the folder of the file.

using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;
using Avalonia.VisualTree;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;

namespace ProjectManager.Ui.Details;

public static partial class Md
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    /// <summary>A scrolling Markdown view; <paramref name="baseDir"/> is where relative paths point to.</summary>
    public static ScrollViewer View(Store s, string markdown, string? baseDir, bool small = false) => new()
    {
        Content = Document(s, markdown, baseDir, small),
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
    };

    /// <summary>The rendered Markdown without scrolling.</summary>
    public static Control Document(Store s, string markdown, string? baseDir, bool small = false)
    {
        var r = new Renderer(s, baseDir, small ? 12 : 14);
        try
        {
            var page = r.Blocks(Markdig.Markdown.Parse(markdown, Pipeline));
            r.FitTo(page);
            return page;
        }
        catch (Exception e)
        {
            // never an empty page: the text as it is
            Console.Error.WriteLine(e);
            return new SelectableTextBlock { Text = markdown, FontFamily = Res.Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        }
    }

    private sealed partial class Renderer(Store s, string? baseDir, double size)
    {
        private static IBrush Border => Res.Brush("BorderStrong");
        private static IBrush CodeBackground => Res.Brush("Panel");

        // ---------------------------------------------------------------- blocks

        private Control? _page;
        private readonly List<Action> _fits = [];

        /// <summary>Keeps the images no wider than the page (also the ones inside text, which get no width limit from the layout).</summary>
        public void FitTo(Control page)
        {
            _page = page;
            page.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.BoundsProperty) foreach (var fit in _fits) fit();
            };
        }

        public StackPanel Blocks(ContainerBlock container)
        {
            // <details> spoilers: the HTML of the tags and the blocks between them go into an expander
            var panels = new Stack<StackPanel>([new StackPanel { Spacing = size * 0.8 }]);
            foreach (var block in container)
            {
                if (block is HtmlBlock html && Text(html) is var source && DetailsTag().IsMatch(source))
                {
                    var at = 0;
                    foreach (Match tag in DetailsTag().Matches(source))
                    {
                        AddHtml(panels.Peek(), source[at..tag.Index]);
                        at = tag.Index + tag.Length;
                        if (tag.Groups["summary"].Success)
                        {
                            if (panels.Peek().Parent is Expander expander)
                                expander.Header = WebUtility.HtmlDecode(AnyTag().Replace(tag.Groups["summary"].Value, "")).Trim();
                        }
                        else if (tag.Value.StartsWith("</"))
                        {
                            if (panels.Count > 1) panels.Pop();
                        }
                        else
                        {
                            var content = new StackPanel { Spacing = size * 0.8 };
                            panels.Peek().Children.Add(new Expander
                            {
                                Header = "Details",
                                Content = content,
                                HorizontalAlignment = HorizontalAlignment.Stretch,
                                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                            });
                            panels.Push(content);
                        }
                    }
                    AddHtml(panels.Peek(), source[at..]);
                }
                else if (Block(block) is { } c) panels.Peek().Children.Add(c);
            }
            return panels.Last();
        }

        private void AddHtml(StackPanel panel, string html)
        {
            if (html.Trim().Length > 0 && Html(html) is { } c) panel.Children.Add(c);
        }

        private static string Text(LeafBlock block) => string.Join("\n", block.Lines.Lines.Take(block.Lines.Count).Select(l => l.ToString()));

        private Control? Block(Block block) => block switch
        {
            HeadingBlock h => Heading(h),
            ParagraphBlock p => Paragraph(p.Inline),
            ListBlock l => List(l),
            QuoteBlock q => new Border
            {
                BorderBrush = Border,
                BorderThickness = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(12, 0, 0, 0),
                Child = Blocks(q).Also(b => TextElement.SetForeground(b, Res.Brush("TextSecondary"))),
            },
            FencedCodeBlock or CodeBlock => Code((LeafBlock)block),
            ThematicBreakBlock => new Border { Height = 4, Background = Res.Brush("Border"), Margin = new Thickness(0, 6) },
            MdTable t => Table(t),
            HtmlBlock html => Html(Text(html)),
            LinkReferenceDefinitionGroup => null,
            ContainerBlock c => Blocks(c),
            LeafBlock { Inline: { } inline } => Paragraph(inline),
            _ => null,
        };

        private Control Heading(HeadingBlock h)
        {
            var factor = h.Level switch
            {
                1 => 1.85,
                2 => 1.45,
                3 => 1.2,
                4 => 1.0,
                5 => 0.9,
                _ => 0.85,
            };
            var text = Text(h.Inline, size * factor);
            text.FontWeight = FontWeight.SemiBold;
            if (h.Level > 2) return text.Margin(0, size * 0.6, 0, 0);
            return new Border
            {
                Child = text,
                BorderBrush = Res.Brush("Border"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 0, 0, size * 0.3),
                Margin = new Thickness(0, size * 0.6, 0, 0),
            };
        }

        /// <summary>A paragraph; one that only holds images (a row of badges, a header picture) keeps them side by side.</summary>
        private Control? Paragraph(ContainerInline? inline)
        {
            if (inline is null) return null;
            var items = inline.ToList();
            if (items.Count > 0 && items.All(i => i is LinkInline { IsImage: true } or LineBreakInline ||
                                                   (i is LinkInline { IsImage: false } l && l.Any() && l.All(x => x is LinkInline { IsImage: true })) ||
                                                   (i is LiteralInline lit && lit.Content.ToString().Trim().Length == 0)))
            {
                var row = new WrapPanel();
                foreach (var i in items)
                {
                    var (img, url) = i switch
                    {
                        LinkInline { IsImage: true } li => (li, (string?)null),
                        LinkInline link => ((LinkInline)link.First(), link.Url),
                        _ => (null, null),
                    };
                    if (img is null) continue;
                    var image = Image(img.Url, null, null);
                    image.Margin = new Thickness(0, 0, 4, 4);
                    row.Children.Add(url is null ? image : Clickable(image, url));
                }
                return row;
            }
            return Text(inline, size);
        }

        private Control List(ListBlock list)
        {
            var panel = new StackPanel { Spacing = list.IsLoose ? size * 0.5 : size * 0.15 };
            var number = int.TryParse(list.OrderedStart, out var n) ? n : 1;
            foreach (var item in list.OfType<ListItemBlock>())
            {
                var content = Blocks(item);
                content.Spacing = size * 0.4;
                Control marker;
                // a task list item: the checkbox replaces the bullet
                if (item.FirstOrDefault() is ParagraphBlock { Inline: { } first } && first.FirstChild is TaskList task)
                {
                    marker = new CheckBox
                    {
                        IsChecked = task.Checked, IsHitTestVisible = false, MinHeight = 0, Padding = new Thickness(0),
                        Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Top,
                    };
                }
                else
                {
                    marker = new TextBlock
                    {
                        Text = list.IsOrdered ? $"{number++}." : "•",
                        FontSize = size,
                        LineHeight = size * 1.5,
                        Margin = new Thickness(0, 0, 8, 0),
                        MinWidth = list.IsOrdered ? size * 1.4 : size * 0.8,
                        TextAlignment = TextAlignment.Right,
                    };
                }
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                grid.Children.Add(marker);
                Grid.SetColumn(content, 1);
                grid.Children.Add(content);
                panel.Children.Add(grid);
            }
            panel.Margin = new Thickness(size * 0.6, 0, 0, 0);
            return panel;
        }

        private Control Code(LeafBlock code)
        {
            var text = Text(code).TrimEnd();
            return new Border
            {
                Background = CodeBackground,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10),
                Child = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = new SelectableTextBlock { Text = text, FontFamily = Res.Mono, FontSize = size * 0.85 },
                },
            };
        }

        private Control Table(MdTable table)
        {
            var columns = Math.Max(1, table.ColumnDefinitions.Count);
            var grid = new Grid();
            for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var row = 0;
            foreach (var r in table.OfType<TableRow>())
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var next = 0;
                foreach (var cell in r.OfType<TableCell>())
                {
                    // Markdig leaves the index at -1 for cells it did not normalize
                    var column = Math.Clamp(cell.ColumnIndex >= 0 ? cell.ColumnIndex : next, 0, columns - 1);
                    next = column + Math.Max(1, cell.ColumnSpan);
                    var content = Blocks(cell);
                    if (r.IsHeader) TextElement.SetFontWeight(content, FontWeight.SemiBold);
                    content.HorizontalAlignment = table.ColumnDefinitions.ElementAtOrDefault(column)?.Alignment switch
                    {
                        TableColumnAlign.Center => HorizontalAlignment.Center,
                        TableColumnAlign.Right => HorizontalAlignment.Right,
                        _ => HorizontalAlignment.Left,
                    };
                    var border = new Border
                    {
                        BorderBrush = Border,
                        BorderThickness = new Thickness(column == 0 ? 1 : 0, row == 0 ? 1 : 0, 1, 1),
                        Padding = new Thickness(12, 5),
                        Background = !r.IsHeader && row % 2 == 0 ? CodeBackground : Brushes.Transparent,
                        Child = content,
                    };
                    Grid.SetRow(border, row);
                    Grid.SetColumn(border, column);
                    Grid.SetColumnSpan(border, Math.Max(1, cell.ColumnSpan));
                    grid.Children.Add(border);
                }
                row++;
            }
            return new ScrollViewer
            {
                Content = grid,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            };
        }

        // ---------------------------------------------------------------- HTML in READMEs

        [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase)]
        private static partial Regex ImgTag();

        [GeneratedRegex(@"\b(src|width|height|alt)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase)]
        private static partial Regex Attribute();

        [GeneratedRegex(@"<a\b[^>]*\bhref\s*=\s*[""']([^""']*)[""'][^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex Anchor();

        [GeneratedRegex(@"<br\s*/?>|</p>|</div>|</h\d>|</summary>|</li>", RegexOptions.IgnoreCase)]
        private static partial Regex Breaks();

        [GeneratedRegex(@"<[^>]+>")]
        private static partial Regex AnyTag();

        [GeneratedRegex(@"align\s*=\s*[""']?center", RegexOptions.IgnoreCase)]
        private static partial Regex Centered();

        [GeneratedRegex(@"<h([1-6])\b[^>]*>(.*?)</h\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex HtmlHeading();

        [GeneratedRegex(@"<details\b[^>]*>|</details\s*>|<summary\b[^>]*>(?<summary>.*?)</summary\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex DetailsTag();

        [GeneratedRegex(@"^<br\s*/?>$", RegexOptions.IgnoreCase)]
        private static partial Regex BreakTag();

        private static string AttributeValue(Match m) =>
            WebUtility.HtmlDecode(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value);

        /// <summary>An HTML block: its images (with their links and sizes) and its text; headings stay headings.</summary>
        private Control? Html(string html)
        {
            if (html.TrimStart().StartsWith("<!--")) return null;
            var center = Centered().IsMatch(html);
            var align = center ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            var panel = new StackPanel { Spacing = size * 0.4 };
            var heading = HtmlHeading().Match(html);
            if (heading.Success)
            {
                var level = int.Parse(heading.Groups[1].Value);
                var title = WebUtility.HtmlDecode(AnyTag().Replace(ImgTag().Replace(heading.Groups[2].Value, ""), "")).Trim();
                if (title.Length > 0)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = title,
                        FontSize = size * (level == 1 ? 1.85 : level == 2 ? 1.45 : 1.2),
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                        HorizontalAlignment = align,
                    });
                }
            }
            // images, the ones inside a link clickable
            var linked = new Dictionary<string, string>();
            foreach (Match a in Anchor().Matches(html))
            foreach (Match img in ImgTag().Matches(a.Groups[2].Value))
                linked[img.Value] = WebUtility.HtmlDecode(a.Groups[1].Value);
            var images = new WrapPanel { HorizontalAlignment = align };
            foreach (Match img in ImgTag().Matches(html))
            {
                var attrs = new Dictionary<string, string>();
                foreach (Match m in Attribute().Matches(img.Value)) attrs[m.Groups[1].Value.ToLowerInvariant()] = AttributeValue(m);
                if (!attrs.TryGetValue("src", out var src)) continue;
                var image = Image(src, Number(attrs.GetValueOrDefault("width")), Number(attrs.GetValueOrDefault("height")));
                image.Margin = new Thickness(2);
                images.Children.Add(linked.TryGetValue(img.Value, out var href) ? Clickable(image, href) : image);
            }
            if (images.Children.Count > 0) panel.Children.Add(images);
            var rest = HtmlHeading().Replace(html, "");
            var text = WebUtility.HtmlDecode(AnyTag().Replace(Breaks().Replace(ImgTag().Replace(rest, ""), "\n"), "")).Trim();
            if (text.Length > 0)
            {
                panel.Children.Add(new SelectableTextBlock
                {
                    Text = string.Join("\n", text.Split('\n').Select(l => l.Trim())),
                    FontSize = size,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = center ? TextAlignment.Center : TextAlignment.Left,
                });
            }
            return panel.Children.Count == 0 ? null : panel;
        }

        private static double? Number(string? value) =>
            value is not null && double.TryParse(value.Trim().TrimEnd('x', 'p'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

        // ---------------------------------------------------------------- inlines

        private SelectableTextBlock Text(ContainerInline? inline, double fontSize)
        {
            var block = new SelectableTextBlock { FontSize = fontSize, TextWrapping = TextWrapping.Wrap, LineHeight = fontSize * 1.5 };
            if (inline is not null) Inlines(block.Inlines!, inline, fontSize);
            return block;
        }

        private void Inlines(InlineCollection target, ContainerInline container, double fontSize)
        {
            foreach (var inline in container) Add(target, inline, fontSize);
        }

        private void Add(InlineCollection target, Markdig.Syntax.Inlines.Inline inline, double fontSize)
        {
            switch (inline)
            {
                case LiteralInline l:
                    target.Add(new Run(l.Content.ToString()));
                    break;
                case HtmlEntityInline e:
                    target.Add(new Run(e.Transcoded.ToString()));
                    break;
                case CodeInline c:
                    target.Add(new Run(c.Content) { FontFamily = Res.Mono, FontSize = fontSize * 0.88, Background = CodeBackground });
                    break;
                case LineBreakInline br:
                    target.Add(br.IsHard ? new LineBreak() : new Run(" "));
                    break;
                case TaskList:
                    break;
                case EmphasisInline em:
                {
                    var span = new Span();
                    if (em.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                    else if (em.DelimiterCount >= 2) span.FontWeight = FontWeight.SemiBold;
                    else span.FontStyle = FontStyle.Italic;
                    Inlines(span.Inlines, em, fontSize);
                    target.Add(span);
                    break;
                }
                case LinkInline { IsImage: true } img:
                    target.Add(new InlineUIContainer(Image(img.Url, null, null)) { BaselineAlignment = BaselineAlignment.TextBottom });
                    break;
                case LinkInline link:
                {
                    // links around images (badges) stay images, others are text links
                    if (link.Any() && link.All(x => x is LinkInline { IsImage: true }))
                    {
                        foreach (var x in link.OfType<LinkInline>())
                            target.Add(new InlineUIContainer(Clickable(Image(x.Url, null, null), link.Url)) { BaselineAlignment = BaselineAlignment.TextBottom });
                        break;
                    }
                    target.Add(Link(PlainText(link), link.Url, fontSize));
                    break;
                }
                case AutolinkInline auto:
                    target.Add(Link(auto.Url, auto.IsEmail ? $"mailto:{auto.Url}" : auto.Url, fontSize));
                    break;
                case HtmlInline html:
                {
                    var tag = html.Tag;
                    if (BreakTag().IsMatch(tag)) target.Add(new LineBreak());
                    else if (ImgTag().IsMatch(tag) &&
                             Attribute().Matches(tag).FirstOrDefault(m => m.Groups[1].Value.Equals("src", StringComparison.OrdinalIgnoreCase)) is { } src)
                        target.Add(new InlineUIContainer(Image(AttributeValue(src), null, null)) { BaselineAlignment = BaselineAlignment.TextBottom });
                    break;
                }
                case ContainerInline c:
                    Inlines(target, c, fontSize);
                    break;
            }
        }

        private Avalonia.Controls.Documents.Inline Link(string text, string? url, double fontSize)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                // the same line height as the paragraph, so the baselines match
                LineHeight = fontSize * 1.5,
                Foreground = Res.Brush("Link"),
                TextWrapping = TextWrapping.Wrap,
            };
            return new InlineUIContainer(Clickable(block, url)) { BaselineAlignment = BaselineAlignment.Baseline };
        }

        private static string PlainText(ContainerInline container) => string.Concat(container.Select(i => i switch
        {
            LiteralInline l => l.Content.ToString(),
            CodeInline c => c.Content,
            HtmlEntityInline e => e.Transcoded.ToString(),
            ContainerInline c => PlainText(c),
            LineBreakInline => " ",
            _ => "",
        }));

        // ---------------------------------------------------------------- images and links

        private Control Clickable(Control content, string? url)
        {
            if (string.IsNullOrEmpty(url)) return content;
            content.Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(content, url);
            content.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Left) Open(url);
            };
            return content;
        }

        /// <summary>Opens a link: web links in the browser, relative links as files; anchors are ignored.</summary>
        private void Open(string href)
        {
            if (href.Length == 0 || href.StartsWith('#')) return;
            if (!href.Contains("://") && !href.StartsWith("mailto:"))
            {
                if (baseDir is null) return;
                var file = Path.GetFullPath(Path.Combine(baseDir, Uri.UnescapeDataString(href.Split('?', '#')[0])));
                React.Spawn(() => Actions.OpenFolder(s, file));
                return;
            }
            React.Spawn(() => Actions.OpenUrl(s, href));
        }

        /// <summary>
        /// An image that appears when it is loaded: as large as the HTML says (width or height, the other one keeps the
        /// aspect ratio), else in its natural size – but never wider than the page.
        /// </summary>
        private Control Image(string? url, double? width, double? height)
        {
            var image = new Avalonia.Controls.Image { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both };
            if (string.IsNullOrWhiteSpace(url)) return image;
            React.Spawn(async () =>
            {
                if (await Images.Load(url, baseDir) is not { } source) return;
                image.Source = source;
                if (source is Images.Animation animation) animation.Play(image);
                var natural = source.Size;
                // without a size from the HTML: the natural size (an SVG badge has no pixels to fill the width with)
                if (width is null && height is null && natural.Width > 0) (width, height) = (natural.Width, natural.Height);
                if (height is { } h) image.MaxHeight = h;
                void Fit()
                {
                    var max = width ?? double.PositiveInfinity;
                    if (_page is { Bounds.Width: > 0 } page) max = Math.Min(max, page.Bounds.Width);
                    if (image.MaxWidth != max) image.MaxWidth = max;
                }
                Fit();
                _fits.Add(Fit);
            });
            return image;
        }
    }

    /// <summary>Images of Markdown documents: downloaded once per session (web) or read from files.</summary>
    private static class Images
    {
        private static readonly HttpClient Client = CreateClient();
        private static readonly ConcurrentDictionary<string, Task<byte[]?>> Downloads = new();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MC-Mod-Project-Manager");
            return client;
        }

        public static async Task<IImage?> Load(string url, string? baseDir)
        {
            byte[]? bytes;
            if (url.StartsWith("http://") || url.StartsWith("https://")) bytes = await Downloads.GetOrAdd(url, Download);
            else
            {
                if (baseDir is null) return null;
                var file = Path.GetFullPath(Path.Combine(baseDir, Uri.UnescapeDataString(url.Split('?', '#')[0])));
                bytes = File.Exists(file) ? await File.ReadAllBytesAsync(file) : null;
            }
            if (bytes is null || bytes.Length == 0) return null;
            try
            {
                if (IsSvg(bytes)) return new SvgImage { Source = SvgSource.LoadFromStream(new MemoryStream(bytes)) };
                if (IsGif(bytes) && await Task.Run(() => Animation.Decode(bytes)) is { } animation) return animation;
                return new Bitmap(new MemoryStream(bytes));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsSvg(byte[] bytes)
        {
            var head = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 512)).TrimStart();
            return head.StartsWith("<svg") || (head.StartsWith("<?xml") && head.Contains("<svg"));
        }

        private static bool IsGif(byte[] bytes) => bytes.Length > 6 && bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F';

        /// <summary>
        /// An animated GIF (Avalonia shows only the first frame): all frames decoded with Skia, shown one after the
        /// other while the image is on screen.
        /// </summary>
        public sealed class Animation : IImage
        {
            // larger animations stay a still picture, they would take too much memory
            private const long MaxBytes = 128L * 1024 * 1024;

            private readonly Bitmap[] _frames;
            private readonly TimeSpan[] _delays;

            private Animation(Bitmap[] frames, TimeSpan[] delays)
            {
                _frames = frames;
                _delays = delays;
            }

            public Size Size => _frames[0].Size;

            public void Draw(DrawingContext context, Rect sourceRect, Rect destRect) =>
                ((IImage)_frames[0]).Draw(context, sourceRect, destRect);

            /// <summary>The frames, or null for a still picture.</summary>
            public static Animation? Decode(byte[] bytes)
            {
                using var codec = SkiaSharp.SKCodec.Create(new MemoryStream(bytes));
                if (codec is null || codec.FrameCount < 2) return null;
                var info = new SkiaSharp.SKImageInfo(codec.Info.Width, codec.Info.Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                if ((long)info.BytesSize * codec.FrameCount > MaxBytes) return null;
                var pixels = new byte[codec.FrameCount][];
                var frames = new Bitmap[codec.FrameCount];
                var delays = new TimeSpan[codec.FrameCount];
                for (var i = 0; i < codec.FrameCount; i++)
                {
                    var frame = codec.FrameInfo[i];
                    // a frame draws over the one it requires
                    pixels[i] = frame.RequiredFrame >= 0 ? (byte[])pixels[frame.RequiredFrame].Clone() : new byte[info.BytesSize];
                    unsafe
                    {
                        fixed (byte* p = pixels[i])
                        {
                            codec.GetPixels(info, (IntPtr)p, new SkiaSharp.SKCodecOptions(i, frame.RequiredFrame));
                            frames[i] = new Bitmap(Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul, (IntPtr)p,
                                new PixelSize(info.Width, info.Height), new Vector(96, 96), info.RowBytes);
                        }
                    }
                    // browsers show frames without a delay (or a very short one) for 100 ms
                    delays[i] = TimeSpan.FromMilliseconds(frame.Duration <= 10 ? 100 : frame.Duration);
                }
                return new Animation(frames, delays);
            }

            /// <summary>Shows the frames in <paramref name="image"/> as long as it is in a window.</summary>
            public void Play(Avalonia.Controls.Image image)
            {
                var index = 0;
                var timer = new Avalonia.Threading.DispatcherTimer { Interval = _delays[0] };
                timer.Tick += (_, _) =>
                {
                    index = (index + 1) % _frames.Length;
                    image.Source = _frames[index];
                    timer.Interval = _delays[index];
                };
                image.AttachedToVisualTree += (_, _) => timer.Start();
                image.DetachedFromVisualTree += (_, _) => timer.Stop();
                if (image.IsAttachedToVisualTree()) timer.Start();
            }
        }

        private static async Task<byte[]?> Download(string url)
        {
            try
            {
                return await Client.GetByteArrayAsync(url);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
