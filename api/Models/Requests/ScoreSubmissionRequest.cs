using System.ComponentModel.DataAnnotations;

namespace Weepa.Api.Models.Requests;

public sealed class ScoreSubmissionRequest
{
    [Required(ErrorMessage = "X IDは必須です")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "X IDを入力してください")]
    public string XId { get; set; } = string.Empty;

    [Required(ErrorMessage = "投稿者名は必須です")]
    [StringLength(50, MinimumLength = 1, ErrorMessage = "投稿者名は1〜50文字で入力してください")]
    public string SubmitName { get; set; } = string.Empty;

    [Required(ErrorMessage = "スコアは必須です")]
    [Range(0, 10_000_000, ErrorMessage = "スコアは 0 〜 10,000,000 の範囲で入力してください")]
    public int Score { get; set; }

    [Range(0, int.MaxValue, ErrorMessage = "EXスコアは 0 以上の整数で入力してください")]
    public int? ExScore { get; set; }

    [Required(ErrorMessage = "リザルト画像は必須です")]
    public IFormFile Image { get; set; } = null!;
}
