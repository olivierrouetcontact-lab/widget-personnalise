using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MailWidget;

public sealed class AppSettings
{
    public DateTimeOffset? LastCheckedUtc { get; set; }
    public int PollingIntervalSeconds { get; set; } = 60;
    public bool StartWithWindows { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public int LayoutVersion { get; set; }
    public List<NoteItem> Notes { get; set; } = CreateStarterNotes();

    public static List<NoteItem> CreateStarterNotes() => new()
    {
        // Keep the public source free of personal reminders. Existing notes
        // are stored locally in %LOCALAPPDATA%\MailWidget\settings.json.
        new NoteItem(),
        new NoteItem(),
        new NoteItem(),
        new NoteItem()
    };
}

public sealed class NoteItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = string.Empty;
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MailWidget");

    private string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                settings.Notes ??= new List<NoteItem>();
                if (settings.Notes.Count is > 0 and <= 3
                    && settings.Notes.All(note => string.IsNullOrWhiteSpace(note.Text)))
                {
                    settings.Notes = AppSettings.CreateStarterNotes();
                }
                return settings;
            }
        }
        catch
        {
            // A corrupt settings file should not prevent the widget from starting.
        }

        return new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DataDirectory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        await File.WriteAllTextAsync(SettingsPath, json, cancellationToken);
    }
}
