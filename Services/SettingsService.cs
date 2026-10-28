using System;
using System.IO;
using System.Text.Json;

namespace Flow.Plugin.ClipHistory.Services;

public class SettingsService
{
    private readonly string _settingsFilePath;
    private readonly object _lock = new();
    private AppSettings _settings;

    public event Action<AppSettings>? OnSettingsChanged;

    public AppSettings Settings
    {
        get
        {
            lock (_lock)
            {
                return _settings;
            }
        }
    }

    public SettingsService(string baseDataDir)
    {
        if (!Directory.Exists(baseDataDir))
        {
            Directory.CreateDirectory(baseDataDir);
        }
        _settingsFilePath = Path.Combine(baseDataDir, "Settings.json");
        _settings = Load();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    if (loaded.MaxHistoryCount < 50) loaded.MaxHistoryCount = 50;
                    if (loaded.MaxHistoryCount > 10000) loaded.MaxHistoryCount = 10000;
                    if (loaded.MaxContentSizeKB < 64) loaded.MaxContentSizeKB = 64;
                    if (loaded.MaxContentSizeKB > 51200) loaded.MaxContentSizeKB = 51200;
                    return loaded;
                }
            }
        }
        catch { }

        var defaults = new AppSettings();
        Save(defaults);
        return defaults;
    }

    public void Update(Action<AppSettings> updateAction)
    {
        AppSettings updated;
        lock (_lock)
        {
            updateAction(_settings);
            updated = _settings;
            Save(_settings);
        }
        OnSettingsChanged?.Invoke(updated);
    }

    public void Save(AppSettings? settings = null)
    {
        lock (_lock)
        {
            var target = settings ?? _settings;
            try
            {
                var json = JsonSerializer.Serialize(target, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsFilePath, json);
            }
            catch { }
        }
    }
}
