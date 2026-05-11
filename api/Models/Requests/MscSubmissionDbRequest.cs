namespace Weepa.Api.Models.Requests;

public sealed class MscSubmissionDbRequest
{
    public int? MscEventId { get; set; }
    public string? XId { get; set; }
    public int? SubmitId { get; set; }
    public string? SubmitName { get; set; }
    public string? Platform { get; set; }
    public DateTimeOffset? SubmitDate { get; set; }
    public short? ApprovalStatus { get; set; }
}
