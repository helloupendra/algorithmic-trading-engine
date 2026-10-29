namespace AlgoTrading.Application.Exceptions;

/// <summary>
/// A vendor call needs a signed-in session and the vendor has none today:
/// nothing is wrong with the request or the server, someone has to sign in.
/// </summary>
/// <remarks>
/// An <see cref="InvalidOperationException"/>, so the endpoints that already
/// answer one with 400 and its message keep doing so. Those that know the
/// difference answer 409 with the message, which says where to sign in. Until
/// 29 Oct 2026 <c>POST /api/Backfill/history</c> let the FYERS one escape and
/// the console showed "An unexpected error occurred".
/// </remarks>
public class VendorNotConnectedException : InvalidOperationException
{
    public VendorNotConnectedException(string providerKey, string message) : base(message)
    {
        ProviderKey = providerKey;
    }

    /// <summary>The connector with no session, e.g. "fyers".</summary>
    public string ProviderKey { get; }
}
