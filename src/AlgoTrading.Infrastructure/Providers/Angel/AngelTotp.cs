namespace AlgoTrading.Infrastructure.Providers.Angel;

/// <summary>
/// The six-digit code SmartAPI's login asks for, with Angel's setting name in
/// the error so a bad secret says where to correct it.
/// </summary>
/// <remarks>
/// The algorithm itself lives in <see cref="Providers.Totp"/>, shared with the
/// other connectors that sign in the same way.
/// </remarks>
public static class AngelTotp
{
    public static string Generate(string base32Secret, DateTimeOffset? at = null, int step = 30, int digits = 6)
    {
        // Validated here so the message names ANGEL_TOTP_SECRET, not "the TOTP secret".
        Totp.FromBase32(base32Secret, "ANGEL_TOTP_SECRET");
        return Totp.Generate(base32Secret, at, step, digits);
    }

    /// <summary>Angel shows the secret unpadded and in groups; both are accepted.</summary>
    public static byte[] FromBase32(string secret) => Totp.FromBase32(secret, "ANGEL_TOTP_SECRET");
}
