using System.Text.Json.Serialization;

namespace FluidHUD.Models;

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<HotkeyModifiers>))]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008
}

public sealed record HotkeyGesture
{
    public HotkeyModifiers Modifiers { get; init; } = HotkeyModifiers.Alt;

    /// <summary>A Win32 virtual-key code.</summary>
    public uint VirtualKey { get; init; } = 0x20; // Space

    [JsonIgnore]
    public bool IsValid => VirtualKey is > 0 and <= 0xFE;

    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            var parts = new List<string>(5);
            if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
            parts.Add(GetKeyName(VirtualKey));
            return string.Join(" + ", parts);
        }
    }

    public static string GetKeyName(uint key) => key switch
    {
        >= 0x30 and <= 0x39 => ((char)key).ToString(),
        >= 0x41 and <= 0x5A => ((char)key).ToString(),
        >= 0x70 and <= 0x87 => $"F{key - 0x6F}",
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x1B => "Esc",
        0x20 => "Space",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        0x2D => "Insert",
        0x2E => "Delete",
        0x6A => "Num *",
        0x6B => "Num +",
        0x6D => "Num −",
        0x6E => "Num .",
        0x6F => "Num /",
        0xBA => ";",
        0xBB => "+",
        0xBC => ",",
        0xBD => "−",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "~",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        _ => $"Key 0x{key:X2}"
    };
}
