using System.IO;
using System.Text.Json;
using AutoAudioAdjust.App.Models;

namespace AutoAudioAdjust.App.Services;

public sealed class SettingsStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AutoAudioAdjust");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
                settings.Validate();
                return settings;
            }
        }
        catch (JsonException)
        {
            // A damaged settings file should never prevent the app from starting.
        }
        catch (IOException)
        {
            // Defaults are safe when the settings directory is temporarily unavailable.
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            settings.Validate();
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch (IOException)
        {
            // Audio control should continue even if preferences cannot be persisted.
        }
        catch (UnauthorizedAccessException)
        {
            // The UI remains usable with in-memory settings.
        }
    }
}
