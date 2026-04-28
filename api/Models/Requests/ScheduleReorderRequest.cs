namespace Weepa.Api.Models.Requests;

public sealed class ScheduleReorderRequest
{
    /// <summary>新しい順序での weepa_song_id の配列。先頭が次に開催される。</summary>
    public int[] Order { get; set; } = System.Array.Empty<int>();
}
