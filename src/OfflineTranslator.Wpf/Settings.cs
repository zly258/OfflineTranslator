using System;
using System.IO;
using System.Text.Json;

namespace OfflineTranslator.Wpf;

/// <summary>每用户设置（%AppData%\WISDRI\OfflineTranslator\settings.json）。</summary>
internal static class Settings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WISDRI", "OfflineTranslator", "settings.json");

    /// <summary>上次成功加载的模型文件名，用于下次启动自动选中。</summary>
    public static string? LastModelFileName
    {
        get => Read()?.LastModelFileName;
        set
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var json = JsonSerializer.Serialize(new SettingsData { LastModelFileName = value },
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json, new System.Text.UTF8Encoding(false));
            }
            catch { /* 设置写不进去不影响翻译 */ }
        }
    }

    private static SettingsData? Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(FilePath));
        }
        catch
        {
            return null;
        }
    }

    private sealed class SettingsData
    {
        public string? LastModelFileName { get; set; }
    }
}
