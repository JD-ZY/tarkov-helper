namespace TarkovHelper.App;

// What kind of physical input triggers the item-lookup hotkey - either a
// keyboard key (fires on key-down) or a mouse button (fires on button-down),
// each optionally requiring a modifier key held at the same time. Default is
// Alt+LeftClick, matching the app's original hardcoded behavior.
public enum InputKind
{
    Keyboard,
    MouseButton,
}

// Mirrors the small subset of mouse buttons GlobalInputHook actually
// distinguishes (left/right/middle) - not System.Windows.Input.MouseButton,
// since this needs to serialize cleanly and match Win32 message codes, not
// WPF's own enum.
public enum MouseButton
{
    Left,
    Right,
    Middle,
}

// A single configured trigger: either a keyboard VK code or a mouse button,
// plus an optional modifier (Alt/Ctrl/Shift/none). Immutable/record so it
// can be compared and persisted directly.
public readonly record struct InputBinding(InputKind Kind, int KeyCode, MouseButton MouseButtonValue, ModifierKey Modifier)
{
    public static readonly InputBinding Default = new(InputKind.MouseButton, 0, MouseButton.Left, ModifierKey.Alt);

    public override string ToString()
    {
        var modifierPrefix = Modifier switch
        {
            ModifierKey.Alt => "Alt+",
            ModifierKey.Ctrl => "Ctrl+",
            ModifierKey.Shift => "Shift+",
            _ => "",
        };

        var inputName = Kind == InputKind.MouseButton
            ? MouseButtonValue switch
            {
                MouseButton.Left => "Left Click",
                MouseButton.Right => "Right Click",
                MouseButton.Middle => "Middle Click",
                _ => "Click",
            }
            : KeyCodeToDisplayName(KeyCode);

        return modifierPrefix + inputName;
    }

    // Covers the practical range a user would actually pick for a hotkey
    // (letters, digits, function keys) - falls back to a raw VK code label
    // for anything else rather than failing to display a name at all.
    private static string KeyCodeToDisplayName(int vkCode) => vkCode switch
    {
        >= 0x70 and <= 0x87 => $"F{vkCode - 0x6F}", // F1-F24
        >= 0x30 and <= 0x39 => ((char)vkCode).ToString(), // '0'-'9'
        >= 0x41 and <= 0x5A => ((char)vkCode).ToString(), // 'A'-'Z'
        0x20 => "Space",
        0x1B => "Escape",
        0x09 => "Tab",
        0x2E => "Delete",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "Page Up",
        0x22 => "Page Down",
        // Defensive: a bare modifier key should never actually reach here
        // (GlobalInputHook filters it out before capturing a binding), but
        // covering it means a settings.json saved before that fix still
        // displays sensibly instead of the meaningless raw VK code that
        // was shown before this fix ("Key(164)" for a left-Alt press).
        0x12 or 0xA4 or 0xA5 => "Alt",
        0x11 or 0xA2 or 0xA3 => "Ctrl",
        0x10 or 0xA0 or 0xA1 => "Shift",
        _ => $"Key({vkCode})",
    };
}

public enum ModifierKey
{
    None,
    Alt,
    Ctrl,
    Shift,
}
