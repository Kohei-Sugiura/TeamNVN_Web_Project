using System.ComponentModel.DataAnnotations;

namespace Weepa.Api.Models.Requests;

public sealed class EventCreationRequest
{
    [Required(ErrorMessage = "weepaSongId は必須です")]
    public int WeepaSongId { get; set; }

    [Required(ErrorMessage = "startDate は必須です")]
    public DateTimeOffset StartDate { get; set; }

    [Required(ErrorMessage = "endDate は必須です")]
    public DateTimeOffset EndDate { get; set; }
}
