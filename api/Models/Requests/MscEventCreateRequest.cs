using System.ComponentModel.DataAnnotations;

namespace Weepa.Api.Models.Requests;

public sealed class MscEventCreateRequest
{
    [Required(ErrorMessage = "タイトルは必須です")]
    [StringLength(200, MinimumLength = 1, ErrorMessage = "タイトルは1〜200文字で入力してください")]
    public string Title { get; set; } = string.Empty;

    [StringLength(1000, ErrorMessage = "説明は1000文字以内で入力してください")]
    public string? Description { get; set; }

    [Required(ErrorMessage = "開催開始日時は必須です")]
    public DateTimeOffset StartDate { get; set; }

    [Required(ErrorMessage = "開催終了日時は必須です")]
    public DateTimeOffset EndDate { get; set; }

    [Required(ErrorMessage = "課題曲は必須です")]
    [MinLength(2, ErrorMessage = "課題曲は2曲以上必要です")]
    [MaxLength(4, ErrorMessage = "課題曲は4曲までです")]
    public List<MscSongInput> Songs { get; set; } = new();
}

public sealed class MscSongInput
{
    [Range(1, 4, ErrorMessage = "songOrder は 1〜4 です")]
    public int SongOrder { get; set; }

    [Required(ErrorMessage = "楽曲名は必須です")]
    [StringLength(200, MinimumLength = 1, ErrorMessage = "楽曲名は1〜200文字で入力してください")]
    public string Song { get; set; } = string.Empty;

    [Required(ErrorMessage = "譜面種は必須です")]
    [RegularExpression(
        @"^(NOV|ADV|EXH|MXM|INF|GRV|HVN|VVD|XCD|NBL|ULT)$",
        ErrorMessage = "譜面種は NOV/ADV/EXH/MXM/INF/GRV/HVN/VVD/XCD/NBL/ULT のいずれかです")]
    public string Effect { get; set; } = string.Empty;
}
