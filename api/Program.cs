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

// 画像認識 API のレート制限: IP ごとに 1 分 10 リクエスト。
// nginx 経由のため X-Forwarded-For 末尾を見る (リバプロから来る場合の対策)。
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("recognize", httpContext =>
    {
        var ip = httpContext.Request.Headers["X-Forwarded-For"].ToString()
            .Split(',')
            .Select(s => s.Trim())
            .FirstOrDefault(s => !string.IsNullOrEmpty(s))
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });
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
