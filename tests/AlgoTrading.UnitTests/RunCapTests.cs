using AlgoTrading.Application.Risk;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The platform's cap on open strategy runs. It used to be a fixed 10, which
/// stopped the owner's own desk at ten runs; it is now opt-in.
/// </summary>
public class RunCapTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 10)]
    [InlineData(0, 250)]
    [InlineData(-1, 99)]
    public void No_cap_never_blocks(int cap, int open)
    {
        Assert.False(RunCap.Blocks(cap, open));
        Assert.Null(RunCap.Describe(cap));
    }

    [Theory]
    [InlineData(3, 2, false)]
    [InlineData(3, 3, true)]
    [InlineData(3, 4, true)]
    public void A_positive_cap_blocks_from_the_cap_onwards(int cap, int open, bool blocked)
    {
        Assert.Equal(blocked, RunCap.Blocks(cap, open));
        Assert.Equal(cap, RunCap.Describe(cap));
    }
}
