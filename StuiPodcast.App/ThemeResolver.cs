using StuiPodcast.App.UI;

namespace StuiPodcast.App;

// ==========================================================
// Theme resolving (default = User)
// ==========================================================
static class ThemeResolver
{
    public sealed record Result(Shell.ThemeMode Mode, string? ShouldPersistPref);

    public static Result Resolve(string? cliTheme, string? savedPref)
    {
        if (!string.IsNullOrWhiteSpace(cliTheme))
        {
            var t = cliTheme.Trim().ToLowerInvariant();
            var cliAskedAuto = t == "auto";

            Shell.ThemeMode tm = t switch
            {
                "base"   => Shell.ThemeMode.Base,
                "accent" => Shell.ThemeMode.MenuAccent,
                "native" => Shell.ThemeMode.Native,
                "user"   => Shell.ThemeMode.User,
                "auto"   => Shell.ThemeMode.User, // default → user
                _        => Shell.ThemeMode.User
            };
            return new Result(tm, cliAskedAuto ? "auto" : tm.ToString());
        }

        var pref = (savedPref ?? "auto").Trim();
        Shell.ThemeMode desired =
            pref.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? Shell.ThemeMode.User
                : (Enum.TryParse<Shell.ThemeMode>(pref, out Shell.ThemeMode saved) ? saved : Shell.ThemeMode.User);

        // Keep "auto" string if it was saved
        return new Result(desired, null);
    }
}