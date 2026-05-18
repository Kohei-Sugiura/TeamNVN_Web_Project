using System.Text.Json;

namespace Weepa.Api.Services;

public sealed class MatchingManagerState
{
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }
    // 待機部屋に入室中のユーザー ID。<@id> で mention レンダリングする
    public List<ulong> WaitingUserIds { get; set; } = new();
    // 開設中のマッチング部屋
    public List<MatchingRoom> Rooms { get; set; } = new();
}

public sealed class MatchingRoom
{
    public int Number { get; set; }              // 1〜9 (表示は ①〜⑨)
    public string Mode { get; set; } = "";       // 例: "ARENA MODE (NORMAL)"
    public string Password { get; set; } = "";   // 6 桁ゾロ目 (例: "111111")
    public ulong ChannelId { get; set; }
    public ulong RoleId { get; set; }
    public ulong AnchorMessageId { get; set; }   // 部屋チャンネル先頭の管理メッセージ
    public List<ulong> MemberUserIds { get; set; } = new();
}

public static class MatchingModes
{
    // (内部値, 表示名)
    public static readonly (string Value, string Display)[] All = new[]
    {
        ("ARENA_NORMAL",  "ARENA MODE (NORMAL)"),
        ("ARENA_ULT",     "ARENA MODE (ULTIMATE)"),
        ("SINGLE_NORMAL", "SINGLE MODE (NORMAL)"),
        ("SINGLE_ULT",    "SINGLE MODE (ULTIMATE)"),
        ("MEGAMIX",       "MEGAMIX BATTLE"),
    };

    public static string? ToDisplay(string value)
    {
        foreach (var (v, d) in All) if (v == value) return d;
        return null;
    }
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

    // ========================
    // Room 管理
    // ========================

    /// <summary>1〜9 のうち未使用最小の番号を返す。全部使用中なら -1。</summary>
    public int NextRoomNumber()
    {
        var used = new HashSet<int>(_state?.Rooms.Select(r => r.Number) ?? Enumerable.Empty<int>());
        for (int i = 1; i <= 9; i++) if (!used.Contains(i)) return i;
        return -1;
    }

    /// <summary>未使用のゾロ目パスワードからランダムに 1 つ選ぶ。空きが無ければ null。</summary>
    public string? PickPassword()
    {
        var used = new HashSet<string>(_state?.Rooms.Select(r => r.Password) ?? Enumerable.Empty<string>());
        var candidates = Enumerable.Range(0, 10)
            .Select(d => new string((char)('0' + d), 6))
            .Where(p => !used.Contains(p))
            .ToList();
        if (candidates.Count == 0) return null;
        return candidates[Random.Shared.Next(candidates.Count)];
    }

    /// <summary>ユーザーが既に何らかの部屋に入室しているか確認 (1 人 1 部屋制約用)。</summary>
    public MatchingRoom? FindRoomContaining(ulong userId)
    {
        return _state?.Rooms.FirstOrDefault(r => r.MemberUserIds.Contains(userId));
    }

    public MatchingRoom? GetRoom(int number)
    {
        return _state?.Rooms.FirstOrDefault(r => r.Number == number);
    }

    public MatchingRoom? GetRoomByChannelId(ulong channelId)
    {
        return _state?.Rooms.FirstOrDefault(r => r.ChannelId == channelId);
    }

