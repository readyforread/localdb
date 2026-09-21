using System;
using System.IO;
using System.Text.Json;

namespace MdfBakViewer;

public sealed class Settings
{
    public string WorkDirectory { get; set; } = DefaultWorkDirectory;

    public string LastInstance { get; set; } = @"(localdb)\MSSQLLocalDB";

    public string? LastOpenFolder { get; set; }

    private static string DefaultWorkDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MdfBakViewer",
        "Work");

    private static string SettingsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MdfBakViewer");

    private static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new Settings();

            string json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();

            if (string.IsNullOrWhiteSpace(settings.WorkDirectory))
                settings.WorkDirectory = DefaultWorkDirectory;

            return settings;
        }
        catch
        {
            return new Settings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(this, options);

            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // молча игнорируем — это не критично
        }
    }
}
