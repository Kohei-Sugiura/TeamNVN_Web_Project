namespace Weepa.Api.Models.Requests;

public sealed class ScheduleConfigRequest
{
    /// <summary>0=Sun ... 6=Sat</summary>
    public int DayOfWeek { get; set; }
    public int Hour { get; set; }
    public int Minute { get; set; }
    public int DurationHours { get; set; }
}
