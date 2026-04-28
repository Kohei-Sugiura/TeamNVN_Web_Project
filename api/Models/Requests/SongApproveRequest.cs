using System.ComponentModel.DataAnnotations;

namespace Weepa.Api.Models.Requests;

public sealed class SongApproveRequest
{
    [StringLength(200, MinimumLength = 1, ErrorMessage = "楽曲名は1〜200文字で入力してください")]
    public string? Song { get; set; }

    [RegularExpression(
        @"^(NOV|ADV|EXH|MXM|INF|GRV|HVN|VVD|XCD|ULT)$",
        ErrorMessage = "譜面種は NOV/ADV/EXH/MXM/INF/GRV/HVN/VVD/XCD/ULT のいずれかです")]
    public string? Effect { get; set; }
}
