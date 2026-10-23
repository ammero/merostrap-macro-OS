using Avalonia.Media;

namespace Merostrap.UI.Elements.Macros;

internal static class MacroTheme
{
    public static void Apply(MacroWindow window, string name)
    {
        var colors = name switch
        {
            "Midnight" => new[] { "#363639", "#303033", "#2D2D30", "#343438", "#4C4C52", "#F2F2F5", "#BDBDC6", "#AAAAB4", "#84B8FF", "#343D4A" },
            "Crimson" => new[] { "#363639", "#303033", "#2D2D30", "#343438", "#4C4C52", "#F2F2F5", "#BDBDC6", "#AAAAB4", "#E7475D", "#473039" },
            "Evergreen" => new[] { "#363639", "#303033", "#2D2D30", "#343438", "#4C4C52", "#F2F2F5", "#BDBDC6", "#AAAAB4", "#83DCAF", "#344239" },
            "Amethyst" => new[] { "#363639", "#303033", "#2D2D30", "#343438", "#4C4C52", "#F2F2F5", "#BDBDC6", "#AAAAB4", "#C5A0FF", "#413A4A" },
            _ => new[] { "#111216", "#15161B", "#191B21", "#1B1D24", "#353640", "#ECECF1", "#A8ACBA", "#969CAA", "#B8A8FF", "#363047" }
        };
        var keys = new[] { "MacroBackground", "MacroHeader", "MacroSurface", "MacroInput", "MacroBorder", "MacroText", "MacroMuted", "MacroSubtle", "MacroAccent", "MacroSelection" };
        for (var i = 0; i < keys.Length; i++) window.Resources[keys[i]] = Brush.Parse(colors[i]);
        window.Resources["MacroTextField"] = Brush.Parse("#29292D");
        window.Resources["MacroFrame"] = Brush.Parse(name != "Obsidian" ? "#55555B" : colors[4]);
        window.Resources["MacroInterior"] = Brush.Parse(name != "Obsidian" ? "#202023" : colors[1]);
        window.Resources["SystemAccentColor"] = Color.Parse(colors[8]);
        window.Resources["SystemControlHighlightAccentBrush"] = Brush.Parse(colors[8]);
        window.Resources["ToggleSwitchFillOn"] = Brush.Parse(colors[8]);
        foreach (var key in new[] { "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentTextFillColorPrimaryBrush", "ComboBoxItemPillFillBrush", "ToggleSwitchFillOnPointerOver", "ToggleSwitchFillOnPressed", "TextControlBorderBrushFocused" })
            window.Resources[key] = Brush.Parse(colors[8]);
        foreach (var key in new[] { "ListViewItemBackgroundSelected", "ListViewItemBackgroundSelectedPointerOver", "ListViewItemBackgroundSelectedPressed" })
            window.Resources[key] = Brush.Parse(colors[9]);
    }
}
