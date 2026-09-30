namespace Argus.Modules.Preview;

public enum MouseButtonKind { None = 0, Middle = 1, X1 = 2, X2 = 3 }

[Flags]
public enum Mods { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }

/// <summary>단축키 하나: 키보드 키 또는 마우스 버튼(가운데·옆 버튼) + 보조키.</summary>
public sealed class HotkeyTrigger
{
    public int Key { get; set; }                       // 가상 키 코드. 마우스 트리거면 0
    public MouseButtonKind Mouse { get; set; }
    public Mods Mods { get; set; }

    public bool IsEmpty => Key == 0 && Mouse == MouseButtonKind.None;
    public bool IsMouse => Mouse != MouseButtonKind.None;

    /// <summary>보조키를 뺀 키(또는 버튼)가 같은가.</summary>
    public bool SameBase(HotkeyTrigger o) => Key == o.Key && Mouse == o.Mouse;
    public bool Same(HotkeyTrigger o) => SameBase(o) && Mods == o.Mods;
    public HotkeyTrigger Clone() => new() { Key = Key, Mouse = Mouse, Mods = Mods };

    public static int ModCount(Mods m) => System.Numerics.BitOperations.PopCount((uint)m);

    public override string ToString()
    {
        if (IsEmpty) return "지정 안 됨";
        var parts = new List<string>();
        if (Mods.HasFlag(Mods.Ctrl)) parts.Add("Ctrl");
        if (Mods.HasFlag(Mods.Alt)) parts.Add("Alt");
        if (Mods.HasFlag(Mods.Shift)) parts.Add("Shift");
        if (Mods.HasFlag(Mods.Win)) parts.Add("Win");
        parts.Add(Mouse switch
        {
            MouseButtonKind.Middle => "마우스 가운데",
            MouseButtonKind.X1 => "마우스 뒤로(옆 1)",
            MouseButtonKind.X2 => "마우스 앞으로(옆 2)",
            _ => KeyName(Key),
        });
        return string.Join(" + ", parts);
    }

    private static string KeyName(int vk)
    {
        var s = System.Windows.Input.KeyInterop.KeyFromVirtualKey(vk).ToString();
        if (s.Length == 2 && s[0] == 'D' && char.IsDigit(s[1])) return s[1].ToString();
        return s == "None" ? $"0x{vk:X2}" : s;
    }
}

public sealed class HotkeyBinding
{
    /// <summary><see cref="HotkeyActions"/> 참고.</summary>
    public string Action { get; set; } = "";
    public HotkeyTrigger Trigger { get; set; } = new();
}

/// <summary>동작 이름: "cycle:next", "cycle:prev", "jump:캐릭터".</summary>
public static class HotkeyActions
{
    public const string Next = "cycle:next", Prev = "cycle:prev", Surge = "surge", JumpPrefix = "jump:";
    public static string Jump(string character) => JumpPrefix + character;
}
