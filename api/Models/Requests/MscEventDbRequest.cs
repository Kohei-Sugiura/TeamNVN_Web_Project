namespace Weepa.Api.Models.Requests;

public sealed class MscEventDbRequest
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset? StartDate { get; set; }
    public DateTimeOffset? EndDate { get; set; }
    public DateTimeOffset? TalliedAt { get; set; }
}
