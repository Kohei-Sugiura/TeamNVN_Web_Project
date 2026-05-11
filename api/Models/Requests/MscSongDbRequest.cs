namespace Weepa.Api.Models.Requests;

public sealed class MscSongDbRequest
{
    public int? MscEventId { get; set; }
    public int? SongOrder { get; set; }
    public string? Song { get; set; }
    public string? Effect { get; set; }
}
