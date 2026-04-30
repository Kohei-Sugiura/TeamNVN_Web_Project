namespace Weepa.Api.Models.Responses;

public sealed class RankingEntry
{
    public int Rank { get; init; }
    public string XId { get; init; } = string.Empty;
    public string SubmitName { get; init; } = string.Empty;
    public int Score { get; init; }
    public int? ExScore { get; init; }
    public DateTimeOffset SubmitDate { get; init; }
    public DateTimeOffset LastSubmitDate { get; init; }
    public int UpdateCount { get; init; }
}
