using System.Security.Cryptography;
using System.Text;

namespace dockdev.Services.Tools;

/// <summary>
/// Hash &amp; HMAC's engine (design doc §14.8): every algorithm computed simultaneously over text
/// or a file. MD5 and SHA-1 are checksums only — callers must label them "not secure" in the UI.
/// </summary>
public static class HashTools
{
    public static readonly string[] Algorithms = ["MD5", "SHA-1", "SHA-256", "SHA-384", "SHA-512", "CRC32"];

    public static string Compute(string algorithm, byte[] data) => algorithm switch
    {
        "MD5" => Hex(MD5.HashData(data)),
        "SHA-1" => Hex(SHA1.HashData(data)),
        "SHA-256" => Hex(SHA256.HashData(data)),
        "SHA-384" => Hex(SHA384.HashData(data)),
        "SHA-512" => Hex(SHA512.HashData(data)),
        "CRC32" => Crc32.Compute(data).ToString("x8"),
        _ => "",
    };

    /// <summary>
    /// The one-shot static <c>HashData(key, source)</c> overloads rather than
    /// <c>new HMACSHA256(key).ComputeHash(data)</c>: every <c>HMAC*</c> type is
    /// <see cref="IDisposable"/> and holds a native algorithm handle, and the instance form here
    /// was constructing five of them per keystroke and disposing none — the key material stayed
    /// pinned in each one until a finalizer got round to it. The static form allocates no
    /// disposable at all and zeroes its own working state.
    /// </summary>
    public static string ComputeHmac(string algorithm, byte[] data, byte[] key) => algorithm switch
    {
        "MD5" => Hex(HMACMD5.HashData(key, data)),
        "SHA-1" => Hex(HMACSHA1.HashData(key, data)),
        "SHA-256" => Hex(HMACSHA256.HashData(key, data)),
        "SHA-384" => Hex(HMACSHA384.HashData(key, data)),
        "SHA-512" => Hex(HMACSHA512.HashData(key, data)),
        _ => "", // CRC32 has no keyed variant
    };

    /// <summary>Constant-time comparison for the "compare to expected" match chip, so timing
    /// can't leak how many leading characters matched.</summary>
    public static bool ConstantTimeEquals(string a, string b)
    {
        var bytesA = Encoding.UTF8.GetBytes(a.Trim().ToLowerInvariant());
        var bytesB = Encoding.UTF8.GetBytes(b.Trim().ToLowerInvariant());
        if (bytesA.Length != bytesB.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}

/// <summary>Hand-rolled table-driven CRC32 (IEEE 802.3 polynomial) — twenty lines beats a NuGet
/// dependency for one checksum (design doc §6).</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        const uint poly = 0xEDB88320;
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? poly ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
