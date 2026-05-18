namespace Weepa.Api.Services;

/// <summary>
/// 30 秒ごとに「プレー中」状態の部屋メッセージを編集して経過時間を更新する。
/// 編集対象がなければ何もしない。
/// </summary>
public sealed class SdvxRoomBackgroundService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly SdvxRoomService _rooms;
    private readonly DiscordCommandClient _discord;
    private readonly ILogger<SdvxRoomBackgroundService> _logger;

    public SdvxRoomBackgroundService(
        SdvxRoomService rooms,
        DiscordCommandClient discord,
        ILogger<SdvxRoomBackgroundService> logger)
    {
        _rooms = rooms;
        _discord = discord;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "sdvx room tick failed");
            }
            try
            {
                await Task.Delay(TickInterval, stoppingToken);
            }
            catch (TaskCanceledException) { /* shutting down */ }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var playing = _rooms.GetPlaying();
        if (playing.Count == 0) return;
        foreach (var r in playing)
        {
            if (ct.IsCancellationRequested) return;
            var payload = _rooms.BuildMessagePayload(r);
            await _discord.EditChannelMessageAsync(r.ChannelId, r.MessageId, payload, ct);
        }
    }
}
