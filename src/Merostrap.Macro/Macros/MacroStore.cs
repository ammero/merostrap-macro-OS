using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Merostrap.Macros;

/// <summary>Stores macro preferences independently of launcher settings.</summary>
public sealed class MacroStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly string _path;
    private readonly object _sync = new();
    private string? _preservedCorruptJson;

    public MacroStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    /// <summary>A recoverable JSON error; file access errors are propagated.</summary>
    public string? LastLoadWarning { get; private set; }
    /// <summary>Location of the untouched invalid document, when recovery occurred.</summary>
    public string? RecoveryPath { get; private set; }

    public MacroPreferences Load()
    {
        lock (_sync)
        {
            LastLoadWarning = null;
            RecoveryPath = null;
            if (!File.Exists(_path))
                return new MacroPreferences();

            var json = File.ReadAllText(_path);
            try
            {
                var preferences = JsonSerializer.Deserialize<MacroPreferences>(json, JsonOptions)
                    ?? throw new JsonException("The settings document is null.");
                return Normalize(preferences);
            }
            catch (JsonException)
            {
                PreserveCorruptDocument(json);
                return new MacroPreferences();
            }
        }
    }

    /// <summary>Flushes a sibling temporary file, then atomically replaces the document.</summary>
    public void Save(MacroPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        lock (_sync)
        {
            // Editors retain references to live profiles and their Values dictionaries.
            // Normalize a detached snapshot so persistence cannot reset ongoing edits.
            var snapshot = JsonSerializer.Deserialize<MacroPreferences>(
                JsonSerializer.SerializeToUtf8Bytes(preferences, JsonOptions), JsonOptions)!;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            PreserveInvalidExistingDocument();
            var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, Normalize(snapshot), JsonOptions);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, _path, overwrite: true);
                _preservedCorruptJson = null;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
    }

    private void PreserveInvalidExistingDocument()
    {
        if (!File.Exists(_path))
            return;
        var json = File.ReadAllText(_path);
        try
        {
            _ = JsonSerializer.Deserialize<MacroPreferences>(json, JsonOptions)
                ?? throw new JsonException("The settings document is null.");
        }
        catch (JsonException)
        {
            if (!string.Equals(json, _preservedCorruptJson, StringComparison.Ordinal))
                PreserveCorruptDocument(json);
        }
    }

    private void PreserveCorruptDocument(string json)
    {
        var backup = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.json";
        // Do not return recoverable defaults unless the original is safely preserved.
        File.Copy(_path, backup, overwrite: false);
        RecoveryPath = backup;
        LastLoadWarning = $"Macro settings could not be read. Defaults were restored; the original is saved at {backup}.";
        _preservedCorruptJson = json;
    }

    private static MacroPreferences Normalize(MacroPreferences preferences)
    {
        preferences.DisplayDevice ??= "";
        preferences.GuiKeybind ??= "f10";
        preferences.GuiTheme ??= "Obsidian";
        preferences.ParryBar ??= new();
        preferences.DodgeBar ??= new();
        preferences.Profiles ??= [];
        if (preferences.Profiles.Count > 10)
            preferences.Profiles.RemoveRange(10, preferences.Profiles.Count - 10);
        while (preferences.Profiles.Count < 10)
            preferences.Profiles.Add(new MacroProfile());

        for (var index = 0; index < preferences.Profiles.Count; index++)
        {
            var profile = preferences.Profiles[index] ?? new MacroProfile();
            preferences.Profiles[index] = profile;
            if (string.IsNullOrWhiteSpace(profile.Name))
                profile.Name = $"Preset {index + 1}";
            profile.Entries = profile.Entries is null
                ? MacroCatalog.CreateEntries()
                : new Dictionary<string, MacroEntry>(profile.Entries, StringComparer.Ordinal);

            foreach (var id in new List<string>(profile.Entries.Keys))
            {
                var entry = profile.Entries[id] ?? new MacroEntry();
                entry.Values = entry.Values is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(entry.Values, StringComparer.Ordinal);
                profile.Entries[id] = entry;
            }

            profile.Entries.Remove("auto-guild-exile");
            profile.Entries.Remove("export-guild-members");
            profile.Entries.Remove("auto-agility");
            profile.Entries.Remove("auto-fortitude");
            profile.Entries.Remove("auto-willpower");
            profile.Entries.Remove("charisma-autofill");

            foreach (var definition in MacroCatalog.All)
            {
                if (!profile.Entries.TryGetValue(definition.Id, out var entry))
                {
                    entry = new MacroEntry();
                    profile.Entries.Add(definition.Id, entry);
                }
                foreach (var field in definition.Fields)
                {
                    if (!entry.Values.TryGetValue(field.Key, out var value) || value is null)
                        entry.Values[field.Key] = field.DefaultValue;
                }
            }
        }
        preferences.SelectedProfile = Math.Clamp(preferences.SelectedProfile, 0, 9);
        return preferences;
    }
}
