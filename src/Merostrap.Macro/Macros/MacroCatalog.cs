// Catalog semantics and field names follow DeepWarden, commit 6e41a224995ec7473bffbd55254ddf69dfb70c57.
// Reference license: PolyForm Noncommercial 1.0.0, LICENSES/PolyForm-Noncommercial-1.0.0.txt.
using System;
using System.Collections.Generic;

namespace Merostrap.Macros;

public static class MacroCatalog
{
    // User-specific binds, coordinates, messages and paths start empty. Numeric and
    // boolean fields have conservative defaults; every new entry starts disabled.
    public static IReadOnlyList<MacroDefinition> All { get; } = Build();

    internal static Dictionary<string, MacroEntry> CreateEntries()
    {
        var entries = new Dictionary<string, MacroEntry>(StringComparer.Ordinal);
        foreach (var definition in All)
        {
            var entry = new MacroEntry();
            foreach (var field in definition.Fields)
                entry.Values[field.Key] = field.DefaultValue;
            entries.Add(definition.Id, entry);
        }
        return entries;
    }

    private static IReadOnlyList<MacroDefinition> Build()
    {
        var definitions = new List<MacroDefinition>
        {
            Define("hold-m1", "Faster Hold M1", "Combat", "Hold your alternate attack key while holding the primary mouse button.",
                new MacroField("keyToPress", "Alternate attack key", "`", "One keyboard character bound to primary attack in Roblox. Default: backtick (`).")),
            Define("right-mouse-parry", "Right mouse button parry", "Combat", "Press F immediately when you right-click. Keeps the normal right-click action."),
            Define("auto-feint", "Auto Feint", "Combat", "Trigger a feint with the secondary mouse button when parry (F) is pressed."),
            Define("auto-roll-cancel", "Auto Roll Cancel", "Combat", "Cancel a roll with the secondary mouse button."),
            Define("auto-uppercut", "Auto Uppercut", "Combat", "Repeat primary attacks while holding Ctrl for an uppercut."),
            Define("dynamic-auto-uppercut", "Dynamic Auto Uppercut", "Combat", "Combine directional input and primary attack with an uppercut.",
                Boolean("toRoll", "Roll after uppercut", "Send the roll key after the uppercut.")),
            Define("uppercut-assassinate", "Uppercut Assassinate", "Combat", "Chain an uppercut into an assassination.", Bind(),
                Boolean("dustLunge", "Dust Lunge", "Skip sending mb all before the assassination.")),
            Define("roll-crit", "Roll Crit", "Combat", "Follow the critical attack key (R) with a roll (Q)."),
            Define("roll-m1", "Roll M1", "Combat", "Run the recorded Roll M1 sequence from your hotkey.", Bind(), Boolean("highPing", "High ping (100+ ms)", "Off uses the low-ping sequence; on uses the high-ping sequence.")),
            Define("roll-parry", "Roll Parry", "Combat", "Chain a roll and parry from your hotkey.", Bind()),
            Define("bell-stack-dodge", "Bell Stack Dodge", "Combat", "Follow the bell key (C) with a dodge (Q)."),
            Define("bell-stack-parry", "Bell Stack Parry", "Combat", "Parry (F) before using the bell key (C)."),
            Define("silentheart-flow-state", "Automatic Flow State", "Silentheart", "Activate Flow State before slide attacks, dodge attacks, aerial follow-ups and Ctrl + Feint. Set its toolbar key first.", FlowState(),
                new MacroField("dodgekeybind", "Dodge key", "q", "Your Roblox dodge key. Default: Q."),
                new MacroField("crouchkeybind", "Crouch key", "ctrl", "Your Roblox crouch / slide key. Rising Star still uses Ctrl."),
                new MacroField("jumpkeybind", "Jump key", "space", "Your Roblox jump key. Default: Space."),
                new MacroField("feintkeybind", "Feint / cancel key", "mouse2", "Your Roblox feint / cancel key. Default: right mouse button.")),
            Define("ankle-cutter", "Ankle Cutter", "Silentheart", "Trigger the Ankle Cutter sequence with your Flow State key.", Bind(), FlowState()),
            Define("mayhem", "Mayhem", "Silentheart", "Trigger the Mayhem sequence with your Flow State key.", Bind(), FlowState()),
            Define("relentless-hunt", "Relentless Hunt", "Silentheart", "Trigger the Relentless Hunt sequence with your Flow State key.", Bind(), FlowState()),
            Define("rising-star", "Rising Star", "Silentheart", "Trigger the Rising Star sequence with your Flow State key.", Bind(), FlowState()),
            Define("ritual-cast", "Ritual Cast", "Mantras", "Automatically reads ritual prompts while Roblox is focused. Detected areas save locally. Escape pauses automatic input until macros are toggled off/on.",
                Coordinates("startcoordinates", "Prompt top-left"), Coordinates("endcoordinates", "Prompt bottom-right"),
                new("fast_mode", "Fast casting", "true", "Read consistently recognized letters while they slide. Retry a still-visible prompt up to twice if it does not advance. Turn off to wait for letters to settle without retries."),
                new("ping_ms", "Ping compensation (ms)", "100", "Tunes key press duration from 50 to 150 ms; waits for visible prompt advancement before the next key. Whole number from 0 to 1000. Include the entire outlined ritual frame and both prompt rows; exclude the title.")),
            Define("auto-buy", "Auto Buy", "Inventory", "Drag the purchase quantity slider and confirm purchases.", Bind(),
                Coordinates("startcoordinates", "Slider start"), Coordinates("endcoordinates", "Slider end"), Coordinates("submitcoordinates", "Buy button")),
            Define("auto-drop-notes", "Auto Drop Notes", "Inventory", "Select notes, set the amount and confirm each drop.", Bind(), Repetitions(),
                Coordinates("notecoordinates", "Notes item"), Coordinates("startcoordinates", "Slider start"), Coordinates("endcoordinates", "Slider end"), Coordinates("confirmationcoordinates", "Confirm button")),
            Define("auto-eat", "Auto Eat", "Inventory", "Find your chosen food in the inventory and use it.", Bind(),
                new("foodname", "Food name", "", "Exact food name to type into the inventory search."),
                Coordinates("boxcoordinates", "Inventory search"), Coordinates("foodcoordinates", "Food item")),
            Define("auto-sell", "Auto Sell", "Inventory", "Select an item and confirm each sale.", Bind(), Repetitions(),
                Coordinates("itemcoordinates", "Item to sell"), Coordinates("confirmationcoordinates", "Confirm button")),
            Define("motif-swap", "Motif Swap", "Inventory", "Equip a motif, use it, then equip your weapon.", Bind(),
                new("motifnum", "Motif toolbar slot", "1", "Toolbar slot key: a digit from 0 to 9. Default: 1."),
                new("weaponnum", "Weapon toolbar slot", "2", "Toolbar slot key: a digit from 0 to 9. Default: 2.")),
            Define("tacet-toggle", "Tacet Toggle", "Utility", "Crouch, activate Tacet, and optionally stand again.", Bind(),
                Boolean("uncrouch", "Stand after Tacet", "Uncrouch after activating Tacet.")),
            Define("auto-log", "Auto Log", "Utility", "Click the configured logout location and confirm with Shift.", Bind(), Coordinates("coordinates", "Logout button")),
            Define("auto-pender", "Auto Pender", "Utility", "Toggle repeated clicks alternating between two screen points. Press the hotkey again to stop.", Bind(),
                Coordinates("firstcoordinates", "Point 1"), Coordinates("secondcoordinates", "Point 2"),
                new("interval_ms", "Delay between clicks (ms)", "100", "Wait after each click before moving to the other point. Whole number from 20 to 5000 milliseconds. Escape or leaving Roblox stops the loop.")),
            Define("flash-map", "Flash Map", "Utility", "Show the map while holding M and close it when released."),
            Define("golden-tongue", "Golden Tongue", "Chat", "Send your configured phrase through Roblox chat.", ChatFields()),
            Define("mb-all", "Mb All", "Chat", "Send “mb all” through Roblox chat.", Bind()),
            Define("trash-talk", "Trash Talk", "Chat", "Send a random line from your chosen text file.", Bind(),
                new("txtpath", "Message file", "", "Full path to a UTF-8 text file with one chat message per line.")),
            Define("gank-pinger", "Gank Pinger", "Chat", "Send your saved message to a Discord webhook once per hotkey press.", Bind(),
                new("delivery_mode", "Delivery", "0", "Send through a webhook or your personal account."),
                new("webhook_url", "Discord webhook", "", "Full HTTPS Discord webhook URL."),
                new("discord_token", "Discord token", "", "Stored encrypted for your Windows account."),
                new("channel_id", "Channel ID", "", "Discord channel to receive the alert."),
                new("message", "Message", "", "Message to send, up to 2000 characters."),
                new("capture_window", "Screenshot window", "", "Select a window to attach, or leave screenshots off."))
        };
        for (var slot = 1; slot <= 10; slot++)
            definitions.Add(Define($"chat-message-{slot}", $"Chat Message {slot}", "Chat", "Send a saved message through Roblox chat.", ChatFields()));
        return definitions.AsReadOnly();
    }

