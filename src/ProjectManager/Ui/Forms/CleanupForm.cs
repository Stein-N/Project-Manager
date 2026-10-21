// Gradle caches and build output with sizes; data no project uses is marked and preselected.

using Avalonia.Controls;
using Avalonia.Layout;
using ProjectManager.Core;
using static ProjectManager.Ui.Rich;

namespace ProjectManager.Ui.Forms;

public static class CleanupForm
{
    private sealed record Item(CacheItem Cache, Var<bool> Checked, Var<long?> Size);

    public static Task Show(Store s) => s.Dialogs.Open<bool>("Clean up Gradle caches", 900, 560, done =>
    {
        var items = new Var<List<Item>>([]);
        var home = new Var<string>("");
        var unusedOnly = new Var<bool>(false);
        var message = new Var<string?>(null);
        var open = true;
        var generation = 0;
        var changed = new Var<int>(0);
        void Scan()
        {
            var run = ++generation;
            message.Value = "Scanning…";
            React.Spawn(async () =>
            {
                try
                {
                    var (h, list) = await s.Api.CleanupScan();
                    home.Value = h;
                    var rows = list.Select(i => new Item(i, new Var<bool>(i.Status == CacheStatus.Unused), new Var<long?>(null))).ToList();
                    foreach (var r in rows)
                    {
                        r.Checked.Changed += () => changed.Value++;
                        r.Size.Changed += () => changed.Value++;
                    }
                    items.Value = rows;
                    message.Value = null;
                    // sizes one by one, filled in as they arrive
                    foreach (var r in rows)
                    {
                        if (!open || generation != run) return;
                        try
                        {
                            r.Size.Value = await s.Api.DirSize(r.Cache.Path);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
                catch (Exception e)
                {
                    message.Value = $"Error: {e.Message}";
                }
            });
        }
        Scan();
        string Summary()
        {
            if (message.Value is { } m) return m;
            var list = items.Value;
            var pending = list.Count(i => i.Size.Value is null);
            var selected = list.Where(i => i.Checked.Value).ToList();
            var selectedSize = selected.Sum(i => i.Size.Value ?? 0);
            var unused = list.Where(i => i.Cache.Status == CacheStatus.Unused).Sum(i => i.Size.Value ?? 0);
            return $"Selected: {selected.Count} item(s), {Util.FormatSize(selectedSize)}   ·   Unused total: {Util.FormatSize(unused)}" +
                   (pending > 0 ? $"   ·   calculating sizes ({pending} left)…" : "");
        }
        void Delete() => React.Spawn(async () =>
        {
            var chosen = items.Value.Where(i => i.Checked.Value).ToList();
            if (chosen.Count == 0) return;
            var total = chosen.Sum(i => i.Size.Value ?? 0);
            var used = chosen.Count(i => i.Cache.Status == CacheStatus.Used);
            var warning = used > 0
                ? $"<br><br><span class=\"warn\">{used} selected item(s) are used by projects and will be downloaded or rebuilt on their next build.</span>"
                : "";
            if (!await s.Dialogs.Confirm("Delete", $"Delete {chosen.Count} item(s) ({Util.FormatSize(total)})?{warning}", "Delete")) return;
            generation++;
            message.Value = "Deleting…";
            try
            {
                var failed = await s.Api.CleanupDelete(chosen.Select(i => i.Cache.Path).ToList());
                if (failed.Count > 0)
                {
                    await s.Dialogs.Message("Clean up",
                        "Some items could not be deleted (files in use – stop the Gradle daemons and close IntelliJ, then try again):<br><br>" +
                        string.Join("<br>", failed.Take(8).Select(Html)));
                }
            }
            catch (Exception)
            {
            }
            Scan();
        });
        void Stop() => React.Spawn(async () =>
        {
            var versions = await s.Bg("Stopping Gradle daemons…", s.Api.StopDaemons());
            if (versions.Ok) message.Value = $"Gradle daemons stopped ({string.Join(", ", versions.Value)}).";
        });

        var table = new W.Table("Auto,*,2*,Auto,2*", ["", "Category", "Item", "Size", "Status"]);
        table.View.Watch(() => table.SetRows(items.Value.Where(i => !unusedOnly.Value || i.Cache.Status == CacheStatus.Unused).Select(i =>
        {
            var (status, cls) = i.Cache.Status switch
            {
                CacheStatus.Unused => ("Unused", "error"),
                CacheStatus.Used => ($"Used by {i.Cache.UsedBy ?? ""}", "ok"),
                CacheStatus.Shared => ("Shared – re-downloaded when needed", "muted"),
                _ => ("Build output – rebuilt by the next build", "muted"),
            };
            var size = W.Text(() => i.Size.Value is { } b ? Util.FormatSize(b) : "…", [i.Size]);
            size.HorizontalAlignment = HorizontalAlignment.Right;
            return (Control)table.Row(W.Check("", i.Checked), W.Text(i.Cache.Category), W.Text(i.Cache.Name), size, W.Text(status, cls))
                .Tip(i.Cache.Path).ToggleOnClick(i.Checked);
        })), items, unusedOnly);
        var intro = Rich.Text("<b>Unused</b> = not used by any project in the master folder (Gradle version or Minecraft version). " +
                              "Other projects on this PC may still need them – deleting only means they are downloaded again.");
        intro.FontSize = 12;
        var body = W.Rows(8,
            (W.Stack(2, intro, W.Muted(() => $"Gradle home: {home.Value}", home)), false),
            (W.Check("Show only unused", unusedOnly), false),
            (table.View, true));
        return Dialogs.Layout(body, Dialogs.Footer(
            W.Grow(W.Muted(Summary, message, items, changed)),
            W.Btn("Stop Gradle daemons", Stop),
            W.Btn("Close", () =>
            {
                open = false;
                done.Cancel();
            }),
            W.Btn("Delete selected", Delete, primary: true).EnabledWhen(() => items.Value.Any(i => i.Checked.Value), items, changed)), growBody: true);
    });
}
