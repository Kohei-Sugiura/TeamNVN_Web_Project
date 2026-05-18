using System.Text.Json;

namespace Weepa.Api.Services;

public sealed class MatchingManagerState
{
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }
    // 待機部屋に入室中のユーザー ID。<@id> で mention レンダリングする
    public List<ulong> WaitingUserIds { get; set; } = new();
}

/// <summary>
/// オンマチ会 (マッチング管理) Bot の永続化付き状態管理。
/// uploads ボリューム上の JSON ファイルに保存。再起動後もメッセージ ID を保持して
/// 同じメッセージを編集し続けるために使う。
/// </summary>
public sealed class MatchingManagerService
{
    // /app/uploads は docker-compose の名前付きボリュームなので
    // コンテナ再ビルドや再起動でも残る
    private const string StatePath = "/app/uploads/matching-manager-state.json";

    private readonly ILogger<MatchingManagerService> _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private MatchingManagerState? _state;

    public MatchingManagerService(ILogger<MatchingManagerService> logger)
    {
        _logger = logger;
        // 同期初期化: 起動時に 1 回だけ読む
        try
        {
            if (File.Exists(StatePath))
            {
                var json = File.ReadAllText(StatePath);
                _state = JsonSerializer.Deserialize<MatchingManagerState>(json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to load matching manager state");
        }
    }

    public MatchingManagerState? GetState() => _state;

    public async Task SetStateAsync(MatchingManagerState state, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            _state = state;
            await SaveUnlockedAsync(ct);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task AddWaitingUserAsync(ulong userId, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            if (_state is null) return;
            if (!_state.WaitingUserIds.Contains(userId))
            {
                _state.WaitingUserIds.Add(userId);
                await SaveUnlockedAsync(ct);
            }
        }
        finally { _fileLock.Release(); }
    }

    public async Task RemoveWaitingUserAsync(ulong userId, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            if (_state is null) return;
            if (_state.WaitingUserIds.Remove(userId))
            {
                await SaveUnlockedAsync(ct);
            }
        }
        finally { _fileLock.Release(); }
    }

    private async Task SaveUnlockedAsync(CancellationToken ct)
    {
        if (_state is null) return;
        var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await File.WriteAllTextAsync(StatePath, json, ct);
    }

    /// <summary>
    /// マッチング管理用メッセージの payload を構築する。
    /// Phase 1 ではマッチング部屋は空、待機部屋ボタン + 新規作成ボタンのみ。
    /// </summary>
    public object BuildMessagePayload()
    {
        var waitingIds = _state?.WaitingUserIds ?? new List<ulong>();
        var memberLines = waitingIds.Count > 0
            ? string.Join("\n", waitingIds.Select(u => $"- <@{u}>"))
            : "-# 誰も居ません";

        var matchingSection = string.Join("\n", new[]
        {
            "## 🎮 オンマチ会 マッチング管理",
            "",
            "### マッチング部屋",
            "-# *(Phase 2 で実装予定)*",
        });
        var waitingSection = string.Join("\n", new[]
        {
            "### 待機部屋",
            "**入室者**",
            memberLines,
        });

        return new
        {
            flags = 32768, // IS_COMPONENTS_V2
            // mention 文字列は描画するが notification は飛ばさない
            allowed_mentions = new { parse = Array.Empty<string>() },
            components = new object[]
            {
                new
                {
                    type = 17, // CONTAINER
                    accent_color = 0x00e5ff,
                    components = new object[]
                    {
                        new { type = 10, content = matchingSection },
                        new
                        {
                            type = 1, // ACTION_ROW
                            components = new object[]
                            {
                                new { type = 2, style = 1, label = "新規マッチング部屋を作成",
                                      custom_id = "matching:create-room", emoji = new { name = "🆕" } },
                            },
                        },
                        // マッチング部屋 ↔ 待機部屋 の区切り
                        new { type = 14, divider = true, spacing = 1 },
                        new { type = 10, content = waitingSection },
                        new
                        {
                            type = 1, // ACTION_ROW
                            components = new object[]
                            {
                                new { type = 2, style = 3, label = "待機部屋に入室",
                                      custom_id = "matching:join-wait",  emoji = new { name = "🚪" } },
                                new { type = 2, style = 4, label = "待機部屋から退出",
                                      custom_id = "matching:leave-wait", emoji = new { name = "🚪" } },
                            },
                        },
                    },
                },
            },
        };
    }
}
