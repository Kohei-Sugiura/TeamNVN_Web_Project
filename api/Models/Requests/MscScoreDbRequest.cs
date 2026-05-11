namespace Weepa.Api.Models.Requests;

public sealed class MscScoreDbRequest
{
    public int? MscEventId { get; set; }
    public string? XId { get; set; }
    public int? SubmitId { get; set; }
    public int? PlayOrder { get; set; }
    public int? SongOrder { get; set; }
    public int? Score { get; set; }
}
