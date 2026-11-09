using AlgoTrading.Infrastructure.Providers.Dhan;

namespace AlgoTrading.UnitTests;

public class DhanRateGateTests
{
    internal static DhanRateGate Gate(AdvancingClock clock) => new(
        Enum.GetValues<DhanRateClass>().ToDictionary(c => c, _ => TimeSpan.FromMilliseconds(300)), clock);

    [Fact]
    public async Task Delayed_send_and_wall_clock_jumps_cannot_shorten_the_gap()
    {
        var clock = new AdvancingClock();
        var gate = Gate(clock);
        long first = 0;
        await gate.SendAsync(DhanRateClass.OptionChain, () =>
        {
            clock.Advance(TimeSpan.FromSeconds(2)); // descheduled before reaching the transport
            first = clock.GetTimestamp();
            return Task.FromResult(1);
        }, default);
        clock.UtcOffset = TimeSpan.FromDays(1);
        await gate.SendAsync(DhanRateClass.OptionChain, () =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(300), clock.GetElapsedTime(first));
            return Task.FromResult(2);
        }, default);
        clock.UtcOffset = TimeSpan.FromDays(-1);
        await gate.SendAsync(DhanRateClass.OptionChain, () => Task.FromResult(3), default);
        Assert.Equal(TimeSpan.FromMilliseconds(600), clock.Waited);
    }

    [Fact]
    public async Task Concurrent_call_waits_for_the_in_flight_exchange_and_spacing()
    {
        var clock = new AdvancingClock();
        var gate = Gate(clock);
        var held = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = gate.SendAsync(DhanRateClass.OptionChain, () => held.Task, default);
        bool sent = false;
        var second = gate.SendAsync(DhanRateClass.OptionChain, () => { sent = true; return Task.FromResult(2); }, default);
        Assert.False(sent);
        clock.Advance(TimeSpan.FromSeconds(5));
        held.SetResult(1);
        await Task.WhenAll(first, second);
        Assert.True(sent);
        Assert.Equal(TimeSpan.FromMilliseconds(300), clock.Waited);
    }

    [Fact]
    public async Task Failed_exchange_consumes_a_slot_and_cancelled_waiter_does_not_send()
    {
        var clock = new AdvancingClock();
        var gate = Gate(clock);
        await Assert.ThrowsAsync<HttpRequestException>(() => gate.SendAsync<int>(DhanRateClass.OptionChain,
            () => throw new HttpRequestException("transport failed"), default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.SendAsync<int>(DhanRateClass.OptionChain,
            () => throw new Exception("must not send"), cancelled.Token));
        await gate.SendAsync(DhanRateClass.OptionChain, () => Task.FromResult(1), default);
        Assert.Equal(TimeSpan.FromMilliseconds(300), clock.Waited);
    }

    [Fact]
    public async Task Rate_classes_have_independent_slots()
    {
        var clock = new AdvancingClock();
        var gate = Gate(clock);
        var held = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chain = gate.SendAsync(DhanRateClass.OptionChain, () => held.Task, default);
        Assert.Equal(2, await gate.SendAsync(DhanRateClass.Quote, () => Task.FromResult(2), default));
        held.SetResult(1);
        await chain;
        Assert.Equal(TimeSpan.Zero, clock.Waited);
    }

    [Fact]
    public async Task Cancelling_a_queued_request_does_not_release_the_active_slot()
    {
        var clock = new AdvancingClock();
        var gate = Gate(clock);
        var held = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = gate.SendAsync(DhanRateClass.OptionChain, () => held.Task, default);
        using var cancel = new CancellationTokenSource();
        var queued = gate.SendAsync<int>(DhanRateClass.OptionChain,
            () => throw new Exception("must not send"), cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        var last = gate.SendAsync(DhanRateClass.OptionChain, () => Task.FromResult(3), default);
        Assert.False(last.IsCompleted);
        held.SetResult(1);
        await Task.WhenAll(first, last);
        Assert.Equal(TimeSpan.FromMilliseconds(300), clock.Waited);
    }

    internal sealed class AdvancingClock : TimeProvider
    {
        private long _ticks;
        public TimeSpan UtcOffset { get; set; }
        public TimeSpan Waited { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks) + UtcOffset;
        public void Advance(TimeSpan duration) => _ticks += duration.Ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                Advance(dueTime);
                Waited += dueTime;
                callback(state);
            }
            return new NoTimer();
        }
        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
