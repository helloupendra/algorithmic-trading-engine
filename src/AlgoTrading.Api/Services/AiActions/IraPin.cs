using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AlgoTrading.Api.Services.AiActions;

/// <summary>
/// The owner's action PIN: 4 to 8 digits, kept only as a salted PBKDF2-SHA256 hash
/// (<c>pbkdf2-sha256$iterations$salt$hash</c>, base64). The PIN itself is never stored, logged or sent to a model.
/// </summary>
public static partial class IraPin
{
    /// <summary>OWASP's 2023 figure for PBKDF2-HMAC-SHA256. Tests lower it; a stored hash keeps its own count.</summary>
    internal static int Iterations { get; set; } = 600_000;

    private const string Scheme = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>Whether <paramref name="text"/> is a PIN's shape: 4 to 8 digits and nothing else.</summary>
    public static bool IsShape(string? text) => text is not null && Shape().IsMatch(text);

    /// <summary>A new salted hash of <paramref name="pin"/>, to store.</summary>
    public static string Hash(string pin)
    {
        if (!IsShape(pin)) throw new ArgumentException("A PIN is 4 to 8 digits.", nameof(pin));
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        int iterations = Iterations;
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Join('$', Scheme, iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    /// <summary>Whether <paramref name="pin"/> is the PIN <paramref name="stored"/> was made from; false for a hash it cannot read.</summary>
    public static bool Verify(string? pin, string? stored)
    {
        if (!IsShape(pin) || string.IsNullOrEmpty(stored)) return false;
        string[] parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int iterations) || iterations < 1) return false;
        try
        {
            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] expected = Convert.FromBase64String(parts[3]);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin!), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^[0-9]{4,8}$", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();
}

/// <summary>
/// Wrong PINs per owner: three within <see cref="Window"/> lock his actions for <see cref="LockFor"/>. In memory, as the
/// AI rate limit is: a restart forgets a lock, which only an API restart (not a guesser) can cause.
/// </summary>
public sealed class IraPinGuard(TimeProvider? time = null)
{
    public const int MaxWrong = 3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<long, State> _users = new();

    private sealed class State
    {
        public readonly List<DateTime> Wrong = [];
        public DateTime? LockedUntilUtc;
    }

    /// <summary>When <paramref name="userId"/>'s actions unlock; null while they are not locked.</summary>
    public DateTime? LockedUntil(long userId)
    {
        if (!_users.TryGetValue(userId, out var state)) return null;
        lock (state)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            if (state.LockedUntilUtc is DateTime until && until > now) return until;
            state.LockedUntilUtc = null;
            return null;
        }
    }

    /// <summary>A wrong PIN: how many tries are left before the lock, and the lock's end once it is on.</summary>
    public (int Left, DateTime? LockedUntilUtc) Wrong(long userId)
    {
        var state = _users.GetOrAdd(userId, _ => new State());
        lock (state)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            state.Wrong.RemoveAll(at => now - at >= Window);
            state.Wrong.Add(now);
            if (state.Wrong.Count < MaxWrong) return (MaxWrong - state.Wrong.Count, null);
            state.Wrong.Clear();
            state.LockedUntilUtc = now + LockFor;
            return (0, state.LockedUntilUtc);
        }
    }

    /// <summary>The right PIN: the wrong ones before it no longer count.</summary>
    public void Right(long userId)
    {
        if (!_users.TryGetValue(userId, out var state)) return;
        lock (state) state.Wrong.Clear();
    }
}
