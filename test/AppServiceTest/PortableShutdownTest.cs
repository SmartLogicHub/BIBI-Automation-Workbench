using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Ray.BiliBiliTool.Application.Contracts.ContentAutomation;
using Ray.BiliBiliTool.Application.Contracts.MaintenanceWorkflows;
using Ray.BiliBiliTool.Web.Services;

namespace AppServiceTest;

public sealed class PortableShutdownTest
{
    [Fact]
    public async Task Last_connected_page_starts_fifteen_second_shutdown_grace_period()
    {
        var delay = new ControllableDelay();
        var shutdown = new RecordingShutdownCoordinator();
        using var lifecycle = CreateLifecycle(delay, shutdown);

        lifecycle.PageConnected("page-1");
        lifecycle.PageDisconnected("page-1");

        Assert.Equal(TimeSpan.FromSeconds(15), delay.SinglePendingDelay);
        Assert.Equal(0, shutdown.RequestCount);

        delay.CompleteNext();
        await shutdown.WaitForRequestAsync();

        Assert.Equal(1, shutdown.RequestCount);
    }

    [Fact]
    public async Task Reconnect_during_grace_period_cancels_pending_shutdown()
    {
        var delay = new ControllableDelay();
        var shutdown = new RecordingShutdownCoordinator();
        using var lifecycle = CreateLifecycle(delay, shutdown);

        lifecycle.PageConnected("page-1");
        lifecycle.PageDisconnected("page-1");
        lifecycle.PageConnected("page-2");

        await delay.WaitForCancellationAsync();
        Assert.Equal(0, shutdown.RequestCount);

        lifecycle.PageDisconnected("page-2");
        delay.CompleteNext();
        await shutdown.WaitForRequestAsync();

        Assert.Equal(1, shutdown.RequestCount);
    }

    [Fact]
    public void Closing_one_of_two_pages_does_not_start_shutdown_timer()
    {
        var delay = new ControllableDelay();
        var shutdown = new RecordingShutdownCoordinator();
        using var lifecycle = CreateLifecycle(delay, shutdown);

        lifecycle.PageConnected("page-1");
        lifecycle.PageConnected("page-2");
        lifecycle.PageDisconnected("page-1");

        Assert.Empty(delay.RequestedDelays);
        Assert.Equal(0, shutdown.RequestCount);
    }

    [Fact]
    public void Never_connected_process_does_not_start_shutdown_timer()
    {
        var delay = new ControllableDelay();
        var shutdown = new RecordingShutdownCoordinator();
        using var lifecycle = CreateLifecycle(delay, shutdown);

        lifecycle.PageDisconnected("unknown-page");

        Assert.Empty(delay.RequestedDelays);
        Assert.Equal(0, shutdown.RequestCount);
    }

    [Fact]
    public async Task Repeated_disconnect_callbacks_request_shutdown_only_once()
    {
        var delay = new ControllableDelay();
        var shutdown = new RecordingShutdownCoordinator();
        using var lifecycle = CreateLifecycle(delay, shutdown);

        lifecycle.PageConnected("page-1");
        lifecycle.PageDisconnected("page-1");
        lifecycle.PageDisconnected("page-1");
        lifecycle.PageDisconnected("page-1");

        Assert.Single(delay.RequestedDelays);
        delay.CompleteNext();
        await shutdown.WaitForRequestAsync();
        lifecycle.PageDisconnected("page-1");

        Assert.Equal(1, shutdown.RequestCount);
        Assert.Single(delay.RequestedDelays);
    }

    [Fact]
    public async Task Automatic_exit_disabled_never_requests_shutdown()
    {
        var delay = new ControllableDelay();
        var shutdown = new RecordingShutdownCoordinator();
        using var lifecycle = new PortableUiLifecycleCoordinator(
            new PortableLaunchOptions(null, false, false),
            delay,
            shutdown,
            NullLogger<PortableUiLifecycleCoordinator>.Instance
        );

        lifecycle.PageConnected("page-1");
        lifecycle.PageDisconnected("page-1");
        await Task.Yield();

        Assert.Empty(delay.RequestedDelays);
        Assert.Equal(0, shutdown.RequestCount);
    }

    [Fact]
    public async Task Graceful_shutdown_stops_workflows_then_python_waits_and_stops_host()
    {
        var order = new List<string>();
        var workflows = CreateProxy<IMaintenanceWorkflowService>(
            (method, _) =>
            {
                Assert.Equal(nameof(IMaintenanceWorkflowService.StopAllRunsAsync), method.Name);
                order.Add("workflows");
                return Task.FromResult(2);
            }
        );
        var bridge = CreateProxy<IContentAutomationBridge>(
            (method, _) =>
            {
                Assert.Equal(nameof(IContentAutomationBridge.StopAllJobsAsync), method.Name);
                order.Add("python");
                return Task.FromResult(3);
            }
        );
        var delay = new RecordingImmediateDelay(order);
        var lifetime = new RecordingApplicationLifetime(order);
        var coordinator = new GracefulShutdownCoordinator(
            workflows,
            bridge,
            lifetime,
            delay,
            NullLogger<GracefulShutdownCoordinator>.Instance
        );

        await coordinator.RequestShutdownAsync();

        Assert.True(coordinator.IsShutdownRequested);
        Assert.Equal(["workflows", "python", "wait:5", "stop-host"], order);
    }

