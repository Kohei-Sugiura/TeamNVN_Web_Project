using Dapper;
using Npgsql;

namespace Weepa.Api.Services;

/// <summary>
/// 毎分の tick で WeePA イベントの「終了時の結果発表」と「ランキング自動公開」を行う。
/// - end_date 経過: Tier 別ランキング画像 + 未処理警告を承認チャンネルへ投稿し、
///   auto_publish_at = end_date + publish_delay_hours を確定させる
/// - auto_publish_at 経過: 未処理スコアが無ければ tallied_at をセットして公開
/// </summary>
public sealed class WeepaScheduleTickService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly NpgsqlDataSource _dataSource;
    private readonly IDiscordNotifier _notifier;
    private readonly RankingScreenshot _shot;
    private readonly ILogger<WeepaScheduleTickService> _logger;

    // 「未処理があるため公開保留」の通知を出したイベント (プロセス内で1回だけ通知)
    private readonly HashSet<int> _blockedNotified = new();

    public WeepaScheduleTickService(
        NpgsqlDataSource dataSource,
        IDiscordNotifier notifier,
        RankingScreenshot shot,
        ILogger<WeepaScheduleTickService> logger)
    {
        _dataSource = dataSource;
        _notifier = notifier;
        _shot = shot;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await WaitNextTickAsync(timer, stoppingToken))
        {
            try { await AnnounceEndedEventsAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "result announce tick failed"); }

            try { await AutoPublishDueEventsAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "auto publish tick failed"); }
        }
    }

    private static async Task<bool> WaitNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    private async Task AnnounceEndedEventsAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var ended = (await conn.QueryAsync<(int EventId, string Song, string Effect)>(
            new CommandDefinition(
                """
                UPDATE weepa_events e
                SET result_announced_at = NOW(),
                    auto_publish_at = COALESCE(
                        e.auto_publish_at,
                        e.end_date + make_interval(hours => c.publish_delay_hours))
                FROM weepa_schedule_config c
                JOIN weepa_songs s ON TRUE
                WHERE c.id = 1
                  AND s.weepa_song_id = e.weepa_song_id
                  AND e.end_date <= NOW()
                  AND e.result_announced_at IS NULL
                RETURNING e.weepa_event_id AS EventId, s.song AS Song, s.effect AS Effect
                """,
                cancellationToken: ct))).ToList();

        foreach (var ev in ended)
        {
            _logger.LogInformation("announcing result for event {EventId}", ev.EventId);
            await AnnounceOneAsync(conn, ev.EventId, ev.Song, ev.Effect, ct);
        }
    }

    private async Task AnnounceOneAsync(
        NpgsqlConnection conn, int eventId, string song, string effect, CancellationToken ct)
    {
        var header = $"🏁 WeePA 終了 — {song} [{effect}] (event #{eventId})";

        var pendingCount = await PendingCountAsync(conn, eventId, ct);
        if (pendingCount > 0)
        {
            await _notifier.PostTextAsync(
                $"{header}\n⚠️ 未処理 (承認/却下待ち) の投稿が {pendingCount} 件あります。以下のランキングに反映されていない可能性があります",
                ct);
        }

        var tiers = (await conn.QueryAsync<int>(
            new CommandDefinition(
                """
                SELECT DISTINCT tier FROM (
                    SELECT DISTINCT ON (x_id) tier
                    FROM weepa_scores
                    WHERE weepa_event_id = @Id AND approval_status = 1
                    ORDER BY x_id, submit_date DESC
                ) latest
                """,
                new { Id = eventId },
                cancellationToken: ct))).ToHashSet();

        if (tiers.Count == 0)
        {
            await _notifier.PostTextAsync($"{header}\n🏆 集計結果: まだ投稿がありません", ct);
            return;
        }

        foreach (var tier in new[] { 1, 2, 3 })
        {
            if (!tiers.Contains(tier)) continue;
            var png = await _shot.CaptureRankingAsync(eventId, tier, ct);
            await _notifier.PostImageAsync(
                $"🏆 集計結果 ({header}) — {TierName(tier)}",
                png,
                $"weepa-result-{eventId}-tier{tier}.png",
                ct);
        }
    }

    private async Task AutoPublishDueEventsAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var due = (await conn.QueryAsync<(int EventId, string Song, string Effect)>(
            new CommandDefinition(
                """
                SELECT e.weepa_event_id AS EventId, s.song AS Song, s.effect AS Effect
                FROM weepa_events e
                JOIN weepa_songs s ON s.weepa_song_id = e.weepa_song_id
                WHERE e.tallied_at IS NULL
                  AND e.auto_publish_at IS NOT NULL
                  AND e.auto_publish_at <= NOW()
                ORDER BY e.auto_publish_at ASC
                """,
                cancellationToken: ct))).ToList();

        foreach (var ev in due)
        {
            var pendingCount = await PendingCountAsync(conn, ev.EventId, ct);
            if (pendingCount > 0)
            {
                if (_blockedNotified.Add(ev.EventId))
                {
                    await _notifier.PostTextAsync(
                        $"⏸️ event #{ev.EventId} ({ev.Song} [{ev.Effect}]) の自動公開時刻になりましたが、未処理の投稿が {pendingCount} 件あるため保留中です。処理が完了し次第、自動で公開されます",
                        ct);
                }
                continue;
            }

            var affected = await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE weepa_events SET tallied_at = NOW() WHERE weepa_event_id = @Id AND tallied_at IS NULL",
                new { Id = ev.EventId },
                cancellationToken: ct));
            if (affected == 0) continue;

            _blockedNotified.Remove(ev.EventId);
            _logger.LogInformation("auto-published event {EventId}", ev.EventId);
            await _notifier.PostTextAsync(
                $"📢 event #{ev.EventId} ({ev.Song} [{ev.Effect}]) のランキングを公開しました\nhttps://team-nvn-server.com/weepa/ranking/?eventId={ev.EventId}",
                ct);
        }
    }

    private static Task<int> PendingCountAsync(NpgsqlConnection conn, int eventId, CancellationToken ct) =>
        conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM weepa_scores WHERE weepa_event_id = @Id AND approval_status = 0",
            new { Id = eventId },
            cancellationToken: ct));

    private static string TierName(int tier) => tier switch
    {
        1 => "ストイック部門",
        2 => "チャレンジ部門",
        3 => "カジュアル部門",
        _ => $"部門{tier}",
    };
}
