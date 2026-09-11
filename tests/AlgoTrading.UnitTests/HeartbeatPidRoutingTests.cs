using System.Reflection;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.LiveData;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Entities;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// A feed's heartbeat records its process id under that feed, and nowhere else.
///
/// Before feeds were per vendor every heartbeat wrote "ingestor.pid". With FYERS
/// and TrueData both running, TrueData's pid overwrote FYERS's; after an API
/// restart the FYERS supervisor found a process that was not FYERS, dropped the
/// record, and showed a running feed as stopped — to Start, and to the
/// market-close stop.
/// </summary>
public class HeartbeatPidRoutingTests
{
    [Theory]
    [InlineData("fyers", "ingestor.pid")]
    [InlineData("truedata", "feed.truedata.pid")]
    [InlineData("TrueData", "feed.truedata.pid")]
    public async Task Each_feed_writes_its_own_pid_key(string feedKey, string expectedKey)
    {
        var store = new RecordingStore();
        var useCase = new UpsertHeartbeatUseCase(NoLiveData.Create(), store);

        await useCase.ExecuteAsync(new UpsertHeartbeatRequest { SourceName = "x", ProcessId = 4242, FeedKey = feedKey });

        Assert.Equal(new[] { (expectedKey, 4242) }, store.Writes);
    }

    [Fact]
    public async Task A_heartbeat_without_a_feed_key_is_the_fyers_ingestor_as_it_always_was()
    {
        var store = new RecordingStore();
        await new UpsertHeartbeatUseCase(NoLiveData.Create(), store)
            .ExecuteAsync(new UpsertHeartbeatRequest { SourceName = "python-live-ingestor", ProcessId = 7 });

        Assert.Equal(new[] { (SystemSettingKeys.IngestorPid, 7) }, store.Writes);
    }

    [Fact]
    public void The_supervisor_and_the_heartbeat_share_one_mapping()
    {
        Assert.Equal(SystemSettingKeys.IngestorPid, SystemSettingKeys.PidForFeed("fyers"));
        Assert.Equal(SystemSettingKeys.FeedPid("truedata"), SystemSettingKeys.PidForFeed("truedata"));
    }

    private sealed class RecordingStore : IProcessSettingsStore
    {
        public List<(string Key, int Pid)> Writes { get; } = new();
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<int?> GetPidAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<int?>(null);
        public Task SetAsync(string key, string value, string? updatedBy = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetPidAsync(string key, int processId, string? updatedBy = null, CancellationToken cancellationToken = default)
        {
            Writes.Add((key, processId));
            return Task.CompletedTask;
        }
        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> DeleteIfPidAsync(string key, int processId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    /// <summary>An ILiveDataService whose every call completes and does nothing.</summary>
    public class NoLiveData : DispatchProxy
    {
        public static ILiveDataService Create() => DispatchProxy.Create<ILiveDataService, NoLiveData>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var type = targetMethod!.ReturnType;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, new[] { value });
            }
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
