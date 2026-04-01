using StuiPodcast.App.UI;

namespace StuiPodcast.App.Services;

static class UiThemeResolver
{
    public sealed record Result(ThemeMode Mode, string? ShouldPersistPref);

    public static Result Resolve(string? cliTheme, string? savedPref)
    {
        if (!string.IsNullOrWhiteSpace(cliTheme))
        {
            var t = cliTheme.Trim().ToLowerInvariant();
            var cliAskedAuto = t == "auto";

            ThemeMode tm = t switch
            {
                "base"   => ThemeMode.Base,
                "accent" => ThemeMode.MenuAccent,
                "native" => ThemeMode.Native,
                "user"   => ThemeMode.User,
                "auto"   => ThemeMode.User, // default user
                _        => ThemeMode.User
            };
            return new Result(tm, cliAskedAuto ? "auto" : tm.ToString());
        }

        var pref = (savedPref ?? "auto").Trim();
        ThemeMode desired =
            pref.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? ThemeMode.User
                : Enum.TryParse(pref, out ThemeMode saved) ? saved : ThemeMode.User;

        // keep "auto" string if it was saved
        return new Result(desired, null);
    }
}