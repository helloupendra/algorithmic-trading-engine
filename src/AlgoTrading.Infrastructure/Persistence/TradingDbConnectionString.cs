using System.Data.Common;
using Npgsql;

namespace AlgoTrading.Infrastructure.Persistence;

/// <summary>
/// The TradingDb connection string as the API's pool uses it: capped at
/// <see cref="DefaultMaxPoolSize"/> connections unless the string says otherwise.
/// </summary>
/// <remarks>
/// Npgsql's own default is 100, which is the whole of Postgres here
/// (max_connections = 100 since 28 Sep). On 28 Sep a deploy overlapped the old
/// and the new API, each free to grow to 100, and the rest of the desk (feeds,
/// runners, the poller) was refused with 53300 "too many clients". At 40 two
/// overlapping APIs still leave room for everything else. An explicit
/// "Maximum Pool Size" in the connection string wins, so the server can raise
/// or lower it in appsettings.Local.json without a build.
/// </remarks>
public static class TradingDbConnectionString
{
    public const int DefaultMaxPoolSize = 40;

    // Every spelling Npgsql accepts for the setting, compared without spaces or case.
    private static readonly string[] PoolSizeKeys = { "maxpoolsize", "maximumpoolsize" };

    /// <summary>
    /// <paramref name="connectionString"/> with <c>Maximum Pool Size</c> set to
    /// <see cref="DefaultMaxPoolSize"/> when it does not set one itself; null or
    /// blank is returned as it is, for EF to report.
    /// </summary>
    public static string? WithPoolCap(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return connectionString;

        var raw = new DbConnectionStringBuilder { ConnectionString = connectionString };
        foreach (string key in raw.Keys)
        {
            if (PoolSizeKeys.Contains(key.Replace(" ", string.Empty).ToLowerInvariant()))
            {
                return connectionString;
            }
        }

        return new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = DefaultMaxPoolSize }.ConnectionString;
    }
}
