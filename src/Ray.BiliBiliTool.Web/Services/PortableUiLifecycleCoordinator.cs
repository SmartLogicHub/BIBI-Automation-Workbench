namespace Ray.BiliBiliTool.Web.Services;

public sealed class PortableUiLifecycleCoordinator : IDisposable
{
    private static readonly TimeSpan DisconnectGracePeriod = TimeSpan.FromSeconds(15);
    private readonly object _sync = new();
    private readonly HashSet<string> _connectedPages = new(StringComparer.Ordinal);
    private readonly PortableLaunchOptions _launchOptions;
    private readonly IAsyncDelay _delay;
    private readonly IGracefulShutdownCoordinator _shutdown;
    private readonly ILogger<PortableUiLifecycleCoordinator> _logger;
    private CancellationTokenSource? _pendingShutdown;
    private bool _hasConnectedPage;
    private bool _shutdownStarted;
    private bool _disposed;

    public PortableUiLifecycleCoordinator(
        PortableLaunchOptions launchOptions,
        IAsyncDelay delay,
        IGracefulShutdownCoordinator shutdown,
        ILogger<PortableUiLifecycleCoordinator> logger
    )
    {
        _launchOptions = launchOptions;
        _delay = delay;
        _shutdown = shutdown;
        _logger = logger;
    }

    public void PageConnected(string pageId)
    {
        if (!_launchOptions.ExitWhenBrowserCloses || string.IsNullOrWhiteSpace(pageId))
            return;

        CancellationTokenSource? pending;
        lock (_sync)
        {
            if (_disposed || _shutdownStarted)
                return;
            _hasConnectedPage = true;
            _connectedPages.Add(pageId);
            pending = _pendingShutdown;
            _pendingShutdown = null;
        }

        if (pending is not null)
        {
            pending.Cancel();
            pending.Dispose();
            _logger.LogDebug("Portable shutdown cancelled because a BIBI page reconnected");
        }
    }

    public void PageDisconnected(string pageId)
    {
        if (!_launchOptions.ExitWhenBrowserCloses || string.IsNullOrWhiteSpace(pageId))
            return;

        CancellationTokenSource? pending = null;
        lock (_sync)
        {
            if (_disposed || _shutdownStarted)
                return;
            _connectedPages.Remove(pageId);
            if (!_hasConnectedPage || _connectedPages.Count > 0 || _pendingShutdown is not null)
                return;

            pending = new CancellationTokenSource();
            _pendingShutdown = pending;
        }

        _ = WaitForShutdownAsync(pending);
    }

    public void Dispose()
    {
        CancellationTokenSource? pending;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            pending = _pendingShutdown;
            _pendingShutdown = null;
            _connectedPages.Clear();
        }

        if (pending is not null)
        {
            pending.Cancel();
            pending.Dispose();
        }
    }

    private async Task WaitForShutdownAsync(CancellationTokenSource pending)
    {
        try
        {
            await _delay.DelayAsync(DisconnectGracePeriod, pending.Token);

            lock (_sync)
            {
                if (
                    _disposed
                    || _shutdownStarted
                    || !ReferenceEquals(_pendingShutdown, pending)
                    || _connectedPages.Count > 0
                )
                    return;

                _shutdownStarted = true;
                _pendingShutdown = null;
            }

            _logger.LogInformation(
                "No BIBI pages reconnected during the portable shutdown grace period"
            );
            await _shutdown.RequestShutdownAsync();
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Portable UI lifecycle shutdown failed");
        }
        finally
        {
            pending.Dispose();
        }
    }
}
