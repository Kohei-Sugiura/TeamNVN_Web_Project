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

var app = builder.Build();

app.UseRouting();
app.MapControllers();

// 起動時に Discord slash command を冪等に sync (PUT は完全置換) する。
// DISCORD_BOT_TOKEN / DISCORD_APP_ID 未設定なら no-op で抜ける。
_ = Task.Run(async () =>
{
    var cmd = app.Services.GetRequiredService<DiscordCommandClient>();
    await cmd.SyncCommandsAsync();
});

app.Run();
