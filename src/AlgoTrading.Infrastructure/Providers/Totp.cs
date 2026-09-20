using System.Security.Cryptography;

namespace AlgoTrading.Infrastructure.Providers;

/// <summary>
/// The six-digit time-based code (RFC 6238) that a broker's daily two-factor
/// login asks for: 30-second steps, SHA-1, the last four bytes reduced.
/// </summary>
/// <remarks>
/// Shared by every connector that signs in this way — Angel One's SmartAPI and
/// the simulated OpenFNO broker — because two copies of twenty lines of
/// cryptography drift, and a drift here reads as "wrong password" at 09:10.
/// Written out rather than taken from a package: the Python engine carries the
/// same thing for its own probe, and both are tested against the RFC's vectors.
/// </remarks>
public static class Totp
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

    /// <summary>
    /// Secrets are shown unpadded and in groups of four; both spellings, and a
    /// padded one, are accepted. <paramref name="settingName"/> names the
    /// setting an operator would correct, so a bad secret says where to look.
    /// </summary>
    public static byte[] FromBase32(string secret, string settingName = "the TOTP secret")
    {
        string text = (secret ?? string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty).TrimEnd('=').ToUpperInvariant();
        if (text.Length == 0) throw new ArgumentException($"The TOTP secret is empty; set {settingName}.", nameof(secret));

        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (char c in text)
        {
            int value = alphabet.IndexOf(c);
            if (value < 0) throw new ArgumentException($"'{c}' is not base32; check {settingName}.", nameof(secret));
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits < 8) continue;
            bits -= 8;
            bytes.Add((byte)((buffer >> bits) & 0xFF));
        }
        return bytes.ToArray();
    }
}
