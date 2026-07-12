using System.Diagnostics;
using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Application.Contracts.ContentAutomation;
using Ray.BiliBiliTool.Application.Contracts.MaintenanceWorkflows;
using Ray.BiliBiliTool.Application.Contracts.Runtime;
using Ray.BiliBiliTool.Config.Options;

namespace Ray.BiliBiliTool.Web.Services;

public sealed class ContentExecutorHostedService : BackgroundService
{
    private readonly IContentAutomationBridge _bridge;
    private readonly IMaintenanceWorkflowService _workflows;
    private readonly ISystemRuntimeStateStore _runtimeState;
    private readonly IOptionsMonitor<ContentAutomationBridgeOptions> _bridgeOptions;
    private readonly IOptionsMonitor<LocalWorkbenchOptions> _workbenchOptions;
    private readonly ILogger<ContentExecutorHostedService> _logger;
    private Process? _ownedProcess;
    private DateTimeOffset? _processStartedAt;
    private DateTimeOffset _lastStartAttempt = DateTimeOffset.MinValue;

    public ContentExecutorHostedService(
        IContentAutomationBridge bridge,
        IMaintenanceWorkflowService workflows,
        ISystemRuntimeStateStore runtimeState,
        IOptionsMonitor<ContentAutomationBridgeOptions> bridgeOptions,
        IOptionsMonitor<LocalWorkbenchOptions> workbenchOptions,
        ILogger<ContentExecutorHostedService> logger
    )
    {
        _bridge = bridge;
        _workflows = workflows;
        _runtimeState = runtimeState;
        _bridgeOptions = bridgeOptions;
        _workbenchOptions = workbenchOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _runtimeState.Update(
            new SystemRuntimeState
            {
                Status = SystemRuntimeStatus.Starting,
                Message = "正在准备运行环境",
            }
        );

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!_bridgeOptions.CurrentValue.IsEnable)
                {
                    PublishNeedsAttention("运行服务需要处理");
                    await DelayAsync(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                var health = await _bridge.CheckHealthAsync(stoppingToken);
                if (!health.IsOnline)
                {
                    TryStartExecutor();
                    var isStarting =
                        _processStartedAt.HasValue
                        && DateTimeOffset.Now - _processStartedAt.Value < TimeSpan.FromSeconds(20);
                    _runtimeState.Update(
                        new SystemRuntimeState
                        {
                            Status = isStarting
                                ? SystemRuntimeStatus.Starting
                                : SystemRuntimeStatus.NeedsAttention,
                            Message = isStarting ? "运行服务正在启动" : "运行服务需要处理",
                        }
                    );
                    await DelayAsync(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                await RefreshSnapshotAsync(stoppingToken);
                await DelayAsync(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            await StopOwnedProcessAsync();
            _runtimeState.Update(
                new SystemRuntimeState
                {
                    Status = SystemRuntimeStatus.Stopped,
                    Message = "应用已停止",
                }
            );
        }
    }

    private async Task RefreshSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            var accountsTask = _bridge.GetAccountsAsync(cancellationToken);
            var ledgerTask = _bridge.GetLedgerAsync(cancellationToken);
            var runsTask = _workflows.GetRunsAsync(cancellationToken);
            await Task.WhenAll(accountsTask, ledgerTask, runsTask);

            var accounts = await accountsTask;
            var ledger = await ledgerTask;
            var runs = await runsTask;
            var today = DateTimeOffset.Now.Date;
            var activeRun = runs.FirstOrDefault(run =>
                run.Status == MaintenanceWorkflowRunStatus.Running
            );

            _runtimeState.Update(
                new SystemRuntimeState
                {
                    Status = SystemRuntimeStatus.Ready,
                    Message = "系统运行正常",
                    AvailableAccounts = accounts.Count(IsAvailableAccount),
                    TotalAccounts = accounts.Count,
                    TodayComments = ledger.Count(item =>
                        IsToday(item.CreatedAt) && IsSuccessful(item.Status)
                    ),
                    TodayMaintenanceRuns = runs.Count(run =>
                        run.StartedAt.LocalDateTime.Date == today
                    ),
                    ActiveOperation = activeRun?.WorkflowName ?? "",
                }
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to refresh runtime snapshot");
            PublishNeedsAttention("状态刷新暂时不可用");
        }
    }

    private void TryStartExecutor()
    {
        if (_ownedProcess is { HasExited: false })
            return;

        if (DateTimeOffset.Now - _lastStartAttempt < TimeSpan.FromSeconds(5))
            return;
        _lastStartAttempt = DateTimeOffset.Now;

        try
        {
            _ownedProcess?.Dispose();
            _ownedProcess = null;

            var startInfo = BuildStartInfo();
            if (startInfo is null)
            {
                _logger.LogError("Comment executor files are missing");
                return;
            }

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    _logger.LogDebug("Comment service: {Message}", args.Data);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    _logger.LogWarning("Comment service: {Message}", args.Data);
            };
            process.Exited += (_, _) =>
                _logger.LogWarning("Comment service exited with code {ExitCode}", process.ExitCode);

            if (!process.Start())
            {
                process.Dispose();
                return;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _ownedProcess = process;
            _processStartedAt = DateTimeOffset.Now;
            _logger.LogInformation("Comment service started as a managed child process");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to start the managed comment service");
        }
    }

    private ProcessStartInfo? BuildStartInfo()
    {
        var configuredPath = _bridgeOptions.CurrentValue.ExecutorPath?.Trim() ?? "";
        var bundledDirectory = Path.Combine(AppContext.BaseDirectory, "Executor");
        var bundledExecutable = Path.Combine(bundledDirectory, "BiliCommentWorkbench.exe");
        var bundledScript = Path.Combine(bundledDirectory, "server.py");

        string fileName;
        string? scriptPath = null;
        if (configuredPath.Length > 0 && File.Exists(configuredPath))
        {
            if (
                string.Equals(
                    Path.GetExtension(configuredPath),
                    ".py",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                fileName = PortableRuntimePaths.ResolvePythonExecutable(
                    AppContext.BaseDirectory,
                    Environment.GetEnvironmentVariable("BIBI_PYTHON")
                );
                scriptPath = configuredPath;
            }
            else
            {
                fileName = configuredPath;
            }
        }
        else if (File.Exists(bundledExecutable))
        {
            fileName = bundledExecutable;
        }
        else if (File.Exists(bundledScript))
        {
            fileName = PortableRuntimePaths.ResolvePythonExecutable(
                AppContext.BaseDirectory,
                Environment.GetEnvironmentVariable("BIBI_PYTHON")
            );
            scriptPath = bundledScript;
        }
        else
        {
            return null;
        }

        var workingDirectory = scriptPath is null
            ? Path.GetDirectoryName(fileName) ?? AppContext.BaseDirectory
            : Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory;
        var stateDirectory = Path.GetFullPath("./data/comment-service", AppContext.BaseDirectory);
        Directory.CreateDirectory(stateDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        if (scriptPath is not null)
            startInfo.ArgumentList.Add(scriptPath);

        startInfo.Environment["BILI_WORKBENCH_HOME"] = stateDirectory;
        startInfo.Environment["BIBI_EXECUTOR_PORT"] = ResolveExecutorPort().ToString();
        startInfo.Environment["BIBI_PARENT_PID"] = Environment.ProcessId.ToString(
            System.Globalization.CultureInfo.InvariantCulture
        );
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        var bundledBrowsers = PortableRuntimePaths.ResolvePlaywrightBrowsersPath(
            AppContext.BaseDirectory
        );
        if (bundledBrowsers is not null)
            startInfo.Environment["PLAYWRIGHT_BROWSERS_PATH"] = bundledBrowsers;
        return startInfo;
    }

    private int ResolveExecutorPort()
    {
        if (
            Uri.TryCreate(_bridgeOptions.CurrentValue.BaseUrl, UriKind.Absolute, out var uri)
            && uri.Port > 0
        )
            return uri.Port;
        return Math.Max(1, _workbenchOptions.CurrentValue.ExecutorPort);
    }

    private async Task StopOwnedProcessAsync()
    {
        var process = _ownedProcess;
        _ownedProcess = null;
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Managed comment service had already stopped");
        }
        finally
        {
            process.Dispose();
        }
    }

    private void PublishNeedsAttention(string message)
    {
        var current = _runtimeState.Current;
        current.Status = SystemRuntimeStatus.NeedsAttention;
        current.Message = message;
        _runtimeState.Update(current);
    }

    private static bool IsAvailableAccount(ContentAutomationAccount account)
    {
        if (!account.Enabled)
            return false;
        var status = account.LoginStatus?.Trim().ToLowerInvariant();
        return status is "ok" or "logged_in" or "success";
    }

    private static bool IsSuccessful(string status)
    {
        return status?.Trim().ToLowerInvariant() is "success" or "done" or "completed";
    }

    private static bool IsToday(string value)
    {
        return DateTimeOffset.TryParse(value, out var date)
            && date.LocalDateTime.Date == DateTimeOffset.Now.Date;
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken);
    }
}
