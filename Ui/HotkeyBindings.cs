using System.Runtime.InteropServices;

internal enum ModifierSide { None, Left, Right, Either }

// Held sides of each modifier: bit 0 left, bit 1 right.
internal readonly record struct HeldModifiers(int Ctrl, int Alt, int Shift)
{
    internal bool Any => (Ctrl | Alt | Shift) != 0;
}

// The modifiers a hotkey requires. Matching is exact: a key set to None must not be held.
internal sealed record ModifierCombo
{
    public ModifierSide Ctrl { get; set; }
    public ModifierSide Alt { get; set; }
    public ModifierSide Shift { get; set; }

    internal bool IsEmpty => Ctrl == ModifierSide.None && Alt == ModifierSide.None && Shift == ModifierSide.None;

    internal bool Matches(HeldModifiers held) => Accepts(Ctrl, held.Ctrl) && Accepts(Alt, held.Alt) && Accepts(Shift, held.Shift);

    // Two combos overlap when some held state matches both, so one key press could mean either.
    internal bool Overlaps(ModifierCombo other) =>
        (States(Ctrl) & States(other.Ctrl)) != 0 && (States(Alt) & States(other.Alt)) != 0 && (States(Shift) & States(other.Shift)) != 0;

    internal string Label => string.Join("+", new[] { Part(Ctrl, "Ctrl"), Part(Alt, "Alt"), Part(Shift, "Shift") }.Where(p => p.Length > 0));

    internal static bool IsValid(ModifierCombo? value) => value is not null &&
        value.Ctrl is >= ModifierSide.None and <= ModifierSide.Either &&
        value.Alt is >= ModifierSide.None and <= ModifierSide.Either &&
        value.Shift is >= ModifierSide.None and <= ModifierSide.Either;

    private static bool Accepts(ModifierSide side, int held) => (States(side) & (1 << held)) != 0;

    // Bit n is set when the held state n (0 none, 1 left, 2 right, 3 both) is accepted.
    private static int States(ModifierSide side) => side switch
    {
        ModifierSide.None => 0b0001,
        ModifierSide.Left => 0b0010,
        ModifierSide.Right => 0b0100,
        _ => 0b1110
    };

    private static string Part(ModifierSide side, string name) => side switch
    {
        ModifierSide.None => "",
        ModifierSide.Left => "L" + name,
        ModifierSide.Right => "R" + name,
        _ => name
    };
}

// Local hotkey bindings (tipaura.settings.json). Keys[0] resets the encounter, Keys[1..9] are the slots
// that encounter files refer to. A key code is the virtual key, plus 0x100 for LLKHF_EXTENDED.
internal sealed record HotkeyBindings
{
    internal const int ResetAllIndex = 0, KeyCount = 10, Extended = 0x100;
    private static readonly int[] DefaultKeys = [0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69];

    public int[] Keys { get; set; } = [.. DefaultKeys];
    public ModifierCombo StartModifiers { get; set; } = new();
    // May be empty while the start modifier is not: the bare keys then reset.
    public ModifierCombo ResetModifiers { get; set; } = new() { Alt = ModifierSide.Either };

    internal int IndexOf(int code) => Array.IndexOf(Keys, code);

    public bool Equals(HotkeyBindings? other) => other is not null && Keys.AsSpan().SequenceEqual(other.Keys) &&
        StartModifiers == other.StartModifiers && ResetModifiers == other.ResetModifiers;

    public override int GetHashCode() => HashCode.Combine(Keys.Length, StartModifiers, ResetModifiers);

    internal HotkeyBindings WithKey(int index, int code)
    {
        int[] keys = [.. Keys];
        keys[index] = code;
        return this with { Keys = keys };
    }

    internal static HotkeyBindings Validate(HotkeyBindings? value)
    {
        var defaults = new HotkeyBindings();
        if (value is null) return defaults;
        bool keysValid = value.Keys is { Length: KeyCount } keys && keys.All(IsBindable) && keys.Distinct().Count() == KeyCount;
        var start = ModifierCombo.IsValid(value.StartModifiers) ? value.StartModifiers : defaults.StartModifiers;
        var reset = ModifierCombo.IsValid(value.ResetModifiers) ? value.ResetModifiers : defaults.ResetModifiers;
        if (start.Overlaps(reset)) (start, reset) = (defaults.StartModifiers, defaults.ResetModifiers);
        return value with { Keys = keysValid ? [.. value.Keys] : defaults.Keys, StartModifiers = start, ResetModifiers = reset };
    }

    internal static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or (>= 0xA0 and <= 0xA5) or 0x5B or 0x5C;

    // Any normalized keyboard key except modifiers, Win, Esc (cancels capture) and NumLock (changes the keypad).
    internal static bool IsBindable(int code)
    {
        int vk = code & 0xFF;
        return (code & ~0x1FF) == 0 && vk is >= 0x08 and <= 0xFE && !IsModifier(vk) && vk is not (0x1B or 0x90) &&
            Normalize(vk, (code & Extended) != 0) == code;
    }

    // With NumLock off the keypad reports navigation keys without the extended flag; the dedicated navigation
    // cluster sets it. Both NumLock states of a keypad digit therefore become the Numpad virtual key.
    internal static int Normalize(int vk, bool extended) => vk switch
    {
        >= 0x60 and <= 0x69 => vk,
        _ when extended => vk | Extended,
        0x2D => 0x60, 0x23 => 0x61, 0x28 => 0x62, 0x22 => 0x63, 0x25 => 0x64,
        0x0C => 0x65, 0x27 => 0x66, 0x24 => 0x67, 0x26 => 0x68, 0x21 => 0x69,
        _ => vk
    };

    private static readonly Dictionary<int, string> Names = [];

    // UI thread only. The keyboard layout's name for the key, e.g. "Num5", "F1", "Insert".
    internal static unsafe string KeyName(int code)
    {
        if (Names.TryGetValue(code, out var cached)) return cached;
        int vk = code & 0xFF;
        string? name = vk is >= 0x60 and <= 0x69 ? $"Num{vk - 0x60}" : null;
        if (name is null)
        {
            uint scan = MapVirtualKeyW((uint)vk, 0);
            char* buffer = stackalloc char[64];
            int length = scan == 0 ? 0 : GetKeyNameTextW((int)(scan << 16 | ((code & Extended) != 0 ? 1u << 24 : 0)), buffer, 64);
            name = length > 0 ? new string(buffer, 0, length)
                : vk is >= 0x70 and <= 0x87 ? $"F{vk - 0x6F}" : $"0x{vk:X2}";
        }
        Names[code] = name;
        return name;
    }

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint type);
    [DllImport("user32.dll")]
    private static extern unsafe int GetKeyNameTextW(int lParam, char* buffer, int size);
}
