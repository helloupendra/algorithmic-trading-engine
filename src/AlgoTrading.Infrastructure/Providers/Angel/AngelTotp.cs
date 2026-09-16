using System.Security.Cryptography;

namespace AlgoTrading.Infrastructure.Providers.Angel;

/// <summary>
/// The six-digit code SmartAPI's login asks for (RFC 6238).
/// </summary>
/// <remarks>
/// Written out rather than taken from a package: it is twenty lines, and the
/// Python engine carries the same thing for its own probe. Both are tested
/// against the RFC's vectors, so a mismatch between them would show up at once.
/// </remarks>
public static class AngelTotp
{
    public static string Generate(string base32Secret, DateTimeOffset? at = null, int step = 30, int digits = 6)
    {
        byte[] key = FromBase32(base32Secret);
        long counter = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / step;
        byte[] message = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(message);

        using var hmac = new HMACSHA1(key);
        byte[] hash = hmac.ComputeHash(message);
        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % (int)Math.Pow(10, digits)).ToString(new string('0', digits));
    }

    /// <summary>Angel shows the secret unpadded and in groups; both are accepted.</summary>
    public static byte[] FromBase32(string secret)
    {
        string text = (secret ?? string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty).TrimEnd('=').ToUpperInvariant();
        if (text.Length == 0) throw new ArgumentException("The TOTP secret is empty; set ANGEL_TOTP_SECRET.", nameof(secret));

        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (char c in text)
        {
            int value = alphabet.IndexOf(c);
            if (value < 0) throw new ArgumentException($"'{c}' is not base32; check ANGEL_TOTP_SECRET.", nameof(secret));
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits < 8) continue;
            bits -= 8;
            bytes.Add((byte)((buffer >> bits) & 0xFF));
        }
        return bytes.ToArray();
    }
}
