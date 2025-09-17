using System.Text.Json;
using StuiPodcast.Core;

namespace StuiPodcast.Infra;

public static class AppStorage {
    static readonly string ConfigDir = 
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        .Replace("\\","/"); // cross-plat

    static readonly string BaseDir = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? ConfigDir,
        "stui-podcast");

    static readonly string DataPath = Path.Combine(BaseDir, "appdata.json");

    public static async Task<AppData> LoadAsync() {
        Directory.CreateDirectory(BaseDir);
        if (!File.Exists(DataPath)) return new AppData();
        var json = await File.ReadAllTextAsync(DataPath);
        return JsonSerializer.Deserialize<AppData>(json, new JsonSerializerOptions {
            PropertyNameCaseInsensitive = true
        }) ?? new AppData();
    }

    public static async Task SaveAsync(AppData data) {
        Directory.CreateDirectory(BaseDir);
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        var tmp = DataPath + ".tmp";
        await File.WriteAllTextAsync(tmp, json);
        File.Move(tmp, DataPath, true); // atomarer Replace
    }


}
