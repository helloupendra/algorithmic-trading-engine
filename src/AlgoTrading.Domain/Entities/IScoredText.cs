namespace AlgoTrading.Domain.Entities;

/// <summary>
/// The six scoring columns a recorded headline or announcement carries. The
/// recorders leave them at their defaults; the Python scorer
/// (<c>analysis/news.py</c>, run as <c>python -m analysis news-score</c>) fills
/// them in place with its own SQL.
/// </summary>
/// <remarks>
/// The column names are a contract with that script
/// (<c>NewsTablesContractTests</c> reads it and checks every name it uses).
/// Every text column is NOT NULL with an empty default, so an insert that
/// names only the recorder's columns lands, and a row with
/// <see cref="ScoredUtc"/> null is one the scorer has not reached yet.
/// </remarks>
public interface IScoredText
{
    /// <summary>−1 (bearish) to 1 (bullish); null until scored.</summary>
    decimal? Sentiment { get; set; }

    /// <summary>0 (noise) to 3 (moves the market); null until scored.</summary>
    short? Importance { get; set; }

    /// <summary>NSE symbols the text is about, comma-separated ("RELIANCE,TCS"); empty when none or not scored.</summary>
    string Symbols { get; set; }

    /// <summary>The scorer's topic tags, comma-separated; empty when none or not scored.</summary>
    string Topics { get; set; }

    /// <summary>When the scorer wrote the four columns above; null means not scored yet.</summary>
    DateTime? ScoredUtc { get; set; }

    /// <summary>Which model scored it, with its version, so a re-score with a new model can be told apart.</summary>
    string ScoreModel { get; set; }
}
