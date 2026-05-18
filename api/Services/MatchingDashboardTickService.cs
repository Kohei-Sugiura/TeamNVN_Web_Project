namespace Weepa.Api.Services;

/// <summary>
/// マッチング管理ダッシュボード (Phase 1 メッセージ) の経過時間更新タイマー。
/// プレー中の部屋がある間、最後の更新から 30 秒経過したら再 PATCH を予約する。
/// 手動更新が走った時は MatchingManagerService.LastDashboardRefreshAt が更新されるため、
/// 自動的に 30 秒タイマーがリセットされる効果になる。
/// </summary>
public sealed class MatchingDashboardTickService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RefreshThreshold = TimeSpan.FromSeconds(5);

    private readonly MatchingManagerService _matching;
    private readonly ILogger<MatchingDashboardTickService> _logger;

    public MatchingDashboardTickService(
        MatchingManagerService matching,
        ILogger<MatchingDashboardTickService> logger)
    {
        _matching = matching;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_matching.HasPlayingRoom())
                {
                    var elapsed = DateTimeOffset.UtcNow - _matching.LastDashboardRefreshAt;
                    if (elapsed >= RefreshThreshold)
                    {
                        _matching.RefreshDashboard();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "matching dashboard tick failed");
            }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (TaskCanceledException) { /* shutting down */ }
        }
    }
}
