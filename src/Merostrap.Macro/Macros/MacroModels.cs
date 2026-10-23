using System;
using System.Collections.Generic;

namespace Merostrap.Macros;

public sealed record MacroDefinition(string Id, string Name, string Category, string Description, IReadOnlyList<MacroField> Fields);
public sealed record MacroField(string Key, string Label, string DefaultValue, string Help);

public sealed class MacroEntry
{
    public bool Enabled { get; set; }
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);
}

public sealed class MacroProfile
{
    public string Name { get; set; } = "";
    public Dictionary<string, MacroEntry> Entries { get; set; } = MacroCatalog.CreateEntries();
}

public sealed class MacroPreferences
{
    /// <summary>Ten independent presets; all macro switches start disabled.</summary>
    public List<MacroProfile> Profiles { get; set; } = CreateDefaultProfiles();
    public int SelectedProfile { get; set; }
    public string GuiKeybind { get; set; } = "f10";
    public string GuiTheme { get; set; } = "Obsidian";
    public bool ReduceMotion { get; set; }
    public bool OverlayVisible { get; set; }
    public bool OverlayLocked { get; set; }
    public int? OverlayX { get; set; }
    public int? OverlayY { get; set; }
    public string DisplayDevice { get; set; } = "";
    public int? GuiX { get; set; }
    public int? GuiY { get; set; }
    public DodgeBarSettings DodgeBar { get; set; } = new();
    public ParryBarSettings ParryBar { get; set; } = new();
    public FlowStateOverlaySettings FlowStateOverlay { get; set; } = new();

    internal static List<MacroProfile> CreateDefaultProfiles()
    {
        var profiles = new List<MacroProfile>(10);
        for (var index = 0; index < 10; index++)
            profiles.Add(new MacroProfile { Name = $"Preset {index + 1}" });
        return profiles;
    }
}

public sealed class FlowStateOverlaySettings
{
    public bool Enabled { get; set; }
    public bool Vertical { get; set; }
    public string Color { get; set; } = "#39BA68";
    public int? X { get; set; }
    public int? Y { get; set; }
}

