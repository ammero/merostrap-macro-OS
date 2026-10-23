// SPDX-License-Identifier: MPL-2.0
using System;
using System.Globalization;
using System.Linq;

namespace Merostrap.Macros;

internal static class MacroKeys
{
    public static string Normalize(string key) => key.Trim().ToLowerInvariant() switch
    {
        "control" or "left ctrl" or "right ctrl" or "lctrl" or "rctrl" => "ctrl",
        "left shift" or "right shift" => "shift", "left alt" or "right alt" => "alt",
        "escape" => "esc", "return" => "enter", "spacebar" => "space",
        "left mouse" or "left click" => "mouse1", "right mouse" or "right click" => "mouse2", var value => value
    };
    public static bool TryVirtualKey(string key, out ushort code)
    {
        key = Normalize(key);
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) { code = (ushort)char.ToUpperInvariant(key[0]); return true; }
        if (key.StartsWith('f') && int.TryParse(key.AsSpan(1), out var function) && function is >= 1 and <= 24) { code = (ushort)(111 + function); return true; }
        if (key.Length == 7 && key.StartsWith("numpad", StringComparison.Ordinal) && key[6] is >= '0' and <= '9') { code = (ushort)(96 + key[6] - '0'); return true; }
        code = key switch
        {
            "mouse1" => 1, "mouse2" => 2, "mouse3" => 4, "mouse4" => 5, "mouse5" => 6,
            "numpad multiply" => 106, "numpad add" => 107, "numpad subtract" => 109,
            "numpad decimal" => 110, "numpad divide" => 111, "numpad enter" => 13,
            "backspace" => 8, "tab" => 9, "enter" => 13, "shift" => 16, "ctrl" => 17, "alt" => 18,
            "caps lock" => 20, "esc" => 27, "space" => 32, "page up" => 33, "page down" => 34,
            "end" => 35, "home" => 36, "left" => 37, "up" => 38, "right" => 39, "down" => 40,
            "insert" => 45, "delete" => 46, ";" => 186, "=" => 187, "," => 188, "-" => 189,
            "." => 190, "/" => 191, "`" => 192, "[" => 219, "\\" => 220, "]" => 221, "'" => 222, _ => 0
        };
        return code != 0;
    }
    public static string FromVirtualKey(uint code) => code switch
    {
        >= 65 and <= 90 => ((char)(code + 32)).ToString(), >= 48 and <= 57 => ((char)code).ToString(),
        >= 112 and <= 135 => "f" + (code - 111).ToString(CultureInfo.InvariantCulture),
        >= 96 and <= 105 => "numpad" + (code - 96).ToString(CultureInfo.InvariantCulture),
        106 => "numpad multiply", 107 => "numpad add", 109 => "numpad subtract", 110 => "numpad decimal", 111 => "numpad divide",
        8 => "backspace", 9 => "tab", 13 => "enter", 16 or 160 or 161 => "shift", 17 or 162 or 163 => "ctrl", 18 or 164 or 165 => "alt",
        20 => "caps lock", 27 => "esc", 32 => "space", 33 => "page up", 34 => "page down", 35 => "end", 36 => "home",
        37 => "left", 38 => "up", 39 => "right", 40 => "down", 45 => "insert", 46 => "delete",
        186 => ";", 187 => "=", 188 => ",", 189 => "-", 190 => ".", 191 => "/", 192 => "`", 219 => "[", 220 => "\\", 221 => "]", 222 => "'", _ => ""
    };
    public static string FromKeyboard(uint code, uint scan, bool extended)
    {
        // Num Lock changes virtual keys into navigation keys; the physical scan stays the same.
        if (extended && code == 13) return "numpad enter";
        if (!extended)
        {
            var numpad = scan switch
            {
                0x52 => "numpad0", 0x4F => "numpad1", 0x50 => "numpad2", 0x51 => "numpad3",
                0x4B => "numpad4", 0x4C => "numpad5", 0x4D => "numpad6", 0x47 => "numpad7",
                0x48 => "numpad8", 0x49 => "numpad9", 0x53 => "numpad decimal", _ => ""
            };
            if (numpad.Length > 0) return numpad;
        }
        return FromVirtualKey(code);
    }
    public static string[] Parse(string value, string field)
    {
        // The keybind picker emits a literal comma for the physical comma key.
        if (value.Trim() == ",") return [","];
        var keys = value.Split([',', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(Normalize).Distinct(StringComparer.Ordinal).ToArray();
        if (keys.Length == 0 || keys.Any(k => !TryVirtualKey(k, out _))) throw new ArgumentException(field + " must contain valid keys separated by commas.");
        return keys;
    }
}
