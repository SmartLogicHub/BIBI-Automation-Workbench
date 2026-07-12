using Ray.BiliBiliTool.Application.Contracts.ContentAutomation;
using Ray.BiliBiliTool.Application.Contracts.MaintenanceWorkflows;

namespace Ray.BiliBiliTool.Web.Services;

public interface IGracefulShutdownCoordinator
{
    bool IsShutdownRequested { get; }

    Task RequestShutdownAsync(CancellationToken cancellationToken = default);
}

public sealed class GracefulShutdownCoordinator : IGracefulShutdownCoordinator
{
    private static readonly TimeSpan ShutdownGracePeriod = TimeSpan.FromSeconds(5);
    private readonly object _sync = new();
    private readonly IMaintenanceWorkflowService _workflows;
    private readonly IContentAutomationBridge _bridge;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly IAsyncDelay _delay;
    private readonly ILogger<GracefulShutdownCoordinator> _logger;
    private Task? _shutdownTask;
    private bool _shutdownRequested;

    public GracefulShutdownCoordinator(
        IMaintenanceWorkflowService workflows,
        IContentAutomationBridge bridge,
        IHostApplicationLifetime applicationLifetime,
        IAsyncDelay delay,
        ILogger<GracefulShutdownCoordinator> logger
    )
    {
        _workflows = workflows;
        _bridge = bridge;
        _applicationLifetime = applicationLifetime;
        _delay = delay;
        _logger = logger;
    }

    public bool IsShutdownRequested
    {
        get
        {
            lock (_sync)
                return _shutdownRequested;
        }
    }

    public Task RequestShutdownAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_shutdownRequested)
                return _shutdownTask ?? Task.CompletedTask;

            _shutdownRequested = true;
            _shutdownTask = ShutdownCoreAsync();
            return _shutdownTask;
        }
    }

    private async Task ShutdownCoreAsync()
    {
        _logger.LogInformation("Portable shutdown started because all BIBI pages were closed");
        using var timeout = new CancellationTokenSource(ShutdownGracePeriod);
        var cancellationToken = timeout.Token;

        try
        {
            await _workflows.StopAllRunsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Maintenance workflow shutdown reached its cancellation boundary");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to stop all maintenance workflows during shutdown");
        }

        try
        {
            await _bridge.StopAllJobsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Python job shutdown reached its cancellation boundary");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to request Python jobs to stop during shutdown");
        }

        try
        {
            await _delay.DelayAsync(ShutdownGracePeriod, cancellationToken);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _applicationLifetime.StopApplication();
        }
    }
}
