using System.Globalization;

namespace dockdev.Services.Tools;

/// <summary>Number Base's engine (design doc §14.15): decimal/hex/binary/octal, width-aware
/// signed/unsigned interpretation, and bitwise operations.</summary>
public static class NumberBaseTools
{
    public static bool TryParse(string text, int fromBase, out long value)
    {
        value = 0;
        text = text.Trim();
        if (text.Length == 0)
            return false;
        bool negative = text.StartsWith('-');
        if (negative)
            text = text[1..];
        text = text.Replace("_", "");
        try
        {
            value = fromBase switch
            {
                16 => Convert.ToInt64(text, 16),
                8 => Convert.ToInt64(text, 8),
                2 => Convert.ToInt64(text, 2),
                _ => long.Parse(text, CultureInfo.InvariantCulture),
            };
            if (negative)
                value = -value;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string ToBase(long value, int toBase, int width, bool signed)
    {
        ulong bits = Mask(value, width);
        return toBase switch
        {
            16 => bits.ToString("x").TrimStart('0').PadLeft(1, '0'),
            8 => System.Convert.ToString((long)bits, 8),
            2 => System.Convert.ToString((long)bits, 2),
            _ => signed ? ((long)SignExtend(bits, width)).ToString() : bits.ToString(),
        };
    }

    /// <summary>Masks a value to the given bit width (8/16/32/64), truncating like real integer
    /// arithmetic would.</summary>
    public static ulong Mask(long value, int width)
    {
        ulong bits = unchecked((ulong)value);
        return width switch
        {
            8 => bits & 0xFF,
            16 => bits & 0xFFFF,
            32 => bits & 0xFFFFFFFF,
            _ => bits,
        };
    }

    public static long SignExtend(ulong bits, int width)
    {
        return width switch
        {
            8 => (sbyte)bits,
            16 => (short)bits,
            32 => (int)bits,
            _ => unchecked((long)bits),
        };
    }

    public static ulong And(ulong a, ulong b) => a & b;
    public static ulong Or(ulong a, ulong b) => a | b;
    public static ulong Xor(ulong a, ulong b) => a ^ b;
    public static ulong Not(ulong a, int width) => ~a & WidthMask(width);
    public static ulong ShiftLeft(ulong a, int n, int width) => (a << n) & WidthMask(width);
    public static ulong ShiftRight(ulong a, int n) => a >> n;

    public static ulong WidthMask(int width) => width switch
    {
        8 => 0xFF,
        16 => 0xFFFF,
        32 => 0xFFFFFFFF,
        _ => 0xFFFFFFFFFFFFFFFF,
    };
}
