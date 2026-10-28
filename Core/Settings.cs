using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace RAM.Core;

public sealed class RecentPlace
{
    public long PlaceId { get; set; }
    public string Name { get; set; } = "";
    public string? IconUrl { get; set; }
    public DateTime LastUsed { get; set; } = DateTime.Now;
}

public sealed class AppSettings
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public bool MultiInstanceOnStartup { get; set; } = true;
    public bool HoldLegacyMutex { get; set; } = true;
    public bool ReleaseCookieLock { get; set; }
    public bool RenameWindows { get; set; } = true;
    public bool DirectLaunch { get; set; }
    public int LaunchDelaySeconds { get; set; } = 6;
    public string Theme { get; set; } = "system";
    public bool MinimizeToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }
    public ObservableCollection<RecentPlace> RecentPlaces { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Paths.Settings))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.Settings)) ?? new AppSettings();
                // Hand-edited files must not break the launch loop or the theme switch.
                settings.LaunchDelaySeconds = Math.Clamp(settings.LaunchDelaySeconds, 0, 30);
                if (settings.Theme is not ("system" or "light" or "dark")) settings.Theme = "system";
                settings.RecentPlaces ??= new ObservableCollection<RecentPlace>();
                return settings;
            }
        }
        catch (Exception ex)
        {
            Log.Error("load settings", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Paths.WriteAtomic(Paths.Settings, JsonSerializer.SerializeToUtf8Bytes(this, Options));
        }
        catch (Exception ex)
        {
            Log.Error("save settings", ex);
        }
    }

    public void RememberPlace(long placeId, string name, string? iconUrl)
    {
        for (int i = RecentPlaces.Count - 1; i >= 0; i--)
            if (RecentPlaces[i].PlaceId == placeId) RecentPlaces.RemoveAt(i);
        RecentPlaces.Insert(0, new RecentPlace { PlaceId = placeId, Name = name, IconUrl = iconUrl });
        while (RecentPlaces.Count > 8) RecentPlaces.RemoveAt(RecentPlaces.Count - 1);
        Save();
    }
}