    [Fact]
    public async Task Graceful_shutdown_is_idempotent_and_stops_host_when_executor_stop_fails()
    {
        var workflowCalls = 0;
        var bridgeCalls = 0;
        var workflows = CreateProxy<IMaintenanceWorkflowService>(
            (method, _) =>
            {
                Assert.Equal(nameof(IMaintenanceWorkflowService.StopAllRunsAsync), method.Name);
                workflowCalls++;
                return Task.FromResult(1);
            }
        );
        var bridge = CreateProxy<IContentAutomationBridge>(
            (method, _) =>
            {
                Assert.Equal(nameof(IContentAutomationBridge.StopAllJobsAsync), method.Name);
                bridgeCalls++;
                return Task.FromException<int>(new HttpRequestException("executor unavailable"));
            }
        );
        var lifetime = new RecordingApplicationLifetime([]);
        var coordinator = new GracefulShutdownCoordinator(
            workflows,
            bridge,
            lifetime,
            new RecordingImmediateDelay([]),
            NullLogger<GracefulShutdownCoordinator>.Instance
        );

        await Task.WhenAll(
            coordinator.RequestShutdownAsync(),
            coordinator.RequestShutdownAsync(),
            coordinator.RequestShutdownAsync()
        );

        Assert.Equal(1, workflowCalls);
        Assert.Equal(1, bridgeCalls);
        Assert.Equal(1, lifetime.StopCount);
    }

    [Fact]
    public async Task Graceful_shutdown_finishes_after_caller_cancellation()
    {
        var bridgeCalls = 0;
        var workflows = CreateProxy<IMaintenanceWorkflowService>(
            (method, args) =>
            {
                Assert.Equal(nameof(IMaintenanceWorkflowService.StopAllRunsAsync), method.Name);
                var token = (CancellationToken)args![0]!;
                Assert.False(token.IsCancellationRequested);
                return Task.FromException<int>(new OperationCanceledException());
            }
        );
        var bridge = CreateProxy<IContentAutomationBridge>(
            (method, _) =>
            {
                Assert.Equal(nameof(IContentAutomationBridge.StopAllJobsAsync), method.Name);
                bridgeCalls++;
                return Task.FromResult(0);
            }
        );
        var lifetime = new RecordingApplicationLifetime([]);
        var coordinator = new GracefulShutdownCoordinator(
            workflows,
            bridge,
            lifetime,
            new RecordingImmediateDelay([]),
            NullLogger<GracefulShutdownCoordinator>.Instance
        );
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await coordinator.RequestShutdownAsync(cancellation.Token);

        Assert.Equal(1, bridgeCalls);
        Assert.Equal(1, lifetime.StopCount);
    }

    private static PortableUiLifecycleCoordinator CreateLifecycle(
        IAsyncDelay delay,
        IGracefulShutdownCoordinator shutdown
    )
    {
        return new PortableUiLifecycleCoordinator(
            new PortableLaunchOptions(null, false, true),
            delay,
            shutdown,
            NullLogger<PortableUiLifecycleCoordinator>.Instance
        );
    }

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, DelegateProxy>();
        ((DelegateProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private class DelegateProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return Handler(targetMethod!, args);
        }
    }

    private sealed class ControllableDelay : IAsyncDelay
    {
        private readonly object _sync = new();
        private readonly Queue<TaskCompletionSource> _pending = new();
        private readonly TaskCompletionSource _cancelled = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public List<TimeSpan> RequestedDelays { get; } = [];

        public TimeSpan SinglePendingDelay => Assert.Single(RequestedDelays);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            lock (_sync)
            {
                RequestedDelays.Add(delay);
                _pending.Enqueue(completion);
            }

            cancellationToken.Register(() =>
            {
                _cancelled.TrySetResult();
                completion.TrySetCanceled(cancellationToken);
            });
            return completion.Task;
        }

        public void CompleteNext()
        {
            TaskCompletionSource completion;
            lock (_sync)
            {
                do completion = _pending.Dequeue();
                while (completion.Task.IsCompleted && _pending.Count > 0);
            }
            completion.TrySetResult();
        }

        public Task WaitForCancellationAsync() =>
            _cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class RecordingImmediateDelay(List<string> order) : IAsyncDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            order.Add($"wait:{delay.TotalSeconds:0}");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingShutdownCoordinator : IGracefulShutdownCoordinator
    {
        private readonly TaskCompletionSource _requested = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public bool IsShutdownRequested => RequestCount > 0;

        public int RequestCount { get; private set; }

        public Task RequestShutdownAsync(CancellationToken cancellationToken = default)
        {
            RequestCount++;
            _requested.TrySetResult();
            return Task.CompletedTask;
        }

        public Task WaitForRequestAsync() => _requested.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class RecordingApplicationLifetime(List<string> order) : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public int StopCount { get; private set; }

        public void StopApplication()
        {
            StopCount++;
            order.Add("stop-host");
        }
    }
}
