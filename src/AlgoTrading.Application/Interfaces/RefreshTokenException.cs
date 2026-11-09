namespace AlgoTrading.Application.Interfaces;

/// <summary>A refused refresh with a stable distinction between duplication and session revocation.</summary>
public sealed class RefreshTokenException(string code, string message, bool retryable = false) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}
