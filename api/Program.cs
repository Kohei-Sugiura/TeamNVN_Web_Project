using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddSingleton<NpgsqlDataSource>(sp =>
{
    var connectionString = sp.GetRequiredService<IConfiguration>()
        .GetConnectionString("Default")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:Default is not configured");
    return new NpgsqlDataSourceBuilder(connectionString).Build();
});

var app = builder.Build();

app.UseRouting();
app.MapControllers();

app.Run();
