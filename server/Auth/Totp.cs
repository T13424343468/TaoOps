using System.Security.Cryptography;
using System.Text;

namespace TaoOps.Server.Auth;

/// <summary>
/// RFC 6238 TOTP 本地实现（HMAC-SHA1，6 位码，30s 周期），全程无外部依赖。
/// 时钟窗 ±1 周期；拒绝已使用的上一周期码（防重放）。
/// </summary>
public static class Totp
{
    public const int PeriodSeconds = 30;
    public const int Digits = 6;
    private const long Window = 1; // ±1 周期

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>生成随机密钥并编码为 Base32（默认 20 字节 = 160 位）。</summary>
    public static string GenerateSecret(int byteLen = 20)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteLen);
        return Base32Encode(bytes);
    }

    /// <summary>Base32 编码（RFC 4648，无填充）。</summary>
    public static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int bitBuffer = 0, bitCount = 0;
        foreach (var b in data)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bitCount += 8;
            while (bitCount >= 5)
            {
                sb.Append(Base32Alphabet[(bitBuffer >> (bitCount - 5)) & 31]);
                bitCount -= 5;
            }
        }
        if (bitCount > 0) sb.Append(Base32Alphabet[(bitBuffer << (5 - bitCount)) & 31]);
        return sb.ToString();
    }

    /// <summary>Base32 解码；非法字符返回 null。</summary>
    public static byte[]? Base32Decode(string encoded)
    {
        if (encoded.Length == 0) return null;
        var bits = new List<byte>();
        int bitBuffer = 0, bitCount = 0;
        foreach (var c in encoded.ToUpperInvariant())
        {
            var idx = Base32Alphabet.IndexOf(c);
            if (idx < 0) return null;
            bitBuffer = (bitBuffer << 5) | idx;
            bitCount += 5;
            if (bitCount >= 8)
            {
                bits.Add((byte)((bitBuffer >> (bitCount - 8)) & 0xFF));
                bitCount -= 8;
            }
        }
        return bits.ToArray();
    }

    /// <summary>otpauth:// URI（前端渲染二维码用）。</summary>
    public static string OtpauthUri(string secret, string username, string issuer = "TaoOps") =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(username)}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&digits={Digits}&period={PeriodSeconds}";

    /// <summary>
    /// 校验 6 位码：当前周期及前后各一（±1 窗口）任一匹配即通过；
    /// 匹配到比 lastUsedStep 更早（已使用）的周期视为重放拒绝。
    /// </summary>
    public static bool Verify(string secret, string code, long nowUnixSeconds, long lastUsedStep = -1)
    {
        if (code?.Length != Digits || !code.All(char.IsDigit)) return false;
        var key = Base32Decode(secret);
        if (key is null) return false;
        var currentStep = nowUnixSeconds / PeriodSeconds;
        for (var step = currentStep + Window; step >= currentStep - Window; step--)
        {
            if (step <= lastUsedStep) return false; // 重放：候选步不晚于已用步
            if (Compute(key, step) == code) return true;
        }
        return false;
    }

    /// <summary>返回当前匹配的步数（供记录 last_used_step）；不匹配返回 -1。</summary>
    public static long MatchStep(string secret, string code, long nowUnixSeconds, long lastUsedStep = -1)
    {
        if (code?.Length != Digits || !code.All(char.IsDigit)) return -1;
        var key = Base32Decode(secret);
        if (key is null) return -1;
        var currentStep = nowUnixSeconds / PeriodSeconds;
        for (var step = currentStep + Window; step >= currentStep - Window; step--)
        {
            if (step <= lastUsedStep) return -1;
            if (Compute(key, step) == code) return step;
        }
        return -1;
    }

    /// <summary>HMAC-SHA1 动态截断（RFC 4226 §5.3）。</summary>
    private static string Compute(byte[] key, long step)
    {
        var counter = new byte[8];
        var s = step;
        for (var i = 7; i >= 0; i--)
        {
            counter[i] = (byte)(s & 0xFF);
            s >>= 8;
        }
        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString($"D{Digits}");
    }
}