    public async Task AddRoomAsync(MatchingRoom room, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            if (_state is null) return;
            _state.Rooms.Add(room);
            await SaveUnlockedAsync(ct);
        }
        finally { _fileLock.Release(); }
    }

    public async Task UpdateRoomAsync(int number, Action<MatchingRoom> mutate, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            if (_state is null) return;
            var room = _state.Rooms.FirstOrDefault(r => r.Number == number);
            if (room is null) return;
            mutate(room);
            await SaveUnlockedAsync(ct);
        }
        finally { _fileLock.Release(); }
    }

    public async Task RemoveRoomAsync(int number, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            if (_state is null) return;
            _state.Rooms.RemoveAll(r => r.Number == number);
            await SaveUnlockedAsync(ct);
        }
        finally { _fileLock.Release(); }
    }

    public static string NumberToCircle(int n) => $"{(char)(0x2460 + n - 1)}";

    public static string FormatPassword(string pw) =>
        pw.Length == 6 ? $"{pw[..3]} {pw[3..]}" : pw;

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
        var rooms = _state?.Rooms.OrderBy(r => r.Number).ToList() ?? new List<MatchingRoom>();

        var inner = new List<object>
        {
            new
            {
                type = 10,
                content = string.Join("\n", new[]
                {
                    "# 🎮 オンマチ会 マッチング管理",
                    "",
                    "## マッチング部屋",
                }),
            },
            // 新規マッチング部屋作成ボタンは「マッチング部屋」見出し直下に置く
            new
            {
                type = 1,
                components = new object[]
                {
                    new
                    {
                        type = 2, style = 1,
                        label = "新規マッチング部屋を作成",
                        custom_id = "matching:create-room",
                        emoji = new { name = "🆕" },
                        disabled = rooms.Count >= 9,
                    },
                },
            },
        };

        if (rooms.Count == 0)
        {
            inner.Add(new { type = 10, content = "-# 現在開設中の部屋はありません" });
        }
        else
        {
            foreach (var r in rooms)
            {
                // 部屋ごとに罫線 (1 部屋目の前にも引く → ボタン直下にも区切りが入る)
                inner.Add(new { type = 14, divider = true, spacing = 1 });
                var memberLines = r.MemberUserIds.Count > 0
                    ? string.Join(" ", r.MemberUserIds.Select(u => $"<@{u}>"))
                    : "-# 誰も居ません";
                var info = string.Join("\n", new[]
                {
                    $"### 🎮 オンマチ{NumberToCircle(r.Number)}",
                    "**モード**",
                    r.Mode,
                    "",
                    "**ステータス**",
                    "-# *(Phase 3 で実装予定)*",
                    "",
                    "**入室者**",
                    memberLines,
                });
                inner.Add(new { type = 10, content = info });
                inner.Add(new
                {
                    type = 1,
                    components = new object[]
                    {
                        new
                        {
                            type = 2, style = 3,
                            label = $"オンマチ{NumberToCircle(r.Number)} に入室",
                            custom_id = $"matching:join-room:{r.Number}",
                            emoji = new { name = "🎮" },
                        },
                    },
                });
            }
        }

        // マッチング部屋 ↔ 待機部屋 セパレータ
        inner.Add(new { type = 14, divider = true, spacing = 1 });

        // 待機部屋セクション
        var waitMemberLines = waitingIds.Count > 0
            ? string.Join("\n", waitingIds.Select(u => $"- <@{u}>"))
            : "-# 誰も居ません";
        inner.Add(new
        {
            type = 10,
            content = string.Join("\n", new[]
            {
                "### 待機部屋",
                "**入室者**",
                waitMemberLines,
            }),
        });
        inner.Add(new
        {
            type = 1,
            components = new object[]
            {
                new { type = 2, style = 3, label = "待機部屋に入室",
                      custom_id = "matching:join-wait",  emoji = new { name = "🚪" } },
                new { type = 2, style = 4, label = "待機部屋から退出",
                      custom_id = "matching:leave-wait", emoji = new { name = "🚪" } },
            },
        });

        return new
        {
            flags = 32768,
            allowed_mentions = new { parse = Array.Empty<string>() },
            components = new object[]
            {
                new
                {
                    type = 17,
                    accent_color = 0x00e5ff,
                    components = inner,
                },
            },
        };
    }

    /// <summary>
    /// 部屋テキストチャンネルの先頭に固定するアンカーメッセージの payload。
    /// プレー開始 (Phase 3) / 退出 / プレー終了 ボタン付き。
    /// </summary>
    public object BuildRoomAnchorPayload(MatchingRoom room)
    {
        var memberLines = room.MemberUserIds.Count > 0
            ? string.Join(" ", room.MemberUserIds.Select(u => $"<@{u}>"))
            : "-# 誰も居ません";
        var content = string.Join("\n", new[]
        {
            $"## 🎮 オンマチ{NumberToCircle(room.Number)}",
            "**モード**",
            room.Mode,
            "",
            "**ステータス**",
            "-# *(Phase 3 で実装予定)*",
            "",
            "**パスワード**",
            $"`{FormatPassword(room.Password)}`",
            "",
            "**入室者**",
            memberLines,
        });
        return new
        {
            flags = 32768,
            allowed_mentions = new { parse = Array.Empty<string>() },
            components = new object[]
            {
                new
                {
                    type = 17,
                    accent_color = 0xbb00ff,
                    components = new object[]
                    {
                        new { type = 10, content },
                        new { type = 14, divider = true, spacing = 1 },
                        new
                        {
                            type = 1,
                            components = new object[]
                            {
                                new { type = 2, style = 3, label = "プレー開始 (Phase 3)",
                                      custom_id = "matching:play-start", emoji = new { name = "▶" },
                                      disabled = true },
                                new { type = 2, style = 2, label = "退出",
                                      custom_id = "matching:room-leave", emoji = new { name = "🚪" } },
                            },
                        },
                    },
                },
            },
        };
    }
}
