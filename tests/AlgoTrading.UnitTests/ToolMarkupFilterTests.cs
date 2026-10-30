using AlgoTrading.Infrastructure.Ai;

namespace AlgoTrading.UnitTests;

public class ToolMarkupFilterTests
{
    private static string Run(params string[] pieces)
    {
        var filter = new ToolMarkupFilter();
        return string.Concat(pieces.Select(filter.Feed)) + filter.Flush();
    }

    [Fact]
    public void Plain_answers_pass_through_whole_including_a_less_than_sign()
    {
        Assert.Equal("NIFTY < 24,000 today.", Run("NIFTY < 24,", "000 today."));
        Assert.Equal("Use <b>bold</b> sparingly", Run("Use <b>bold</b> sparingly"));
        Assert.Equal("ends with <", Run("ends with <"));
    }

    [Theory]
    [InlineData("मैं अभी पिछले 24 घंटे की खबरें देख सकता हूं:\n\n<｜DSML｜function_calls>\n<｜DSML｜invoke name=\"x\">")]
    [InlineData("I can look at the last 24 hours:\n\n< | DSML | function_calls")]
    public void Everything_from_deepseeks_call_marker_on_is_dropped(string answer)
    {
        string shown = Run(answer);
        Assert.DoesNotContain("DSML", shown);
        Assert.EndsWith(":", shown);
    }

    [Fact]
    public void A_marker_split_across_pieces_is_still_caught_and_nothing_after_it_shows()
    {
        // The line break before the marker went out before the marker was known; nothing after it does.
        Assert.Equal("Reading the news now.\n", Run("Reading the news now.\n<", "｜DS", "ML｜function_calls>", " more call text"));
    }

    [Fact]
    public void A_held_back_start_that_turns_out_to_be_text_is_given_back()
    {
        var filter = new ToolMarkupFilter();
        Assert.Equal("a ", filter.Feed("a <"));
        Assert.Equal("< 5 lakh", filter.Feed(" 5 lakh"));
        Assert.Equal(string.Empty, filter.Flush());
    }
}
