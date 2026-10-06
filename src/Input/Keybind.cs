using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;

namespace MoogleMap.Input;

/// <summary>A key plus the modifiers that must be held with it. <see cref="VirtualKey.NO_KEY"/> means unbound.</summary>
[Serializable]
public sealed class Keybind
{
    public VirtualKey Key { get; set; } = VirtualKey.NO_KEY;
    public bool Ctrl { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }

    public bool IsBound => Key != VirtualKey.NO_KEY;

    public Keybind() { }

    public Keybind(VirtualKey key, bool ctrl = false, bool shift = false, bool alt = false)
    {
        Key = key;
        Ctrl = ctrl;
        Shift = shift;
        Alt = alt;
    }

    /// <summary>
    /// Held right now, with exactly these modifiers. Extra modifiers don't count, so Ctrl+M
    /// doesn't also fire a plain M bind.
    /// </summary>
    public bool IsHeld(Func<VirtualKey, bool> down)
    {
        if (!IsBound || !down(Key))
            return false;

        return down(VirtualKey.CONTROL) == Ctrl
               && down(VirtualKey.SHIFT) == Shift
               && down(VirtualKey.MENU) == Alt;
    }

    public override string ToString()
    {
        if (!IsBound) return "Not set";

        var sb = new StringBuilder();
        if (Ctrl) sb.Append("Ctrl + ");
        if (Shift) sb.Append("Shift + ");
        if (Alt) sb.Append("Alt + ");
        sb.Append(KeyName(Key));
        return sb.ToString();
    }

    public static string KeyName(VirtualKey key) => key switch
    {
        VirtualKey.OEM_1 => "; (OEM 1)",
        VirtualKey.OEM_2 => "/ (OEM 2)",
        VirtualKey.OEM_3 => "` (OEM 3)",
        VirtualKey.OEM_4 => "[ (OEM 4)",
        VirtualKey.OEM_5 => "\\ (OEM 5)",
        VirtualKey.OEM_6 => "] (OEM 6)",
        VirtualKey.OEM_7 => "' (OEM 7)",
        VirtualKey.OEM_PLUS => "+",
        VirtualKey.OEM_MINUS => "-",
        VirtualKey.OEM_COMMA => ",",
        VirtualKey.OEM_PERIOD => ".",
        VirtualKey.ADD => "Num +",
        VirtualKey.SUBTRACT => "Num -",
        VirtualKey.MULTIPLY => "Num *",
        VirtualKey.DIVIDE => "Num /",
        VirtualKey.PRIOR => "Page Up",
        VirtualKey.NEXT => "Page Down",
        VirtualKey.CAPITAL => "Caps Lock",
        VirtualKey.BACK => "Backspace",
        VirtualKey.RETURN => "Enter",
        _ => key.ToString().Replace("NUMPAD", "Num ").Replace('_', ' '),
    };

    public static bool IsModifier(VirtualKey key) => key is VirtualKey.CONTROL or VirtualKey.SHIFT or VirtualKey.MENU
        or VirtualKey.LCONTROL or VirtualKey.RCONTROL or VirtualKey.LSHIFT or VirtualKey.RSHIFT
        or VirtualKey.LMENU or VirtualKey.RMENU or VirtualKey.LWIN or VirtualKey.RWIN;

    /// <summary>Keys never offered for capture: mouse buttons, and Escape which cancels it.</summary>
    public static bool IsCapturable(VirtualKey key) => !IsModifier(key)
        && key is not (VirtualKey.NO_KEY or VirtualKey.ESCAPE or VirtualKey.LBUTTON or VirtualKey.RBUTTON
            or VirtualKey.MBUTTON or VirtualKey.XBUTTON1 or VirtualKey.XBUTTON2);

    public Keybind Clone() => new(Key, Ctrl, Shift, Alt);
}

/// <summary>
/// Turns key states into press edges, and keeps a pressed key from also reaching the game
/// when asked to.
/// </summary>
/// <remarks>
/// The physical state comes from Windows rather than from the game: once a key is hidden from
/// the game its state there reads up, and keyboard auto-repeat would then make every held key
/// look like a fresh press.
/// </remarks>
public sealed class KeyWatcher
{
    private readonly Dictionary<Keybind, bool> wasHeld = new(ReferenceEqualityComparer.Instance);
    private readonly Func<VirtualKey, bool> down = key => (GetAsyncKeyState((int)key) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>Keys pressed in another window must not open the map in the game.</summary>
    public static bool GameHasFocus => GetForegroundWindow() == Plugin.PluginInterface.UiBuilder.WindowHandlePtr;

    public bool IsDown(VirtualKey key) => down(key);

    /// <summary>True on the frame the bind goes down.</summary>
    public bool Pressed(IKeyState keys, Keybind bind, bool consume)
    {
        var held = Update(keys, bind, consume, out var before);
        return held && !before;
    }

    /// <summary>Whether the bind is down, for hold-to-show.</summary>
    public bool Held(IKeyState keys, Keybind bind, bool consume) => Update(keys, bind, consume, out _);

    private bool Update(IKeyState keys, Keybind bind, bool consume, out bool before)
    {
        var held = bind.IsHeld(down);
        wasHeld.TryGetValue(bind, out before);
        wasHeld[bind] = held;

        if (held && consume && keys.IsVirtualKeyValid(bind.Key))
            keys[bind.Key] = false;

        return held;
    }

    /// <summary>
    /// Marks every bind as already held, so one that is down when focus returns (or while the
    /// player was typing) doesn't fire until it is released and pressed again.
    /// </summary>
    public void Suppress(params Keybind[] binds)
    {
        foreach (var bind in binds)
            wasHeld[bind] = bind.IsHeld(down);
    }
}
