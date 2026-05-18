using System.Collections.Concurrent;

namespace Weepa.Api.Services;

/// <summary>
/// Discord メッセージ編集の debounce 用ヘルパー。
/// 同じ (channelId, messageId) への連続呼び出しを 1 秒間まとめて、最後の
/// payload だけを実際に PATCH する。Discord のレート制限を回避するため。
/// </summary>
public sealed class MessageEditDebouncer
{
    private readonly DiscordCommandClient _discord;
    private readonly ILogger<MessageEditDebouncer> _logger;
    private readonly TimeSpan _delay = TimeSpan.FromSeconds(1);

    // (channelId, messageId) → 現在 pending 中の CancellationTokenSource
    private readonly ConcurrentDictionary<(ulong, ulong), CancellationTokenSource> _pending = new();

    public MessageEditDebouncer(DiscordCommandClient discord, ILogger<MessageEditDebouncer> logger)
    {
        _discord = discord;
        _logger = logger;
    }

    /// <summary>
    /// 同キーで pending な debounce があればキャンセルし、即時 PATCH する。
    /// 状態遷移など 1 秒のラグも許容できない更新に使う。
    /// </summary>
    public async Task EditNowAsync(
        ulong channelId, ulong messageId, object payload, CancellationToken ct = default)
    {
        var key = (channelId, messageId);
        if (_pending.TryRemove(key, out var existing))
        {
            try { existing.Cancel(); existing.Dispose(); }
            catch { /* ignore */ }
        }
        await _discord.EditChannelMessageAsync(channelId, messageId, payload, ct);
    }

    /// <summary>
    /// 1 秒以内に同じキーで再度呼ぶと、前の呼び出しはキャンセルされ、新しい payloadFactory が
    /// その時点での内容で実行される。fire-and-forget。
    /// </summary>
    public void Schedule(ulong channelId, ulong messageId, Func<object> payloadFactory)
    {
        var key = (channelId, messageId);
        var newCts = new CancellationTokenSource();

        // 既存があればキャンセル + 新しい cts に差し替え (アトミック)
        _pending.AddOrUpdate(
            key,
            _ => newCts,
            (_, existing) =>
            {
                try { existing.Cancel(); existing.Dispose(); }
                catch { /* ignore */ }
                return newCts;
            });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_delay, newCts.Token);
                if (newCts.IsCancellationRequested) return;
                // 自分が最新の cts でなければ既に上書きされているのでスキップ
                if (!_pending.TryGetValue(key, out var current) || current != newCts) return;

                object payload;
                try { payload = payloadFactory(); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "debounce payload factory threw");
                    return;
                }

                await _discord.EditChannelMessageAsync(channelId, messageId, payload);
            }
            catch (TaskCanceledException) { /* superseded by newer Schedule */ }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "debounced edit threw");
            }
            finally
            {
                // 自分の cts が最新ならクリーンアップ
                if (_pending.TryGetValue(key, out var current) && current == newCts)
                {
                    _pending.TryRemove(key, out _);
                }
                try { newCts.Dispose(); } catch { }
            }
        });
    }
}
