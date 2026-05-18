using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using Weepa.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddHttpClient();

builder.Services.AddSingleton<NpgsqlDataSource>(sp =>
{
    var connectionString = sp.GetRequiredService<IConfiguration>()
        .GetConnectionString("Default")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:Default is not configured");
    return new NpgsqlDataSourceBuilder(connectionString).Build();
});

builder.Services.AddSingleton<IDiscordNotifier, DiscordNotifier>();
builder.Services.AddSingleton<WeepaAdminClient>();
builder.Services.AddSingleton<DiscordCommandClient>();
builder.Services.AddSingleton<RankingScreenshot>();
builder.Services.AddSingleton<ClaudeImageRecognitionService>();
builder.Services.AddSingleton<SdvxRoomService>();
builder.Services.AddHostedService<SdvxRoomBackgroundService>();

// 画像認識 API のレート制限: WeePA / MSC で別バケット (IP × エンドポイント種別)、
// 各 1 分 3 リクエスト。nginx 経由のため X-Forwarded-For 末尾を見る。
static string GetClientIp(HttpContext ctx)
{
    return ctx.Request.Headers["X-Forwarded-For"].ToString()
        .Split(',')
        .Select(s => s.Trim())
        .FirstOrDefault(s => !string.IsNullOrEmpty(s))
        ?? ctx.Connection.RemoteIpAddress?.ToString()
        ?? "unknown";
}

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    foreach (var policy in new[] { "recognize-weepa", "recognize-msc" })
    {
        var key = policy;
        options.AddPolicy(key, ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                $"{key}:{GetClientIp(ctx)}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 3,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
    }
});

var app = builder.Build();

app.UseRouting();
app.UseRateLimiter();
app.MapControllers();

// 起動時に Discord slash command を冪等に sync (PUT は完全置換) する。
// DISCORD_BOT_TOKEN / DISCORD_APP_ID 未設定なら no-op で抜ける。
_ = Task.Run(async () =>
{
    var cmd = app.Services.GetRequiredService<DiscordCommandClient>();
    await cmd.SyncCommandsAsync();
});

app.Run();
