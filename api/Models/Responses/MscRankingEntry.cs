namespace Weepa.Api.Models.Responses;

public sealed class MscRankingEntry
{
    public int Rank { get; init; }
    public string SubmitName { get; init; } = string.Empty;
    public int TotalScore { get; init; }
    public DateTimeOffset SubmitDate { get; init; }
    public DateTimeOffset LastSubmitDate { get; init; }
    public int SubmissionCount { get; init; }
    public List<MscRankingPerSong> PerSong { get; set; } = new();
}

public sealed class MscRankingPerSong
{
    public int SongOrder { get; init; }
    public int Score { get; init; }
}
