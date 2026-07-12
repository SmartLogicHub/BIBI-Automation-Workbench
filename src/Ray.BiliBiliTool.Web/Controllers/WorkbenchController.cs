using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Application.Contracts.ContentAutomation;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.Config.SQLite;
using Ray.BiliBiliTool.Web.Services;

namespace Ray.BiliBiliTool.Web.Controllers;

[ApiController]
[Route("api/workbench")]
public class WorkbenchController(
    IConfiguration configuration,
    IOptionsMonitor<ContentAutomationBridgeOptions> bridgeOptions,
    IOptionsMonitor<LocalWorkbenchOptions> localOptions,
    IContentAutomationBridge contentAutomationBridge,
    IDataMaintenanceService dataMaintenanceService
) : ControllerBase
{
    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken cancellationToken)
    {
        var executor = await contentAutomationBridge.CheckHealthAsync(cancellationToken);
        var local = localOptions.CurrentValue;

        return Ok(
            new
            {
                status = "ok",
                generatedAt = DateTimeOffset.Now,
                local.IsInitialized,
                backendBaseUrl = local.BackendBaseUrl,
                frontendApiBaseUrl = local.FrontendApiBaseUrl,
                executor = new
                {
                    executor.IsOnline,
                    executor.BaseUrl,
                    executor.Message,
                },
            }
        );
    }

    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        return Ok(
            new WorkbenchConfigPayload(bridgeOptions.CurrentValue, localOptions.CurrentValue)
        );
    }

    [HttpPost("config")]
    public IActionResult SaveConfig([FromBody] WorkbenchConfigPayload payload)
    {
        var provider = GetSqliteConfigurationProvider();
        if (provider is null)
            return Problem("SQLite configuration provider is unavailable.");

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (payload.ContentAutomationBridge is not null)
        {
            foreach (var item in payload.ContentAutomationBridge.ToConfigDictionary())
                values[item.Key] = item.Value;
        }

        if (payload.LocalWorkbench is not null)
        {
            payload.LocalWorkbench.IsInitialized = true;
            foreach (var item in payload.LocalWorkbench.ToConfigDictionary())
                values[item.Key] = item.Value;
        }

        if (values.Count == 0)
            return BadRequest("No configuration values were supplied.");

        provider.BatchSet(values);
        return Ok(new { success = true, saved = values.Count });
    }

    [HttpGet("maintenance/snapshot")]
    public async Task<IActionResult> GetMaintenanceSnapshot(CancellationToken cancellationToken)
    {
        return Ok(await dataMaintenanceService.GetSnapshotAsync(cancellationToken));
    }

    [HttpPost("logs/clean")]
    public async Task<IActionResult> CleanLogs(
        [FromQuery] int daysToKeep = 30,
        CancellationToken cancellationToken = default
    )
    {
        var system = await dataMaintenanceService.CleanExpiredSystemLogsAsync(
            daysToKeep,
            cancellationToken
        );
        var files = await dataMaintenanceService.CleanExpiredLogFilesAsync(
            daysToKeep,
            cancellationToken
        );

        return Ok(
            new
            {
                success = system.Success && files.Success,
                system,
                files,
            }
        );
    }

    private SqliteConfigurationProvider? GetSqliteConfigurationProvider()
    {
        if (configuration is not IConfigurationRoot root)
            return null;

        foreach (var provider in root.Providers)
        {
            if (provider is SqliteConfigurationProvider sqlite)
                return sqlite;
        }

        return null;
    }
}

public sealed record WorkbenchConfigPayload(
    ContentAutomationBridgeOptions? ContentAutomationBridge,
    LocalWorkbenchOptions? LocalWorkbench
);