    private static MacroDefinition Define(string id, string name, string category, string description, params MacroField[] fields)
        => new(id, name, category, description, Array.AsReadOnly(fields));

    private static MacroField Bind() => new("keybind", "Hotkey", "", "One keyboard key, numpad key, or side mouse button (Mouse 4/5). Leave blank until configured.");
    private static MacroField Binds() => new("keybinds", "Mantra keys", "", "Comma-separated keyboard keys, such as 1,2,3. Each key triggers the sequence.");
    private static MacroField FlowState() => new("flowstatekeybind", "Flow State key", "", "Keyboard key bound to Flow State in Roblox, such as 4.");
    private static MacroField Coordinates(string key, string label) => new(key, label, "", "Screen pixel coordinates as X Y, such as 960 540. Configure for your current display and game window.");
    private static MacroField Repetitions() => new("repetitions", "Repetitions", "1", "Positive whole number of repetitions. Default: 1.");
    private static MacroField Boolean(string key, string label, string action) => new(key, label, "false", $"true or false. {action} Default: false.");
    private static MacroField[] ChatFields() =>
    [
        Bind(),
        new("content", "Message", "", "Chat text to send. Unicode text is supported."),
        Boolean("autosprint", "Resume sprint", "Resume forward sprint after sending the message.")
    ];
}
