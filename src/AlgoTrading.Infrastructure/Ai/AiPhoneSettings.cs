namespace AlgoTrading.Infrastructure.Ai;

/// <summary>
/// The <c>Ai:Phone</c> section: "Hey Siri, Ira". An Apple Shortcut on the owner's phone asks the Desk Assistant through
/// <c>POST api/Ira/ask</c> with a phone token made on the console, and speaks the short answer back.
/// </summary>
public sealed class AiPhoneSettings
{
    /// <summary>The desk-wide switch: off, <c>POST api/Ira/ask</c> answers 503 and no new token can be made.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Phone tokens one admin may hold at once; revoke one to make another.</summary>
    public int MaxTokensPerUser { get; set; } = 5;

    /// <summary>The longest question a phone may send, in characters (a dictated question is a sentence or two).</summary>
    public int MaxQuestionChars { get; set; } = 2_000;
}
