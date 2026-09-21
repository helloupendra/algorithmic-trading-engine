namespace AlgoTrading.Domain.Entities;

/// <summary>
/// A trader's account at the simulated broker: which account on the broker is
/// theirs, and the credentials their app signs in with.
/// </summary>
/// <remarks>
/// <para>
/// The broker shows the TOTP secret when the account is opened and the app
/// secret when the app is issued, and never shows either again. They are stored
/// here, encrypted, because the alternative is a trader who can never sign in
/// again and an account that has to be reopened from scratch.
/// </para>
/// <para>
/// The row is the platform's record of the link, not the account itself: the
/// money, the orders and the positions live at the broker, and are read from it
/// every time rather than copied here, so the two can never disagree.
/// </para>
/// </remarks>
public class SimBrokerAccount
{
    public long Id { get; set; }

    /// <summary>The trader this account belongs to. One account per trader.</summary>
    public long UserId { get; set; }

    /// <summary>The broker's own id for the account, for example <c>OFB00001</c>.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>The API app issued for this account.</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>The app secret, encrypted with ASP.NET Data Protection.</summary>
    public string AppSecretProtected { get; set; } = string.Empty;

    /// <summary>The base32 TOTP secret, encrypted the same way.</summary>
    public string TotpSecretProtected { get; set; } = string.Empty;

    /// <summary>The addresses the app may call from, as the broker was told them.</summary>
    public string StaticIps { get; set; } = string.Empty;

    /// <summary>
    /// A disabled link is not shown to the trader and is not used by the
    /// platform. The account at the broker is untouched by this: money and
    /// positions are not something a checkbox here should be able to destroy.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>The admin who issued it.</summary>
    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
