using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace Weepa.Api.Models.Requests;

/// <summary>
/// MSC スコア提出 (multipart/form-data)。
/// plays は JSON 文字列で受け取って controller 側で parse する。
/// 画像は arcade=summary 1 枚必須、konaste=summary or playN のどれかが
/// 1 枚以上必須 (各 play の枚数は plays.Count 以内)。
/// </summary>
public sealed class MscSubmissionRequest
{
    [Required(ErrorMessage = "X IDは必須です")]
    [StringLength(100, MinimumLength = 1)]
    public string XId { get; set; } = string.Empty;

    [Required(ErrorMessage = "表示名は必須です")]
    [StringLength(50, MinimumLength = 1, ErrorMessage = "表示名は1〜50文字で入力してください")]
    public string SubmitName { get; set; } = string.Empty;

    [Required(ErrorMessage = "platform は必須です")]
    [RegularExpression("^(arcade|konaste)$",
        ErrorMessage = "platform は 'arcade' か 'konaste' です")]
    public string Platform { get; set; } = string.Empty;

    /// <summary>
    /// JSON 文字列: [{"playOrder":1,"songOrder":1,"score":9800000}, ...]
    /// </summary>
    [Required(ErrorMessage = "plays は必須です")]
    public string Plays { get; set; } = string.Empty;

    public IFormFile? ImageSummary { get; set; }
    public IFormFile? ImagePlay1 { get; set; }
    public IFormFile? ImagePlay2 { get; set; }
    public IFormFile? ImagePlay3 { get; set; }
    public IFormFile? ImagePlay4 { get; set; }
}

public sealed class MscPlayInput
{
    public int PlayOrder { get; set; }
    public int SongOrder { get; set; }
    public int Score { get; set; }
}
