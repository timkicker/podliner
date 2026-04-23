using StuiPodcast.App.Services;
using StuiPodcast.App.UI;

namespace StuiPodcast.App.Command.UseCases;

// Handles the OS-level side-effects for :open (hand a URL to the default
// browser / xdg-open) and :copy (stuff text into the clipboard). Reflection
// is used to tolerate the many feed-metadata property names publishers use.
internal sealed class IoUseCase
{
    readonly IUiShell _ui;
    readonly IFeedStore _feedStore;

    public IoUseCase(IUiShell ui, IFeedStore feedStore)
    {
        _ui = ui;
        _feedStore = feedStore;
    }

    public void ExecOpen(string[] args)
    {
        var mode = (args.Length > 0 ? args[0] : "site").Trim().ToLowerInvariant(); // "site" | "audio"
        var ep = _ui.GetSelectedEpisode();
        if (ep == null) { _ui.ShowOsd("no episode selected"); return; }

        string? url = null;

        if (mode == "audio") url = ep.AudioUrl;
        else
        {
            url = GetPropString(ep, "Link", "PageUrl", "Website", "WebsiteUrl", "HtmlUrl");
            if (string.IsNullOrWhiteSpace(url))
            {
                var feed = _feedStore.Find(ep.FeedId);
                url = GetPropString(feed, "Link", "Website", "WebsiteUrl", "HtmlUrl", "Home");
            }
            if (string.IsNullOrWhiteSpace(url)) url = ep.AudioUrl;
        }

        if (string.IsNullOrWhiteSpace(url)) { _ui.ShowOsd("no URL to open"); return; }
        if (!TryOpenSystem(url)) _ui.ShowOsd(url, 2000);
    }

    public void ExecCopy(string[] args)
    {
        var what = (args.Length > 0 ? args[0] : "url").Trim().ToLowerInvariant(); // url|title|guid
        var ep = _ui.GetSelectedEpisode();
        if (ep == null) { _ui.ShowOsd("no episode selected"); return; }

        string? text = what switch
        {
            "title" => ep.Title ?? "",
            "guid"  => GetPropString(ep, "Guid", "EpisodeGuid") ?? ep.Id.ToString(),
            _       => ep.AudioUrl ?? ""
        };

        if (string.IsNullOrWhiteSpace(text)) { _ui.ShowOsd("nothing to copy"); return; }

        if (TryCopyToClipboard(text)) _ui.ShowOsd("copied", 800);
        else _ui.ShowOsd(text, 3000);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    static bool TryOpenSystem(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            { var psi = new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }; System.Diagnostics.Process.Start(psi); return true; }
            if (OperatingSystem.IsMacOS())
            { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open", url) { UseShellExecute = false }); return true; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
            return true;
        }
        catch { return false; }
    }

    static string? GetPropString(object? obj, params string[] names)
    {
        if (obj == null) return null;
        var t = obj.GetType();
        foreach (var n in names)
        {
            var p = t.GetProperty(n, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            if (p != null && p.PropertyType == typeof(string))
            {
                var v = (string?)p.GetValue(obj);
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            }
        }
        return null;
    }

    static bool TryCopyToClipboard(string text)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var psi = new System.Diagnostics.ProcessStartInfo("powershell", $"-NoProfile -Command Set-Clipboard -Value @'\n{text}\n'@")
                { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(1200);
                return p != null && p.ExitCode == 0;
            }
            if (OperatingSystem.IsMacOS())
            {
                var psi = new System.Diagnostics.ProcessStartInfo("pbcopy") { UseShellExecute = false, RedirectStandardInput = true };
                using var p = System.Diagnostics.Process.Start(psi);
                p!.StandardInput.Write(text); p.StandardInput.Close(); p.WaitForExit(800);
                return true;
            }
            foreach (var tool in new[] { "xclip", "xsel" })
            {
                try
                {
                    var psi = tool == "xclip"
                        ? new System.Diagnostics.ProcessStartInfo("xclip", "-selection clipboard")
                        : new System.Diagnostics.ProcessStartInfo("xsel", "--clipboard --input");
                    psi.UseShellExecute = false; psi.RedirectStandardInput = true;
                    using var p = System.Diagnostics.Process.Start(psi);
                    p!.StandardInput.Write(text); p.StandardInput.Close(); p.WaitForExit(800);
                    return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }
}
