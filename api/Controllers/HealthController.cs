using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Weepa.Api.Controllers;

[ApiController]
[Route("api/health")]
public class HealthController : ControllerBase
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<HealthController> _logger;

    public HealthController(
        NpgsqlDataSource dataSource,
        ILogger<HealthController> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        string dbStatus;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            var result = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition("SELECT 1", cancellationToken: ct));
            dbStatus = result == 1 ? "ok" : "ng";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DB health check failed");
            dbStatus = "ng";
        }

        return Ok(new { status = "ok", db = dbStatus });
    }
}
