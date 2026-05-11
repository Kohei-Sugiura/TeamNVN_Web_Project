namespace Weepa.Api.Models.Responses;

public sealed class MscEventResponse
{
    public int MscEventId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public DateTimeOffset StartDate { get; init; }
    public DateTimeOffset EndDate { get; init; }
    public DateTimeOffset? TalliedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public List<MscSongResponse> Songs { get; set; } = new();
}

public sealed class MscSongResponse
{
    public int SongOrder { get; init; }
    public string Song { get; init; } = string.Empty;
    public string Effect { get; init; } = string.Empty;
}
