using StuiPodcast.App.UI;

namespace StuiPodcast.App.Services;

// ==========================================================
// Theme resolving (default = User)
// ==========================================================
static class UiThemeResolver
{
    public sealed record Result(UiShell.ThemeMode Mode, string? ShouldPersistPref);

    public static Result Resolve(string? cliTheme, string? savedPref)
    {
        if (!string.IsNullOrWhiteSpace(cliTheme))
        {
            var t = cliTheme.Trim().ToLowerInvariant();
            var cliAskedAuto = t == "auto";

            UiShell.ThemeMode tm = t switch
            {
                "base"   => UiShell.ThemeMode.Base,
                "accent" => UiShell.ThemeMode.MenuAccent,
                "native" => UiShell.ThemeMode.Native,
                "user"   => UiShell.ThemeMode.User,
                "auto"   => UiShell.ThemeMode.User, // default → user
                _        => UiShell.ThemeMode.User
            };
            return new Result(tm, cliAskedAuto ? "auto" : tm.ToString());
        }

        var pref = (savedPref ?? "auto").Trim();
        UiShell.ThemeMode desired =
            pref.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? UiShell.ThemeMode.User
                : Enum.TryParse(pref, out UiShell.ThemeMode saved) ? saved : UiShell.ThemeMode.User;

        // Keep "auto" string if it was saved
        return new Result(desired, null);
    }
}