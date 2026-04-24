using System.ComponentModel.DataAnnotations;

namespace Weepa.Api.Models.Requests;

public sealed class ScoreApproveRequest
{
    [Range(0, 10_000_000, ErrorMessage = "修正後スコアは 0 〜 10,000,000 の範囲で入力してください")]
    public int? Score { get; set; }

    [Range(0, int.MaxValue, ErrorMessage = "修正後EXスコアは 0 以上の整数で入力してください")]
    public int? ExScore { get; set; }
}
