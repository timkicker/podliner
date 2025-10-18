using System.Text.Json;
using StuiPodcast.Core;

namespace StuiPodcast.Infra;

public static class AppStorage
{
    // ---- Pfade ----
    private static readonly string ConfigDir =
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData).Replace("\\", "/");

    private static readonly string BaseDir = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? ConfigDir,
        "podliner");

    private static readonly string DataPath = Path.Combine(BaseDir, "appdata.json");
    private static readonly string Bak1Path = Path.Combine(BaseDir, "appdata.json.bak1");
    private static readonly string Bak2Path = Path.Combine(BaseDir, "appdata.json.bak2");

    // ---- Status ----
    public static bool ReadOnlyMode { get; private set; }
    public static string? ReadOnlyReason { get; private set; }

    // JSON-Optionen (tolerant)
    private static readonly JsonSerializerOptions _jsonReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonSerializerOptions _jsonWriteOpts = new()
    {
        WriteIndented = true
    };

    static AppStorage()
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            // Write-Probe: kann das Verzeichnis beschreiben?
            var probe = Path.Combine(BaseDir, ".write_probe.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            ReadOnlyMode = false;
            ReadOnlyReason = null;
        }
        catch (Exception ex)
        {
            // Kein Schreiben möglich → ReadOnlyMode aktivieren (wir werfen NICHT)
            ReadOnlyMode = true;
            ReadOnlyReason = ex.GetType().Name + (ex.Message is { Length: > 0 } ? $": {ex.Message}" : "");
        }
    }

    // -------------------- Load --------------------
    public static async Task<AppData> LoadAsync()
    {
        Directory.CreateDirectory(BaseDir);

        // 1) Primärdatei
        var data = await TryLoadAsync(DataPath);
        if (data is not null) return data;

        // 2) Backup 1
        data = await TryLoadAsync(Bak1Path);
        if (data is not null) return data;

        // 3) Backup 2
        data = await TryLoadAsync(Bak2Path);
        if (data is not null) return data;

        // Fallback: neue, leere AppData
        return new AppData();
    }

    private static async Task<AppData?> TryLoadAsync(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            return JsonSerializer.Deserialize<AppData>(json, _jsonReadOpts);
        }
        catch
        {
            // Korrupte Datei → caller versucht nächste Stufe
            return null;
        }
    }

    // -------------------- Save (best effort, atomar + Backups) --------------------
    public static async Task SaveAsync(AppData data)
    {
        Directory.CreateDirectory(BaseDir);

        // Wenn wir nicht schreiben dürfen → still überspringen (UI kann ReadOnlyMode abfragen)
        if (ReadOnlyMode) return;

        // Serialize
        string json;
        try
        {
            json = JsonSerializer.Serialize(data, _jsonWriteOpts);
        }
        catch
        {
            // Sollte nicht passieren; keine Datei anrühren
            return;
        }

        var tmp = DataPath + ".tmp";

        // 1) tmp schreiben
        try
        {
            await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
        }
        catch
        {
            // tmp selbst konnte nicht geschrieben werden → keine weiteren Aktionen
            return;
        }

        // 2) Backups rotieren (best effort)
        try
        {
            // .bak2 löschen
            if (File.Exists(Bak2Path))
                SafeDelete(Bak2Path);

            // .bak1 -> .bak2
            if (File.Exists(Bak1Path))
                SafeMove(Bak1Path, Bak2Path, overwrite: true);

            // appdata.json -> .bak1
            if (File.Exists(DataPath))
                SafeMove(DataPath, Bak1Path, overwrite: true);
        }
        catch
        {
            // Rotation ist best-effort; weiter zum finalen Move
        }

        // 3) tmp → appdata.json (atomar, mit Retry bei Windows/AV-Lock)
        const int MAX_TRIES = 3;
        for (int attempt = 1; attempt <= MAX_TRIES; attempt++)
        {
            try
            {
                File.Move(tmp, DataPath, overwrite: true);
                return; // success
            }
            catch (IOException) when (attempt < MAX_TRIES)
            {
                // Kurz warten und erneut versuchen (AV/Scanner sperrt Datei oft nur ms)
                await Task.Delay(80 * attempt).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < MAX_TRIES)
            {
                await Task.Delay(100 * attempt).ConfigureAwait(false);
            }
            catch
            {
                break; // nicht recoverbar
            }
        }

        // 4) Cleanup: tmp entfernen (best effort)
        SafeDelete(tmp);
        // Hinweis: Falls der finale Move scheiterte, bleibt .bak1 als letzte gute Version erhalten.
    }

    // -------------------- Helpers --------------------
    private static void SafeDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    private static void SafeMove(string src, string dst, bool overwrite)
    {
        try
        {
            if (overwrite && File.Exists(dst)) File.Delete(dst);
            File.Move(src, dst);
        }
        catch
        {
            // best effort
        }
    }
}
