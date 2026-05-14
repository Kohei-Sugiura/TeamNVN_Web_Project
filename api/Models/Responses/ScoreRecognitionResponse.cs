namespace Weepa.Api.Models.Responses;

public sealed class ScoreRecognitionResponse
{
    public bool Success { get; init; }
    public int? Score { get; init; }
    public int? ExScore { get; init; }
    public string? Error { get; init; }
}
