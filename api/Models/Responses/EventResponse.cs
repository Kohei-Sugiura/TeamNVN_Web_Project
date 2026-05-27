namespace Weepa.Api.Models.Responses;

public sealed class EventResponse
{
    public int WeepaEventId { get; init; }
    public int WeepaSongId { get; init; }
    public string Song { get; init; } = string.Empty;
    public string Effect { get; init; } = string.Empty;
    public DateTimeOffset StartDate { get; init; }
    public DateTimeOffset EndDate { get; init; }
    public DateTimeOffset? TalliedAt { get; init; }
    public string SubmitName { get; init; } = string.Empty;
    public string? SubmitReason { get; init; }
    /// <summary>EX SCORE の MAX 値 (未確定なら null)。ランキング表示の "MAX-N" / "%" 計算に使う。</summary>
    public int? ExScoreMax { get; init; }
}
