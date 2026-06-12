namespace Weepa.Api.Models.Responses;

public sealed class RankingEntry
{
    public int Rank { get; init; }
    public string XId { get; init; } = string.Empty;
    public string SubmitName { get; init; } = string.Empty;
    public int Score { get; init; }
    public int? ExScore { get; init; }
    // 所属部門 (最後の投稿の tier): 1=ストイック / 2=チャレンジ / 3=カジュアル
    public int Tier { get; init; }
    // 最初の投稿のスコア (カジュアル部門の自己ベスト更新幅 = Score - FirstScore)
    public int FirstScore { get; init; }
    public DateTimeOffset FirstSubmitDate { get; init; }
    public DateTimeOffset SubmitDate { get; init; }
    public DateTimeOffset LastSubmitDate { get; init; }
    public int UpdateCount { get; init; }
}
