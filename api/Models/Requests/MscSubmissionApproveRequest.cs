namespace Weepa.Api.Models.Requests;

/// <summary>
/// MSC 提出の承認 (任意で各 play スコアを修正承認)。
/// Plays が null なら修正なし、指定された play_order のみ score 更新。
/// </summary>
public sealed class MscSubmissionApproveRequest
{
    public List<MscPlayCorrection>? Plays { get; set; }
}

public sealed class MscPlayCorrection
{
    public int PlayOrder { get; set; }
    public int Score { get; set; }
}
