namespace Weepa.Api.Models.Requests;

public sealed class MscSubmissionImageDbRequest
{
    public int? MscEventId { get; set; }
    public string? XId { get; set; }
    public int? SubmitId { get; set; }
    public int? PlayOrder { get; set; }
    public string? ImagePath { get; set; }
}
