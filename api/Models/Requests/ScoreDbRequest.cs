namespace Weepa.Api.Models.Requests;

public sealed class ScoreDbRequest
{
    public int? WeepaEventId { get; set; }
    public string? XId { get; set; }
    public int? SubmitId { get; set; }
    public string? SubmitName { get; set; }
    public int? Score { get; set; }
    public int? ExScore { get; set; }
    public string? ImagePath { get; set; }
    public DateTimeOffset? SubmitDate { get; set; }
    public short? ApprovalStatus { get; set; }
}
