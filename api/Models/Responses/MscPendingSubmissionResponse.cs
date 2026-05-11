namespace Weepa.Api.Models.Responses;

public sealed class MscPendingSubmissionResponse
{
    public int MscEventId { get; init; }
    public string XId { get; init; } = string.Empty;
    public int SubmitId { get; init; }
    public string SubmitName { get; init; } = string.Empty;
    public string Platform { get; init; } = string.Empty;
    public DateTimeOffset SubmitDate { get; init; }
    public string EventTitle { get; init; } = string.Empty;
    public List<MscPendingPlayResponse> Plays { get; set; } = new();
    public List<MscPendingImageResponse> Images { get; set; } = new();
}

public sealed class MscPendingPlayResponse
{
    public int PlayOrder { get; init; }
    public int SongOrder { get; init; }
    public int Score { get; init; }
    public string Song { get; init; } = string.Empty;
    public string Effect { get; init; } = string.Empty;
}

public sealed class MscPendingImageResponse
{
    public int PlayOrder { get; init; }
    public string ImagePath { get; init; } = string.Empty;
}
