namespace Weepa.Api.Models.Requests;

public sealed class SongExMaxRequest
{
    /// <summary>EX SCORE の MAX 値。null を渡すと未確定状態に戻す。</summary>
    public int? ExScoreMax { get; init; }
}
