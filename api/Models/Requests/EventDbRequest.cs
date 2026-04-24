namespace Weepa.Api.Models.Requests;

public sealed class EventDbRequest
{
    public int? WeepaSongId { get; set; }
    public DateTimeOffset? StartDate { get; set; }
    public DateTimeOffset? EndDate { get; set; }
}
