namespace dockdev.Services.Tools;

public enum ImageKind { None, Png, Jpeg, Gif, Bmp, WebP }

public static class Base64Tools
{
    public static string Encode(byte[] bytes, bool urlSafe, bool mime76)
    {
        var s = Convert.ToBase64String(bytes);
        if (urlSafe)
            s = s.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        if (mime76)
            s = string.Join("\n", Chunk(s, 76));
        return s;
    }

    /// <summary>Decodes Base64 (standard or URL-safe). Never throws — a decode failure returns
    /// false with a message, and never silently truncates (design doc §14.5).</summary>
    public static bool TryDecode(string text, bool strict, out byte[] bytes, out string error)
    {
        error = "";
        bytes = [];
        var cleaned = strict ? text : new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        cleaned = cleaned.Replace('-', '+').Replace('_', '/');

        int mod = cleaned.Length % 4;
        if (mod == 2)
            cleaned += "==";
        else if (mod == 3)
            cleaned += "=";
        else if (mod == 1)
        {
            error = "Invalid Base64: length cannot be 4n+1.";
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(cleaned);
            return true;
        }
        catch (FormatException ex)
        {
            error = "Invalid Base64: " + ex.Message;
            return false;
        }
    }

    public static ImageKind SniffImage(byte[] bytes)
    {
        if (bytes.Length < 4)
            return ImageKind.None;
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return ImageKind.Png;
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return ImageKind.Jpeg;
        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
            return ImageKind.Gif;
        if (bytes[0] == 0x42 && bytes[1] == 0x4D)
            return ImageKind.Bmp;
        if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            return ImageKind.WebP;
        return ImageKind.None;
    }

    private static IEnumerable<string> Chunk(string s, int size)
    {
        for (int i = 0; i < s.Length; i += size)
            yield return s.Substring(i, Math.Min(size, s.Length - i));
    }
}
