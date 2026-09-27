namespace AlgoTrading.Api.Configuration;

/// <summary>
/// Where the Desk finds what it reads from disk. Every value defaults to the
/// repository layout, so a fresh clone needs no configuration.
/// </summary>
public class DeskOptions
{
    public const string SectionName = "Desk";

    /// <summary>
    /// The morning plan. When empty, <c>config/morning-plan.txt</c> is looked
    /// for from the API's content root upwards (src/AlgoTrading.Api under a
    /// plain <c>dotnet run</c>, so ../../config). A relative path is taken from
    /// the content root.
    /// </summary>
    public string? PlanFile { get; set; }
}
