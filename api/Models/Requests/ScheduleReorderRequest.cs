namespace Weepa.Api.Models.Requests;

public sealed class ScheduleReorderRequest
{
    /// <summary>新しい順序での weepa_event_id の配列。先頭が次に開催される（=最も早い start_date）。</summary>
    public int[] Order { get; set; } = System.Array.Empty<int>();
}
