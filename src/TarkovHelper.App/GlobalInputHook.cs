using System.Runtime.InteropServices;

namespace TarkovHelper.App;

// Low-level global keyboard + mouse hooks (WH_KEYBOARD_LL / WH_MOUSE_LL)
// used for two purposes: (1) firing TriggerActivated when the user's
// currently configured InputBinding occurs anywhere on the desktop, so item
// lookup works while EFT has focus - a WPF-level input handler only fires
// within this app's own window; (2) a one-shot "capture the next key or
// click" mode used by the rebind UI, so the user can press literally
// whatever they want to bind rather than picking from a fixed list.
//
// Passive only: inspects events to check whether they match, never blocks,
// modifies, or injects input (CallNextHookEx always runs, hook return value
// never overridden), so normal keyboard/mouse use elsewhere is unaffected
// beyond this one extra observer. Same mechanism RatScanner itself uses
// (SetWindowsHookEx) for its own click trigger.
//
// Real bug the original GlobalMouseHook's design was built around: reacting
// to an already-hovering cursor let EFT's tooltip fully render/settle
// exactly where it's most likely to overlap the hovered cell, corrupting
// the grid-detection/template-match crop. Firing on key-down/button-down
// (not on release, not on hover) keeps that same "capture before the
// tooltip renders" timing regardless of which physical input is configured.
public sealed class GlobalInputHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;

    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    // GetAsyncKeyState reads TRUE global keyboard state via Win32 directly,
    // unlike System.Windows.Input.Keyboard.Modifiers which is WPF's own
    // input-system view and isn't guaranteed to reflect key state while a
    // different process (EFT) has focus.
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VkMenu = 0x12; // Alt (generic/either-side code)
    private const int VkControl = 0x11; // Ctrl (generic/either-side code)
    private const int VkShift = 0x10; // Shift (generic/either-side code)

    // Real bug: SetWindowsHookEx's WH_KEYBOARD_LL callback reports the
    // SIDE-SPECIFIC virtual key code for modifier keys (VK_LMENU/VK_RMENU
    // etc.), never the generic VK_MENU/VK_CONTROL/VK_SHIFT codes above -
    // confirmed directly (pressing Alt alone during capture mode produced
    // vkCode 164 = VK_LMENU, not 0x12). The "is this key itself just a
    // bare modifier, not a real bindable input" check only excluded the
    // generic codes, so a bare Alt/Ctrl/Shift press was never recognized
    // as a modifier and got wrongly captured as the whole keybind - fixed
    // by checking every side-specific code too, not just the generic ones.
    private const int VkLMenu = 0xA4;
    private const int VkRMenu = 0xA5;
    private const int VkLControl = 0xA2;
    private const int VkRControl = 0xA3;
    private const int VkLShift = 0xA0;
    private const int VkRShift = 0xA1;

    private static bool IsModifierKeyCode(int vkCode) => vkCode is
        VkMenu or VkLMenu or VkRMenu or
        VkControl or VkLControl or VkRControl or
        VkShift or VkLShift or VkRShift;

    private static ModifierKey CurrentModifier()
    {
        if ((GetAsyncKeyState(VkMenu) & 0x8000) != 0)
        {
            return ModifierKey.Alt;
        }

        if ((GetAsyncKeyState(VkControl) & 0x8000) != 0)
        {
            return ModifierKey.Ctrl;
        }

        if ((GetAsyncKeyState(VkShift) & 0x8000) != 0)
        {
            return ModifierKey.Shift;
        }

        return ModifierKey.None;
    }

    // Kept as fields (not locals) so the delegates aren't garbage collected
    // while the unmanaged hooks still hold references to them - a real,
    // well-known pitfall with SetWindowsHookEx in .NET.
    private readonly LowLevelProc _keyboardProc;
    private readonly LowLevelProc _mouseProc;
    private readonly IntPtr _keyboardHookHandle;
    private readonly IntPtr _mouseHookHandle;

    public event EventHandler? TriggerActivated;

    // Set while the rebind UI is waiting for the user's next input - when
    // non-null, the NEXT key-down or button-down is captured into this
    // callback instead of being checked against Binding, and capture mode
    // turns itself off immediately after (one-shot).
    private Action<InputBinding>? _captureCallback;

    public InputBinding Binding { get; set; } = InputBinding.Default;

    public GlobalInputHook()
    {
        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        var moduleHandle = GetModuleHandle(curModule.ModuleName!);
        _keyboardHookHandle = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, moduleHandle, 0);
        _mouseHookHandle = SetWindowsHookEx(WhMouseLl, _mouseProc, moduleHandle, 0);
    }

    public bool IsInstalled => _keyboardHookHandle != IntPtr.Zero && _mouseHookHandle != IntPtr.Zero;

    // Arms one-shot capture mode: the next key or mouse-button press
    // anywhere on the desktop is reported via callback as a new
    // InputBinding (with whatever modifier was held at that instant),
    // instead of being checked against the currently configured Binding.
    // Used by the rebind button - "click a button, it looks out for your
    // next input, that becomes your keybind."
    public void CaptureNextInput(Action<InputBinding> callback) => _captureCallback = callback;

    public void CancelCapture() => _captureCallback = null;

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam.ToInt32() == WmKeyDown || wParam.ToInt32() == WmSysKeyDown))
        {
            var vkCode = Marshal.ReadInt32(lParam);

            // A bare modifier key press (Alt/Ctrl/Shift alone) is never
            // itself a valid trigger or capturable bind - it's the
            // modifier FOR a following key/click, not an input in its own
            // right. Skipping it here means pressing Alt on its own
            // during capture mode doesn't immediately (and wrongly) bind
            // "Alt" as the whole keybind.
            if (!IsModifierKeyCode(vkCode))
            {
                if (_captureCallback is { } callback)
                {
                    var captured = new InputBinding(InputKind.Keyboard, vkCode, MouseButton.Left, CurrentModifier());
                    _captureCallback = null;
                    callback(captured);
                }
                else if (Binding.Kind == InputKind.Keyboard && Binding.KeyCode == vkCode && Binding.Modifier == CurrentModifier())
                {
                    TriggerActivated?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            MouseButton? button = message switch
            {
                WmLButtonDown => MouseButton.Left,
                WmRButtonDown => MouseButton.Right,
                WmMButtonDown => MouseButton.Middle,
                _ => null,
            };

            if (button is { } clickedButton)
            {
                if (_captureCallback is { } callback)
                {
                    var captured = new InputBinding(InputKind.MouseButton, 0, clickedButton, CurrentModifier());
                    _captureCallback = null;
                    callback(captured);
                }
                else if (Binding.Kind == InputKind.MouseButton && Binding.MouseButtonValue == clickedButton && Binding.Modifier == CurrentModifier())
                {
                    TriggerActivated?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        // Always pass the event through unchanged - this hook only
        // observes, it must never consume or alter real input.
        return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_keyboardHookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHookHandle);
        }

        if (_mouseHookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHookHandle);
        }
    }
}
