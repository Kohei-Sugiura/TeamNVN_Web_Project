namespace Weepa.Api.Models.Requests;

public sealed class SongDbRequest
{
    public string? Song { get; set; }
    public string? Effect { get; set; }
    public string? XId { get; set; }
    public string? SubmitName { get; set; }
    public string? SubmitReason { get; set; }
    public DateTimeOffset? SubmitDate { get; set; }
    public short? ApprovalStatus { get; set; }
}
