using System.ComponentModel.DataAnnotations;

namespace Weepa.Api.Models.Requests;

public sealed class SongSubmissionRequest
{
    [Required(ErrorMessage = "楽曲名は必須です")]
    [StringLength(200, MinimumLength = 1, ErrorMessage = "楽曲名は1〜200文字で入力してください")]
    public string Song { get; set; } = string.Empty;

    [Required(ErrorMessage = "譜面種は必須です")]
    [RegularExpression(
        @"^(NOV|ADV|EXH|MXM|INF|GRV|HVN|VVD|XCD|ULT)$",
        ErrorMessage = "譜面種は NOV/ADV/EXH/MXM/INF/GRV/HVN/VVD/XCD/ULT のいずれかです")]
    public string Effect { get; set; } = string.Empty;

    [Required(ErrorMessage = "X IDは必須です")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "X IDを入力してください")]
    public string XId { get; set; } = string.Empty;

    [Required(ErrorMessage = "表示名は必須です")]
    [StringLength(50, MinimumLength = 1, ErrorMessage = "表示名は1〜50文字で入力してください")]
    public string SubmitName { get; set; } = string.Empty;

    [StringLength(500, ErrorMessage = "選曲理由は500文字以内で入力してください")]
    public string? SubmitReason { get; set; }

    // ハニーポット: 人間の目には触れない隠しフィールド。値が入っていれば bot 投稿。
    public string? Website { get; set; }
}
