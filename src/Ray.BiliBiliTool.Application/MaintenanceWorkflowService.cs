using System.Globalization;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.Relation;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Interfaces;
using Ray.BiliBiliTool.Application.Contracts;
using Ray.BiliBiliTool.Application.Contracts.ContentAutomation;
using Ray.BiliBiliTool.Application.Contracts.MaintenanceWorkflows;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.DomainService.Dtos;
using Ray.BiliBiliTool.DomainService.Interfaces;
using Ray.BiliBiliTool.Infrastructure.Cookie;

namespace Ray.BiliBiliTool.Application;

public class MaintenanceWorkflowService : IMaintenanceWorkflowService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _lock = new();
    private readonly IServiceProvider? _serviceProvider;
    private readonly ILogger<MaintenanceWorkflowService>? _logger;
    private readonly string _storePath;
    private readonly List<MaintenanceWorkflowDefinition> _workflows = [];
    private readonly List<MaintenanceWorkflowRunRecord> _runs = [];
    private readonly Dictionary<string, CancellationTokenSource> _activeRunStops = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly Dictionary<string, string> _activeWorkflowRuns = new(
        StringComparer.OrdinalIgnoreCase
    );
    private bool _loaded;

    public MaintenanceWorkflowService()
    {
        _storePath = "";
    }

    public MaintenanceWorkflowService(
        IServiceProvider serviceProvider,
        IOptionsMonitor<LocalWorkbenchOptions> options,
        ILogger<MaintenanceWorkflowService> logger
    )
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _storePath = ResolveStorePath(options.CurrentValue.WorkflowStorePath);
    }

    public Task<IReadOnlyList<MaintenanceWorkflowNodeDefinition>> GetNodeCatalogAsync(
        CancellationToken cancellationToken = default
    )
    {
        return Task.FromResult<IReadOnlyList<MaintenanceWorkflowNodeDefinition>>(
            BuildNodeCatalog()
        );
    }

    public Task<IReadOnlyList<MaintenanceWorkflowDefinition>> GetWorkflowsAsync(
        CancellationToken cancellationToken = default
    )
    {
        EnsureLoaded();
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<MaintenanceWorkflowDefinition>>(
                _workflows.Select(CloneWorkflow).ToList()
            );
        }
    }

    public Task<MaintenanceWorkflowDefinition?> GetWorkflowAsync(
        string workflowId,
        CancellationToken cancellationToken = default
    )
    {
        EnsureLoaded();
        lock (_lock)
        {
            var workflow = _workflows.FirstOrDefault(x => SameId(x.Id, workflowId));
            return Task.FromResult(workflow is null ? null : CloneWorkflow(workflow));
        }
    }

    public Task<MaintenanceWorkflowDefinition> SaveWorkflowAsync(
        MaintenanceWorkflowDefinition workflow,
        CancellationToken cancellationToken = default
    )
    {
        EnsureLoaded();
        var normalized = NormalizeWorkflow(workflow);

        lock (_lock)
        {
            var index = _workflows.FindIndex(x => SameId(x.Id, normalized.Id));
            if (index >= 0)
                _workflows[index] = normalized;
            else
                _workflows.Add(normalized);

            foreach (var run in _runs.Where(run => SameId(run.WorkflowId, normalized.Id)))
                run.WorkflowName = normalized.Name;

            SaveState();
        }

        return Task.FromResult(CloneWorkflow(normalized));
    }

    public Task<MaintenanceWorkflowDefinition?> CopyWorkflowAsync(
        string workflowId,
        CancellationToken cancellationToken = default
    )
    {
        EnsureLoaded();
        MaintenanceWorkflowDefinition? copy;
        lock (_lock)
        {
            var source = _workflows.FirstOrDefault(x => SameId(x.Id, workflowId));
            if (source is null)
                return Task.FromResult<MaintenanceWorkflowDefinition?>(null);

            copy = CloneWorkflow(source);
            copy.Id = NewId();
            copy.Name = $"{copy.Name} 副本";
            copy.CreatedAt = DateTimeOffset.Now;
            copy.UpdatedAt = copy.CreatedAt;
            var stepIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            copy.Steps = copy
                .Steps.Select(step =>
                {
                    var cloned = CloneStep(step);
                    stepIdMap[cloned.Id] = NewId();
                    cloned.Id = stepIdMap[cloned.Id];
                    return cloned;
                })
                .ToList();
            copy.Edges = copy
                .Edges.Select(edge =>
                {
                    var cloned = CloneEdge(edge);
                    cloned.Id = NewId();
                    cloned.SourceStepId = stepIdMap.GetValueOrDefault(
                        edge.SourceStepId,
                        edge.SourceStepId
                    );
                    cloned.TargetStepId = stepIdMap.GetValueOrDefault(
                        edge.TargetStepId,
                        edge.TargetStepId
                    );
                    return cloned;
                })
                .ToList();

            _workflows.Add(copy);
            SaveState();
        }

        return Task.FromResult<MaintenanceWorkflowDefinition?>(copy);
    }

    public Task<bool> DeleteWorkflowAsync(
        string workflowId,
        CancellationToken cancellationToken = default
    )
    {
        EnsureLoaded();
        lock (_lock)
        {
            var hasActiveRun = _runs.Any(run =>
                SameId(run.WorkflowId, workflowId)
                && run.Status
                    is MaintenanceWorkflowRunStatus.Running
                        or MaintenanceWorkflowRunStatus.WaitingManualConfirm
            );
            if (hasActiveRun)
                return Task.FromResult(false);

            var removed = _workflows.RemoveAll(x => SameId(x.Id, workflowId)) > 0;
            if (removed)
            {
                _runs.RemoveAll(run => SameId(run.WorkflowId, workflowId));
                SaveState();
            }
            return Task.FromResult(removed);
        }
    }

    public Task<int> RemoveAccountAsync(
        string accountUid,
        CancellationToken cancellationToken = default
    )
    {
        EnsureLoaded();
        var normalizedUid = accountUid?.Trim() ?? "";
        if (normalizedUid.Length == 0)
            return Task.FromResult(0);

        lock (_lock)
        {
            var changed = 0;
            foreach (var workflow in _workflows)
            {
                var remaining = workflow
                    .AccountIds.Where(id =>
                        !id.Equals(normalizedUid, StringComparison.OrdinalIgnoreCase)
                    )
                    .ToList();
                if (remaining.Count == workflow.AccountIds.Count)
                    continue;

                workflow.AccountIds = remaining;
                workflow.UpdatedAt = DateTimeOffset.Now;
                changed++;
            }

            if (changed > 0)
                SaveState();
            return Task.FromResult(changed);
        }
    }

    public async Task<MaintenanceWorkflowRunRecord> RunWorkflowAsync(
        string workflowId,
        MaintenanceWorkflowRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        EnsureLoaded();
        var workflow = await GetWorkflowAsync(workflowId, cancellationToken);
        if (workflow is null)
        {
            return new MaintenanceWorkflowRunRecord
            {
                Id = NewId(),
                WorkflowId = workflowId,
                StartedAt = DateTimeOffset.Now,
                EndedAt = DateTimeOffset.Now,
                Status = MaintenanceWorkflowRunStatus.Failed,
                FailureReason = "流程不存在",
            };
        }

        var run = new MaintenanceWorkflowRunRecord
        {
            Id = NewId(),
            WorkflowId = workflow.Id,
            WorkflowName = workflow.Name,
            DryRun = request.DryRun,
            TriggerSource = string.IsNullOrWhiteSpace(request.TriggerSource)
                ? "manual"
                : request.TriggerSource.Trim(),
            StartedAt = DateTimeOffset.Now,
            Status = MaintenanceWorkflowRunStatus.Running,
            ExecutionConfig = JsonSerializer.Serialize(workflow, JsonOptions),
        };

        if (!workflow.IsEnabled)
            return FinishRunWithoutExecution(
                run,
                MaintenanceWorkflowRunStatus.Skipped,
                "流程已停用，没有执行任何动作。"
            );

        if (
            !TryIsWithinTimeWindow(
                workflow.TimeWindow,
                TimeOnly.FromDateTime(DateTime.Now),
                out var isWithinTimeWindow
            )
        )
        {
            return FinishRunWithoutExecution(
                run,
                MaintenanceWorkflowRunStatus.Failed,
                "运行时段格式不正确，请在流程设置中使用 09:00-18:00 这样的格式。"
            );
        }

        if (!isWithinTimeWindow)
        {
            return FinishRunWithoutExecution(
                run,
                MaintenanceWorkflowRunStatus.Skipped,
                $"当前不在设置的运行时段（{workflow.TimeWindow}）内。"
            );
        }

        using var stopCts = new CancellationTokenSource();
        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromMinutes(Math.Max(1, workflow.MaxRuntimeMinutes))
        );
        using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            stopCts.Token,
            timeoutCts.Token
        );

        var duplicateRun = false;
        lock (_lock)
        {
            if (_activeWorkflowRuns.ContainsKey(workflow.Id))
            {
                duplicateRun = true;
            }
            else
            {
                _activeWorkflowRuns[workflow.Id] = run.Id;
                _activeRunStops[run.Id] = stopCts;
            }
        }

        if (duplicateRun)
        {
            return FinishRunWithoutExecution(
                run,
                MaintenanceWorkflowRunStatus.Skipped,
                "该流程正在运行，本次重复启动已取消。"
            );
        }

        var logs = new List<MaintenanceWorkflowStepLog>();
        StoreRun(run);

        if (!request.DryRun)
        {
            run.AccountSnapshots = await CaptureAccountSnapshotsAsync(
                workflow,
                "before",
                CancellationToken.None
            );
            StoreRun(run);
        }

        try
        {
            var executionSteps = GetExecutionSteps(workflow);
            var workflowEdges = workflow.Edges ?? Array.Empty<MaintenanceWorkflowEdge>();
            var incomingTargets = workflowEdges
                .Select(edge => edge.TargetStepId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var reachedSteps = executionSteps
                .Where(step => !incomingTargets.Contains(step.Id))
                .Select(step => step.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var step in executionSteps)
            {
                if (!reachedSteps.Contains(step.Id))
                    continue;

                executionCts.Token.ThrowIfCancellationRequested();
                var log = await ExecuteStepWithPolicyAsync(
                    workflow,
                    step,
                    request,
                    executionCts.Token
                );
                logs.Add(log);

                var outgoingEdges = workflowEdges
                    .Where(edge => SameId(edge.SourceStepId, step.Id))
                    .ToList();
                if (
                    step.NodeType.Equals("control.random", StringComparison.OrdinalIgnoreCase)
                    && log.Status == MaintenanceWorkflowStepStatus.Success
                )
                {
                    var selectedBranch = SelectRandomBranch(step);
                    log.Message =
                        selectedBranch == "matched"
                            ? "随机判断已命中，继续执行命中分支。"
                            : "随机判断未命中，继续执行另一分支。";
                    var selectedEdges = outgoingEdges
                        .Where(edge => NormalizeBranchLabel(edge) == selectedBranch)
                        .ToList();
                    if (selectedEdges.Count == 0 && outgoingEdges.Count > 0)
                    {
                        var fallbackIndex =
                            selectedBranch == "matched" ? 0 : Math.Min(1, outgoingEdges.Count - 1);
                        selectedEdges.Add(outgoingEdges[fallbackIndex]);
                    }

                    foreach (var edge in selectedEdges)
                        reachedSteps.Add(edge.TargetStepId);
                }
                else
                {
                    foreach (var edge in outgoingEdges)
                        reachedSteps.Add(edge.TargetStepId);
                }

                switch (log.Status)
                {
                    case MaintenanceWorkflowStepStatus.Success:
                        run.SuccessCount++;
                        break;
                    case MaintenanceWorkflowStepStatus.Skipped:
                        run.SkippedCount++;
                        break;
                    case MaintenanceWorkflowStepStatus.WaitingManualConfirm:
                        run.Status = MaintenanceWorkflowRunStatus.WaitingManualConfirm;
                        run.StepLogs = logs.ToList();
                        StoreRun(run);
                        return CloneRun(run);
                    case MaintenanceWorkflowStepStatus.Failed:
                        run.FailedCount++;
                        if (step.FailureStrategy.Equals("stop", StringComparison.OrdinalIgnoreCase))
                        {
                            run.Status = MaintenanceWorkflowRunStatus.Failed;
                            run.FailureReason = log.Message;
                            run.StepLogs = logs.ToList();
                            await AppendAccountSnapshotsAsync(run, workflow, "after");
                            run.EndedAt = DateTimeOffset.Now;
                            StoreRun(run);
                            return CloneRun(run);
                        }
                        break;
                }

                run.StepLogs = logs.ToList();
                StoreRun(run);
            }

            run.Status =
                run.FailedCount > 0
                    ? MaintenanceWorkflowRunStatus.Failed
                    : MaintenanceWorkflowRunStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            var exceededRuntime =
                timeoutCts.IsCancellationRequested
                && !stopCts.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested;
            run.Status = exceededRuntime
                ? MaintenanceWorkflowRunStatus.Failed
                : MaintenanceWorkflowRunStatus.Cancelled;
            run.FailureReason = exceededRuntime ? "流程超过最长运行时间。" : "流程已停止。";
        }
        finally
        {
            lock (_lock)
            {
                _activeRunStops.Remove(run.Id);
                if (
                    _activeWorkflowRuns.TryGetValue(workflow.Id, out var activeRunId)
                    && SameId(activeRunId, run.Id)
                )
                {
                    _activeWorkflowRuns.Remove(workflow.Id);
                }
            }
        }

        run.StepLogs = logs.ToList();
        if (!request.DryRun)
            await AppendAccountSnapshotsAsync(run, workflow, "after");
        run.EndedAt = DateTimeOffset.Now;
        StoreRun(run);
        return CloneRun(run);
    }

    public Task<IReadOnlyList<MaintenanceWorkflowRunRecord>> GetRunsAsync(
        CancellationToken cancellationToken = default
    )
    {
        EnsureLoaded();
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<MaintenanceWorkflowRunRecord>>(
                _runs.OrderByDescending(x => x.StartedAt).Select(CloneRun).ToList()
            );
        }
    }

    public Task<MaintenanceWorkflowRunRecord?> GetRunAsync(
        string runId,
        CancellationToken cancellationToken = default
    )
    {
        EnsureLoaded();
        lock (_lock)
        {
            var run = _runs.FirstOrDefault(x => SameId(x.Id, runId));
            return Task.FromResult(run is null ? null : CloneRun(run));
        }
    }

    public Task<bool> StopRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        EnsureLoaded();
        CancellationTokenSource? stopCts;
        lock (_lock)
        {
            var run = _runs.FirstOrDefault(x => SameId(x.Id, runId));
            if (
                run is null
                || run.Status
                    is not (
                        MaintenanceWorkflowRunStatus.Running
                        or MaintenanceWorkflowRunStatus.WaitingManualConfirm
                    )
            )
                return Task.FromResult(false);

            run.Status = MaintenanceWorkflowRunStatus.Cancelled;
            run.EndedAt = DateTimeOffset.Now;
            run.FailureReason = "流程已停止。";
            _activeRunStops.TryGetValue(runId, out stopCts);
            SaveState();
        }

        stopCts?.Cancel();
        return Task.FromResult(true);
    }

    public Task<bool> DeleteRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var run = _runs.FirstOrDefault(x => SameId(x.Id, runId));
            if (
                run is null
                || run.Status
                    is MaintenanceWorkflowRunStatus.Running
                        or MaintenanceWorkflowRunStatus.WaitingManualConfirm
            )
            {
                return Task.FromResult(false);
            }

            _runs.Remove(run);
            SaveState();
            return Task.FromResult(true);
        }
    }

    public Task<int> ClearRunsAsync(
        string workflowId,
        CancellationToken cancellationToken = default
    )
    {
        EnsureLoaded();
        lock (_lock)
        {
            var removed = _runs.RemoveAll(run =>
                SameId(run.WorkflowId, workflowId)
                && run.Status
                    is not (
                        MaintenanceWorkflowRunStatus.Running
                        or MaintenanceWorkflowRunStatus.WaitingManualConfirm
                    )
            );
            if (removed > 0)
                SaveState();
            return Task.FromResult(removed);
        }
    }

    private async Task<MaintenanceWorkflowStepLog> ExecuteStepWithPolicyAsync(
        MaintenanceWorkflowDefinition workflow,
        MaintenanceWorkflowStep step,
        MaintenanceWorkflowRunRequest request,
        CancellationToken cancellationToken
    )
    {
        if (!step.NodeType.Equals("control.random", StringComparison.OrdinalIgnoreCase))
        {
            var executionProbability = GetIntParameter(step, "executionProbability", 100, 0, 100);
            if (executionProbability == 0 || Random.Shared.Next(1, 101) > executionProbability)
            {
                return StepLog(
                    step,
                    step.DisplayName,
                    DateTimeOffset.Now,
                    MaintenanceWorkflowStepStatus.Skipped,
                    "根据本次执行概率，本次未执行。",
                    0
                );
            }
        }

        var isRepeatableAction = IsGranularNode(step.NodeType);
        var dailyLimit = isRepeatableAction ? GetIntParameter(step, "dailyLimit", 0, 0, 1000) : 0;
        var usedToday = dailyLimit > 0 ? CountSuccessfulExecutionsToday(workflow.Id, step.Id) : 0;
        if (dailyLimit > 0 && usedToday >= dailyLimit)
        {
            return StepLog(
                step,
                step.DisplayName,
                DateTimeOffset.Now,
                MaintenanceWorkflowStepStatus.Skipped,
                "今日执行次数已达到设置的上限。",
                0
            );
        }

        var countMin = isRepeatableAction ? GetIntParameter(step, "countMin", 1, 1, 50) : 1;
        var countMax = isRepeatableAction
            ? GetIntParameter(step, "countMax", countMin, countMin, 50)
            : 1;
        var executionCount = Random.Shared.Next(countMin, countMax + 1);
        if (dailyLimit > 0)
            executionCount = Math.Min(executionCount, dailyLimit - usedToday);

        var waitMinSeconds = isRepeatableAction
            ? GetIntParameter(step, "waitMinSeconds", 0, 0, 3600)
            : 0;
        var waitMaxSeconds = isRepeatableAction
            ? GetIntParameter(step, "waitMaxSeconds", waitMinSeconds, waitMinSeconds, 3600)
            : 0;
        MaintenanceWorkflowStepLog? lastLog = null;
        var completedCount = 0;
        var evidence = new List<MaintenanceWorkflowActionEvidence>();

        for (var executionIndex = 0; executionIndex < executionCount; executionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (executionIndex > 0 && waitMaxSeconds > 0)
            {
                var waitSeconds = Random.Shared.Next(waitMinSeconds, waitMaxSeconds + 1);
                if (waitSeconds > 0)
                    await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
            }

            lastLog = await ExecuteStepAttemptWithRetryAsync(
                workflow,
                step,
                request,
                cancellationToken
            );
            evidence.AddRange(lastLog.Evidence);
            if (lastLog.Status == MaintenanceWorkflowStepStatus.Success)
            {
                completedCount++;
                continue;
            }

            lastLog.ExecutionCount = completedCount;
            lastLog.Evidence = evidence;
            return lastLog;
        }

        lastLog ??= StepLog(
            step,
            step.DisplayName,
            DateTimeOffset.Now,
            MaintenanceWorkflowStepStatus.Skipped,
            "本次没有可执行动作。",
            0
        );
        lastLog.ExecutionCount = completedCount;
        lastLog.Evidence = evidence;
        if (completedCount > 1)
            lastLog.Message = $"已按设置完成 {completedCount} 次执行。";
        return lastLog;
    }

    private async Task<MaintenanceWorkflowStepLog> ExecuteStepAttemptWithRetryAsync(
        MaintenanceWorkflowDefinition workflow,
        MaintenanceWorkflowStep step,
        MaintenanceWorkflowRunRequest request,
        CancellationToken cancellationToken
    )
    {
        var retryCount = step.FailureStrategy.Equals("retry", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(0, step.RetryCount)
            : 0;
        MaintenanceWorkflowStepLog? lastLog = null;

        for (var attempt = 0; attempt <= retryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (step.MaxRuntimeSeconds > 0)
                stepCts.CancelAfter(TimeSpan.FromSeconds(step.MaxRuntimeSeconds));

            try
            {
                lastLog = await ExecuteStepAsync(workflow, step, request, stepCts.Token);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested && stepCts.IsCancellationRequested)
            {
                lastLog = StepLog(
                    step,
                    step.DisplayName,
                    DateTimeOffset.Now.AddSeconds(-Math.Max(1, step.MaxRuntimeSeconds)),
                    MaintenanceWorkflowStepStatus.Failed,
                    $"运行超时（{step.MaxRuntimeSeconds} 秒）。"
                );
            }

            if (lastLog.Status != MaintenanceWorkflowStepStatus.Failed)
                return lastLog;
        }

        if (retryCount > 0 && lastLog is not null)
            lastLog.Message = $"{lastLog.Message} 已重试 {retryCount} 次。";
        return lastLog
            ?? StepLog(
                step,
                step.DisplayName,
                DateTimeOffset.Now,
                MaintenanceWorkflowStepStatus.Failed,
                "任务未能完成。",
                0
            );
    }

    private async Task<MaintenanceWorkflowStepLog> ExecuteStepAsync(
        MaintenanceWorkflowDefinition workflow,
        MaintenanceWorkflowStep step,
        MaintenanceWorkflowRunRequest request,
        CancellationToken cancellationToken
    )
    {
        var startedAt = DateTimeOffset.Now;
        var catalogNode = BuildNodeCatalog().FirstOrDefault(x => x.NodeType == step.NodeType);
        var displayName = !string.IsNullOrWhiteSpace(step.DisplayName)
            ? step.DisplayName
            : catalogNode?.DisplayName ?? step.NodeType;

        if (!step.Enabled)
            return StepLog(
                step,
                displayName,
                startedAt,
                MaintenanceWorkflowStepStatus.Skipped,
                "步骤已禁用，跳过。"
            );

        var requiredPermission = !string.IsNullOrWhiteSpace(step.Permission)
            ? step.Permission
            : catalogNode?.Permission ?? "";
        if (
            !string.IsNullOrWhiteSpace(requiredPermission)
            && request.PermissionSnapshot.TryGetValue(requiredPermission, out var hasPermission)
            && !hasPermission
        )
        {
            return StepLog(
                step,
                displayName,
                startedAt,
                MaintenanceWorkflowStepStatus.Skipped,
                $"缺少权限：{requiredPermission}，已跳过。"
            );
        }

        if (step.NodeType.StartsWith("account.", StringComparison.OrdinalIgnoreCase))
            return StepLog(
                step,
                displayName,
                startedAt,
                MaintenanceWorkflowStepStatus.Skipped,
                "账号登录、Cookie 和账号状态已迁移到账号管理，不再作为养号策略节点执行。"
            );

        if (step.NodeType == "task.comment_automation")
            return StepLog(
                step,
                displayName,
                startedAt,
                MaintenanceWorkflowStepStatus.Skipped,
                "评论引流已迁移到评论引流运行中心，不再混入自动养号策略编排。"
            );

        if (catalogNode is null)
            return StepLog(
                step,
                displayName,
                startedAt,
                MaintenanceWorkflowStepStatus.Failed,
                "节点类型不存在。"
            );

        if (!catalogNode.IsExecutable)
            return StepLog(
                step,
                displayName,
                startedAt,
                MaintenanceWorkflowStepStatus.Skipped,
                catalogNode.DisabledReason
            );

        if (step.NodeType == "control.random")
            return StepLog(
                step,
                displayName,
                startedAt,
                MaintenanceWorkflowStepStatus.Success,
                "随机判断已完成。",
                1
            );

        if (step.NodeType == "control.manual_confirm")
            return StepLog(
                step,
                displayName,
                startedAt,
                MaintenanceWorkflowStepStatus.WaitingManualConfirm,
                "等待人工确认。"
            );

        if (step.NodeType == "control.wait")
        {
            var seconds = Math.Max(0, step.IntervalSeconds);
            if (seconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
            return StepLog(
                step,
                displayName,
                startedAt,
                MaintenanceWorkflowStepStatus.Success,
                $"已等待 {seconds} 秒。"
            );
        }

        if (step.NodeType == "control.log_result")
            return StepLog(
                step,
                displayName,
                startedAt,
                MaintenanceWorkflowStepStatus.Success,
                "已记录流程结果。"
            );

        if (request.DryRun)
            return StepLog(
                step,
                displayName,
                startedAt,
                MaintenanceWorkflowStepStatus.Success,
                "测试运行已完成，未执行实际操作。",
                1
            );

        try
        {
            if (
                await ExecuteGranularMaintenanceNodeAsync(
                    workflow,
                    step.NodeType,
                    cancellationToken
                ) is
                { } result
            )
            {
                return StepLog(
                    step,
                    displayName,
                    startedAt,
                    result.Status,
                    result.Message,
                    evidence: result.Evidence
                );
            }

            var appServiceResult = await ExecuteAppServiceNodeAsync(
                workflow,
                step.NodeType,
                cancellationToken
            );
            return StepLog(
                step,
                displayName,
                startedAt,
                appServiceResult.Status,
                appServiceResult.Message,
                evidence: appServiceResult.Evidence
            );
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Maintenance workflow node {NodeType} failed", step.NodeType);
            return StepLog(
                step,
                displayName,
                startedAt,
                MaintenanceWorkflowStepStatus.Failed,
                ex.Message
            );
        }
    }

    private async Task<AppServiceExecutionResult> ExecuteAppServiceNodeAsync(
        MaintenanceWorkflowDefinition workflow,
        string nodeType,
        CancellationToken cancellationToken
    )
    {
        if (_serviceProvider is null)
            throw new InvalidOperationException("任务服务暂不可用，请稍后重试。");

        using var scope = _serviceProvider.CreateScope();
        var appServiceType = nodeType switch
        {
            "task.daily" => typeof(IDailyTaskAppService),
            "task.manga" => typeof(IMangaTaskAppService),
            "task.manga_privilege" => typeof(IMangaPrivilegeTaskAppService),
            "task.vip_privilege" => typeof(IVipPrivilegeTaskAppService),
            "task.vip_big_point" => typeof(IVipBigPointAppService),
            "task.silver2coin" => typeof(ISilver2CoinTaskAppService),
            "task.charge" => typeof(IChargeTaskAppService),
            "task.live_fans_medal" => typeof(ILiveFansMedalAppService),
            "task.live_lottery" => typeof(ILiveLotteryTaskAppService),
            "task.unfollow_batched" => typeof(IUnfollowBatchedTaskAppService),
            _ => null,
        };

        if (appServiceType is null)
            throw new InvalidOperationException("当前动作不可用，请重新选择养号动作。");

        var service = (IAppService)scope.ServiceProvider.GetRequiredService(appServiceType);
        if (service is not BaseMultiAccountsAppService accountService)
            throw new InvalidOperationException("当前动作暂不支持按账号执行。");

        var cookies = await GetTargetCookiesAsync(
            scope.ServiceProvider,
            workflow,
            cancellationToken
        );
        if (cookies.Count == 0)
            throw new InvalidOperationException("没有可用账号，请先在账号管理中完成登录。");

        var actionName =
            BuildNodeCatalog().FirstOrDefault(item => item.NodeType == nodeType)?.DisplayName
            ?? "养号任务";
        var evidence = new List<MaintenanceWorkflowActionEvidence>();
        foreach (var cookie in cookies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await accountService.DoTaskForAccountAsync(cookie, cancellationToken);
                evidence.Add(
                    new MaintenanceWorkflowActionEvidence
                    {
                        EvidenceId = Guid.NewGuid().ToString("N"),
                        AccountUid = cookie.UserId,
                        Action = actionName,
                        Outcome = "completed",
                        Attempt = 1,
                        Quantity = 1,
                        Unit = "次",
                        ConfirmationMethod = "task_completion",
                        Detail = "该账号的任务已执行完成，可结合运行前后状态核验变化。",
                        OccurredAt = DateTimeOffset.Now,
                    }
                );
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex,
                    "Account {AccountUid} failed while executing {NodeType}",
                    cookie.UserId,
                    nodeType
                );
                evidence.Add(FailedEvidence(cookie, actionName, ex.Message));
            }
        }

        var failedCount = evidence.Count(item => item.Outcome == "failed");
        var completedCount = evidence.Count - failedCount;
        return new AppServiceExecutionResult
        {
            Status =
                failedCount > 0
                    ? MaintenanceWorkflowStepStatus.Failed
                    : MaintenanceWorkflowStepStatus.Success,
            Message =
                failedCount == 0
                    ? $"已完成 {completedCount} 个账号的任务。"
                    : $"已处理 {evidence.Count} 个账号：{completedCount} 个完成，{failedCount} 个失败。",
            Evidence = evidence,
        };
    }

    private async Task<GranularExecutionResult?> ExecuteGranularMaintenanceNodeAsync(
        MaintenanceWorkflowDefinition workflow,
        string nodeType,
        CancellationToken cancellationToken
    )
    {
        if (!IsGranularNode(nodeType))
            return null;

        if (_serviceProvider is null)
            throw new InvalidOperationException("服务容器不可用，无法执行随机养号节点。");

        using var scope = _serviceProvider.CreateScope();
        var cookies = await GetTargetCookiesAsync(
            scope.ServiceProvider,
            workflow,
            cancellationToken
        );
        if (cookies.Count == 0)
            throw new InvalidOperationException("没有可用账号，请先在账号管理中完成登录。");

        var evidence = new List<MaintenanceWorkflowActionEvidence>();
        foreach (var cookie in cookies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                evidence.Add(
                    await ExecuteGranularNodeForCookieAsync(
                        scope.ServiceProvider,
                        nodeType,
                        cookie,
                        cancellationToken
                    )
                );
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex,
                    "Account {AccountUid} failed while executing {NodeType}",
                    cookie.UserId,
                    nodeType
                );
                var actionName =
                    BuildNodeCatalog()
                        .FirstOrDefault(item => item.NodeType == nodeType)
                        ?.DisplayName
                    ?? "养号动作";
                evidence.Add(FailedEvidence(cookie, actionName, ex.Message));
            }
        }

        var confirmedCount = evidence.Count(item =>
            item.Outcome.Equals("platform_confirmed", StringComparison.OrdinalIgnoreCase)
            && item.PlatformCode == 0
        );
        var failedCount = evidence.Count(item =>
            item.Outcome.Equals("failed", StringComparison.OrdinalIgnoreCase)
        );
        var pendingCount = evidence.Count - confirmedCount - failedCount;
        return new GranularExecutionResult
        {
            Status =
                failedCount > 0
                    ? MaintenanceWorkflowStepStatus.Failed
                    : MaintenanceWorkflowStepStatus.Success,
            Message =
                failedCount > 0
                    ? $"已处理 {evidence.Count} 个账号：{confirmedCount} 条平台已确认，{failedCount} 条失败。"
                : pendingCount == 0 ? $"已完成 {confirmedCount} 条养号动作，平台均已确认。"
                : $"已发起 {evidence.Count} 条养号动作，其中 {confirmedCount} 条平台已确认，{pendingCount} 条等待核验。",
            Evidence = evidence,
        };
    }

    private static bool IsGranularNode(string nodeType)
    {
        return nodeType
            is "daily.watch_share"
                or "daily.random_watch"
                or "daily.random_like"
                or "daily.random_share"
                or "daily.donate_coin"
                or "social.random_follow"
                or "live_fans_medal.danmaku"
                or "live_fans_medal.like"
                or "live_fans_medal.heartbeat"
                or "live_lottery.tianxuan";
    }

    private static async Task<MaintenanceWorkflowActionEvidence> ExecuteGranularNodeForCookieAsync(
        IServiceProvider provider,
        string nodeType,
        BiliCookie cookie,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (nodeType)
        {
            case "daily.watch_share":
                return await WatchAndShareRandomRankingVideoAsync(provider, cookie);
            case "daily.random_watch":
                return await WatchRandomRankingVideoAsync(provider, cookie);
            case "daily.random_like":
                return await LikeRandomRankingVideoAsync(provider, cookie);
            case "daily.random_share":
                return await ShareRandomRankingVideoAsync(provider, cookie);
            case "daily.donate_coin":
                return await DonateCoinToRandomVideoAsync(provider, cookie);
            case "social.random_follow":
                return await FollowRandomRankingVideoOwnerAsync(provider, cookie);
            case "live_fans_medal.danmaku":
                await provider
                    .GetRequiredService<ILiveDomainService>()
                    .SendDanmakuToFansMedalLive(cookie);
                return SubmittedEvidence(cookie, "发送直播弹幕");
            case "live_fans_medal.like":
                await provider.GetRequiredService<ILiveDomainService>().LikeFansMedalLive(cookie);
                return SubmittedEvidence(cookie, "点赞直播间");
            case "live_fans_medal.heartbeat":
                await provider
                    .GetRequiredService<ILiveDomainService>()
                    .SendHeartBeatToFansMedalLive(cookie);
                return SubmittedEvidence(cookie, "直播时长挂机");
            case "live_lottery.tianxuan":
                await provider.GetRequiredService<ILiveDomainService>().TianXuan(cookie);
                return SubmittedEvidence(cookie, "参与天选抽奖");
            default:
                throw new InvalidOperationException($"不支持的随机养号节点：{nodeType}");
        }
    }

    private static async Task<MaintenanceWorkflowActionEvidence> WatchAndShareRandomRankingVideoAsync(
        IServiceProvider provider,
        BiliCookie cookie
    )
    {
        var videoService = provider.GetRequiredService<IVideoDomainService>();
        var video = await GetRandomRankingVideoInfoAsync(videoService);
        var watchReceipt = await videoService.WatchVideoWithReceipt(video, cookie);
        var shareReceipt = await videoService.ShareVideoWithReceipt(video, cookie);
        return ConfirmedVideoEvidence(
            cookie,
            "随机观看并分享",
            video,
            watchReceipt,
            $"观看 {watchReceipt.Quantity} 秒；分享结果：{shareReceipt.PlatformMessage}"
        );
    }

    private static async Task<MaintenanceWorkflowActionEvidence> WatchRandomRankingVideoAsync(
        IServiceProvider provider,
        BiliCookie cookie
    )
    {
        var videoService = provider.GetRequiredService<IVideoDomainService>();
        var video = await GetRandomRankingVideoInfoAsync(videoService);
        var receipt = await videoService.WatchVideoWithReceipt(video, cookie);
        return ConfirmedVideoEvidence(cookie, "随机观看视频", video, receipt);
    }

    private static async Task<MaintenanceWorkflowActionEvidence> ShareRandomRankingVideoAsync(
        IServiceProvider provider,
        BiliCookie cookie
    )
    {
        var videoService = provider.GetRequiredService<IVideoDomainService>();
        var video = await GetRandomRankingVideoInfoAsync(videoService);
        var receipt = await videoService.ShareVideoWithReceipt(video, cookie);
        return ConfirmedVideoEvidence(cookie, "随机分享视频", video, receipt);
    }

    private static async Task<MaintenanceWorkflowActionEvidence> LikeRandomRankingVideoAsync(
        IServiceProvider provider,
        BiliCookie cookie
    )
    {
        var videoService = provider.GetRequiredService<IVideoDomainService>();
        var video = await GetRandomRankingVideoInfoAsync(videoService);
        var receipt = await videoService.LikeVideoWithReceipt(video, cookie);
        return ConfirmedVideoEvidence(cookie, "随机点赞视频", video, receipt);
    }

    private static async Task<MaintenanceWorkflowActionEvidence> DonateCoinToRandomVideoAsync(
        IServiceProvider provider,
        BiliCookie cookie
    )
    {
        var coinService = provider.GetRequiredService<IDonateCoinDomainService>();
        var video =
            await coinService.TryGetCanDonatedVideo(cookie)
            ?? throw new InvalidOperationException("没有找到当前可投币的视频。");
        if (!await coinService.DoAddCoinForVideo(video, false, cookie))
            throw new InvalidOperationException("平台未确认本次投币，请查看账号余额和今日限制。");

        return ConfirmedEvidence(
            cookie,
            "随机投币",
            video.Bvid,
            video.Title,
            VideoUrl(video.Bvid),
            quantity: 1,
            unit: "枚"
        );
    }

    private static async Task<MaintenanceWorkflowActionEvidence> FollowRandomRankingVideoOwnerAsync(
        IServiceProvider provider,
        BiliCookie cookie
    )
    {
        var videoService = provider.GetRequiredService<IVideoDomainService>();
        var relationApi = provider.GetRequiredService<IRelationApi>();
        var ranking = await videoService.GetRandomVideoOfRanking();
        var detail = await videoService.GetVideoDetail(ranking.Aid.ToString());
        var ownerMid = detail.Owner?.Mid ?? 0;
        if (ownerMid <= 0)
            throw new InvalidOperationException("随机视频没有返回 UP 主信息，无法执行随机关注。");
        if (ownerMid.ToString() == cookie.UserId)
            throw new InvalidOperationException("随机命中当前账号自己，已跳过关注。");

        var request = new ModifyRelationRequest(ownerMid, cookie.BiliJct) { Act = 1 };
        var referer = string.Format(RelationApiConstant.ModifyReferer, cookie.UserId, 0);
        var response = await relationApi.ModifyRelation(request, cookie.ToString(), referer);
        if (response.Code != 0)
            throw new InvalidOperationException($"随机关注失败：{response.Message}");
        return ConfirmedEvidence(
            cookie,
            "随机关注 UP",
            ownerMid.ToString(),
            string.IsNullOrWhiteSpace(detail.Owner?.Name) ? $"UP {ownerMid}" : detail.Owner.Name,
            $"https://space.bilibili.com/{ownerMid}",
            $"平台已确认关注，来源视频 {ranking.Bvid}《{ranking.Title}》",
            quantity: 1,
            unit: "次"
        );
    }

    private static async Task<VideoInfoDto> GetRandomRankingVideoInfoAsync(
        IVideoDomainService videoService
    )
    {
        var ranking = await videoService.GetRandomVideoOfRanking();
        return new VideoInfoDto
        {
            Aid = ranking.Aid.ToString(),
            Bvid = ranking.Bvid,
            Cid = ranking.Cid,
            Copyright = ranking.Copyright,
            Duration = ranking.Duration,
            Title = ranking.Title,
        };
    }

    private static MaintenanceWorkflowActionEvidence ConfirmedVideoEvidence(
        BiliCookie cookie,
        string action,
        VideoInfoDto video,
        VideoActionReceipt receipt,
        string detail = ""
    ) =>
        ConfirmedEvidence(
            cookie,
            action,
            video.Bvid,
            video.Title,
            VideoUrl(video.Bvid),
            string.IsNullOrWhiteSpace(detail) ? "平台已确认完成" : detail,
            receipt.PlatformCode,
            receipt.PlatformMessage,
            receipt.Quantity,
            receipt.Unit
        );

    private static MaintenanceWorkflowActionEvidence ConfirmedEvidence(
        BiliCookie cookie,
        string action,
        string targetId = "",
        string targetTitle = "",
        string targetUrl = "",
        string detail = "平台已确认完成",
        int? platformCode = 0,
        string platformMessage = "",
        int quantity = 1,
        string unit = "次"
    ) =>
        new()
        {
            EvidenceId = Guid.NewGuid().ToString("N"),
            AccountUid = cookie.UserId,
            Action = action,
            TargetId = targetId,
            TargetTitle = targetTitle,
            TargetUrl = targetUrl,
            Outcome = "platform_confirmed",
            Attempt = 1,
            Quantity = quantity,
            Unit = unit,
            PlatformCode = platformCode,
            PlatformMessage = platformMessage,
            ConfirmationMethod = "platform_response",
            Detail = detail,
            OccurredAt = DateTimeOffset.Now,
        };

    private static MaintenanceWorkflowActionEvidence SubmittedEvidence(
        BiliCookie cookie,
        string action,
        string targetId = "",
        string targetTitle = "",
        string targetUrl = ""
    ) =>
        new()
        {
            EvidenceId = Guid.NewGuid().ToString("N"),
            AccountUid = cookie.UserId,
            Action = action,
            TargetId = targetId,
            TargetTitle = targetTitle,
            TargetUrl = targetUrl,
            Outcome = "submitted",
            Attempt = 1,
            Quantity = 1,
            Unit = "次",
            ConfirmationMethod = "local_submission",
            Detail = "动作已发起，但平台没有提供可回读结果",
            OccurredAt = DateTimeOffset.Now,
        };

    private static MaintenanceWorkflowActionEvidence FailedEvidence(
        BiliCookie cookie,
        string action,
        string detail
    ) =>
        new()
        {
            EvidenceId = Guid.NewGuid().ToString("N"),
            AccountUid = cookie.UserId,
            Action = action,
            Outcome = "failed",
            Attempt = 1,
            Quantity = 0,
            Unit = "次",
            ConfirmationMethod = "execution_error",
            Detail = string.IsNullOrWhiteSpace(detail) ? "该账号的动作未能完成。" : detail,
            OccurredAt = DateTimeOffset.Now,
        };

    private static string VideoUrl(string bvid) =>
        string.IsNullOrWhiteSpace(bvid) ? "" : $"https://www.bilibili.com/video/{bvid}";

    private async Task<IReadOnlyList<BiliCookie>> GetTargetCookiesAsync(
        IServiceProvider provider,
        MaintenanceWorkflowDefinition workflow,
        CancellationToken cancellationToken
    )
    {
        var factory = provider.GetRequiredService<CookieStrFactory<BiliCookie>>();
        var selectedAccountIds = workflow
            .AccountIds.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string>? eligibleUids = null;
        var eligibleAccountsByUid = new Dictionary<string, ContentAutomationAccount>(
            StringComparer.OrdinalIgnoreCase
        );

        var accountBridge = provider.GetService<IContentAutomationBridge>();
        if (accountBridge is not null)
        {
            try
            {
                var accounts = await accountBridge.GetAccountsAsync(cancellationToken);
                var eligibleAccounts = accounts
                    .Where(account =>
                        account.Enabled
                        && IsLoggedIn(account.LoginStatus)
                        && !string.IsNullOrWhiteSpace(account.Uid)
                        && (
                            selectedAccountIds.Count == 0
                            || selectedAccountIds.Contains(account.Id)
                            || selectedAccountIds.Contains(account.Uid)
                        )
                    )
                    .ToList();
                eligibleUids = eligibleAccounts
                    .Select(account => account.Uid.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                eligibleAccountsByUid = eligibleAccounts
                    .GroupBy(account => account.Uid.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.First(),
                        StringComparer.OrdinalIgnoreCase
                    );
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Unable to resolve eligible workflow accounts");
                return [];
            }
        }
        else if (selectedAccountIds.Count > 0)
        {
            eligibleUids = selectedAccountIds;
        }

        var result = new List<BiliCookie>();
        for (var i = 0; i < factory.Count; i++)
        {
            var cookie = factory.GetCookie(i);
            if (eligibleUids is null || eligibleUids.Contains(cookie.UserId))
                result.Add(cookie);
        }

        var userInfoApi = provider.GetService<IUserInfoApi>();
        if (accountBridge is null || userInfoApi is null)
            return result;

        var verified = new List<BiliCookie>();
        foreach (var cookie in result)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await userInfoApi.LoginByCookie(cookie.ToString());
                var platformUid = response.Data?.Mid.ToString(CultureInfo.InvariantCulture) ?? "";
                if (
                    response.Code == 0
                    && response.Data?.IsLogin == true
                    && (platformUid.Length == 0 || SameId(platformUid, cookie.UserId))
                )
                {
                    verified.Add(cookie);
                    continue;
                }

                if (eligibleAccountsByUid.TryGetValue(cookie.UserId, out var account))
                    await MarkAccountLoginRequiredAsync(
                        provider,
                        accountBridge,
                        account,
                        cancellationToken
                    );
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex,
                    "Unable to verify platform session for account {AccountUid}",
                    cookie.UserId
                );
            }
        }

        return verified;
    }

    private async Task MarkAccountLoginRequiredAsync(
        IServiceProvider provider,
        IContentAutomationBridge accountBridge,
        ContentAutomationAccount account,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await accountBridge.UpdateAccountAsync(
                account.Id,
                new ContentAutomationAccountUpdateRequest
                {
                    LoginStatus = "login_required",
                    ClearCookie = true,
                },
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "Unable to mark account {AccountUid} as signed out",
                account.Uid
            );
        }

        try
        {
            if (provider.GetService<ILoginDomainService>() is { } loginDomainService)
                await loginDomainService.DeleteCookieFromJsonFileAsync(
                    account.Uid,
                    cancellationToken
                );
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "Unable to remove expired session for account {AccountUid}",
                account.Uid
            );
        }
    }

    private static bool IsLoggedIn(string status) =>
        status?.Trim().ToLowerInvariant() is "ok" or "logged_in" or "success";

    private async Task AppendAccountSnapshotsAsync(
        MaintenanceWorkflowRunRecord run,
        MaintenanceWorkflowDefinition workflow,
        string phase
    )
    {
        var snapshots = await CaptureAccountSnapshotsAsync(workflow, phase, CancellationToken.None);
        run.AccountSnapshots = run.AccountSnapshots.Concat(snapshots).ToList();
    }

    private async Task<
        IReadOnlyList<MaintenanceWorkflowAccountSnapshot>
    > CaptureAccountSnapshotsAsync(
        MaintenanceWorkflowDefinition workflow,
        string phase,
        CancellationToken cancellationToken
    )
    {
        if (_serviceProvider is null)
            return [];

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var cookies = await GetTargetCookiesAsync(
                scope.ServiceProvider,
                workflow,
                cancellationToken
            );
            if (cookies.Count == 0)
                return [];

            var userInfoApi = scope.ServiceProvider.GetRequiredService<IUserInfoApi>();
            var dailyTaskApi = scope.ServiceProvider.GetRequiredService<IDailyTaskApi>();
            var relationApi = scope.ServiceProvider.GetRequiredService<IRelationApi>();
            var snapshots = new List<MaintenanceWorkflowAccountSnapshot>();

            foreach (var cookie in cookies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = new MaintenanceWorkflowAccountSnapshot
                {
                    AccountUid = cookie.UserId,
                    Phase = phase,
                    CapturedAt = DateTimeOffset.Now,
                };
                var errors = new List<string>();

                try
                {
                    var response = await userInfoApi.LoginByCookie(cookie.ToString());
                    if (response.Code == 0 && response.Data?.IsLogin == true)
                    {
                        snapshot.Available = true;
                        snapshot.AccountName = response.Data.Uname ?? "";
                        snapshot.Level = response.Data.Level_info?.Current_level ?? 0;
                        snapshot.Experience = response.Data.Level_info?.Current_exp ?? 0;
                        snapshot.CoinBalance = response.Data.Money ?? 0;
                    }
                    else
                    {
                        errors.Add("登录状态未通过平台确认");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"账号状态读取失败：{ex.Message}");
                }

                try
                {
                    var response = await dailyTaskApi.GetDailyTaskRewardInfoAsync(
                        cookie.ToString()
                    );
                    if (response.Code == 0 && response.Data is not null)
                    {
                        snapshot.DailyLoginCompleted = response.Data.Login;
                        snapshot.DailyWatchCompleted = response.Data.Watch;
                        snapshot.DailyShareCompleted = response.Data.Share;
                        snapshot.DailyCoinExperience = response.Data.Coins;
                    }
                    else
                    {
                        errors.Add("每日任务状态暂时不可读");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"每日任务读取失败：{ex.Message}");
                }

                try
                {
                    var request = new GetFollowingsRequest(long.Parse(cookie.UserId)) { Ps = 1 };
                    var response = await relationApi.GetFollowings(request, cookie.ToString());
                    if (response.Code == 0 && response.Data is not null)
                        snapshot.FollowingCount = response.Data.Total;
                    else
                        errors.Add("关注数暂时不可读");
                }
                catch (Exception ex)
                {
                    errors.Add($"关注数读取失败：{ex.Message}");
                }

                snapshot.ErrorMessage = string.Join("；", errors);
                snapshots.Add(snapshot);
            }

            return snapshots;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "Failed to capture account snapshots for workflow {WorkflowId}",
                workflow.Id
            );
            return [];
        }
    }

    private void StoreRun(MaintenanceWorkflowRunRecord run)
    {
        lock (_lock)
        {
            _runs.RemoveAll(x => SameId(x.Id, run.Id));
            _runs.Add(run);
            SaveState();
        }
    }

    private MaintenanceWorkflowRunRecord FinishRunWithoutExecution(
        MaintenanceWorkflowRunRecord run,
        MaintenanceWorkflowRunStatus status,
        string reason
    )
    {
        run.Status = status;
        run.FailureReason = reason;
        run.EndedAt = DateTimeOffset.Now;
        StoreRun(run);
        return CloneRun(run);
    }

    private static bool TryIsWithinTimeWindow(
        string? value,
        TimeOnly current,
        out bool isWithinWindow
    )
    {
        isWithinWindow = true;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var parts = value.Split(
            ['-', '–', '—', '~'],
            2,
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
        );
        if (
            parts.Length != 2
            || !TimeOnly.TryParseExact(
                parts[0],
                ["H:mm", "HH:mm"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var start
            )
            || !TimeOnly.TryParseExact(
                parts[1],
                ["H:mm", "HH:mm"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var end
            )
        )
        {
            return false;
        }

        isWithinWindow =
            start == end
            || start < end && current >= start && current <= end
            || start > end && (current >= start || current <= end);
        return true;
    }

    private MaintenanceWorkflowDefinition NormalizeWorkflow(MaintenanceWorkflowDefinition workflow)
    {
        var now = DateTimeOffset.Now;
        var catalog = BuildNodeCatalog()
            .ToDictionary(x => x.NodeType, StringComparer.OrdinalIgnoreCase);
        var unavailableStep = workflow.Steps.FirstOrDefault(step =>
            string.IsNullOrWhiteSpace(step.NodeType) || !catalog.ContainsKey(step.NodeType.Trim())
        );
        if (unavailableStep is not null)
            throw new InvalidOperationException(
                $"流程中包含不可用的动作：{unavailableStep.DisplayName}。"
            );

        var steps = workflow
            .Steps.OrderBy(x => x.SortOrder)
            .Select(
                (step, index) =>
                {
                    catalog.TryGetValue(step.NodeType, out var node);
                    var normalized = CloneStep(step);
                    normalized.Id = string.IsNullOrWhiteSpace(normalized.Id)
                        ? NewId()
                        : normalized.Id.Trim();
                    normalized.NodeType = normalized.NodeType.Trim();
                    normalized.DisplayName =
                        node?.DisplayName
                        ?? (
                            string.IsNullOrWhiteSpace(normalized.DisplayName)
                                ? normalized.NodeType
                                : normalized.DisplayName.Trim()
                        );
                    normalized.Group =
                        node?.Group
                        ?? (
                            string.IsNullOrWhiteSpace(normalized.Group)
                                ? ""
                                : normalized.Group.Trim()
                        );
                    normalized.Permission = string.IsNullOrWhiteSpace(normalized.Permission)
                        ? node?.Permission ?? ""
                        : normalized.Permission.Trim();
                    if (!IsGranularNode(normalized.NodeType))
                    {
                        normalized.Parameters.Remove("countMin");
                        normalized.Parameters.Remove("countMax");
                        normalized.Parameters.Remove("waitMinSeconds");
                        normalized.Parameters.Remove("waitMaxSeconds");
                        normalized.Parameters.Remove("dailyLimit");
                    }
                    normalized.SortOrder = index + 1;
                    normalized.FailureStrategy = NormalizeFailureStrategy(
                        normalized.FailureStrategy
                    );
                    normalized.IntervalSeconds = Math.Max(0, normalized.IntervalSeconds);
                    normalized.RetryCount = Math.Max(0, normalized.RetryCount);
                    if (normalized.X == 0 && normalized.Y == 0)
                    {
                        normalized.X = 160 + (index % 3) * 260;
                        normalized.Y = 120 + (index / 3) * 170;
                    }
                    return normalized;
                }
            )
            .ToList();
        var stepIds = steps.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceEdges = workflow.Edges ?? Array.Empty<MaintenanceWorkflowEdge>();
        var edges = sourceEdges
            .Select(CloneEdge)
            .Where(edge =>
                stepIds.Contains(edge.SourceStepId) && stepIds.Contains(edge.TargetStepId)
            )
            .Select(edge =>
            {
                edge.Id = string.IsNullOrWhiteSpace(edge.Id) ? NewId() : edge.Id.Trim();
                edge.SourceStepId = edge.SourceStepId.Trim();
                edge.TargetStepId = edge.TargetStepId.Trim();
                edge.Label = edge.Label?.Trim() ?? "";
                edge.SourceHandle = string.IsNullOrWhiteSpace(edge.SourceHandle)
                    ? "default"
                    : edge.SourceHandle.Trim();
                edge.TargetHandle = string.IsNullOrWhiteSpace(edge.TargetHandle)
                    ? "default"
                    : edge.TargetHandle.Trim();
                return edge;
            })
            .GroupBy(
                edge => $"{edge.SourceStepId}|{edge.TargetStepId}|{edge.SourceHandle}",
                StringComparer.OrdinalIgnoreCase
            )
            .Select(group => group.First())
            .ToList();

        ValidateWorkflowGraph(steps, edges);

        return new MaintenanceWorkflowDefinition
        {
            SchemaVersion = MaintenanceWorkflowDefinition.CurrentSchemaVersion,
            Id = string.IsNullOrWhiteSpace(workflow.Id) ? NewId() : workflow.Id.Trim(),
            Name = string.IsNullOrWhiteSpace(workflow.Name) ? "未命名工作流" : workflow.Name.Trim(),
            IsEnabled = workflow.IsEnabled,
            AccountIds = NormalizeList(workflow.AccountIds),
            TimeWindow = workflow.TimeWindow?.Trim() ?? "",
            MaxRuntimeMinutes = Math.Max(1, workflow.MaxRuntimeMinutes),
            DefaultStepIntervalSeconds = Math.Max(0, workflow.DefaultStepIntervalSeconds),
            FailureStrategy = NormalizeFailureStrategy(workflow.FailureStrategy),
            Steps = steps,
            Edges = edges,
            ViewportX = workflow.ViewportX,
            ViewportY = workflow.ViewportY,
            ViewportZoom = Math.Clamp(
                workflow.ViewportZoom <= 0 ? 1 : workflow.ViewportZoom,
                0.25,
                2
            ),
            CreatedAt = workflow.CreatedAt == default ? now : workflow.CreatedAt,
            UpdatedAt = now,
        };
    }

    private void EnsureLoaded()
    {
        if (_loaded)
            return;

        lock (_lock)
        {
            if (_loaded)
                return;

            if (!string.IsNullOrWhiteSpace(_storePath) && File.Exists(_storePath))
            {
                try
                {
                    var json = File.ReadAllText(_storePath);
                    var state = JsonSerializer.Deserialize<PersistedState>(json, JsonOptions);
                    if (state is not null)
                    {
                        var requiresMigration =
                            !json.Contains("\"schemaVersion\"", StringComparison.OrdinalIgnoreCase)
                            || state.Workflows.Any(workflow =>
                                workflow.SchemaVersion
                                < MaintenanceWorkflowDefinition.CurrentSchemaVersion
                            );
                        if (requiresMigration)
                            BackupLegacyStore();

                        _workflows.Clear();
                        _workflows.AddRange(
                            requiresMigration
                                ? state.Workflows.Select(MigrateLegacyWorkflow)
                                : state.Workflows
                        );
                        _runs.Clear();
                        _runs.AddRange(state.Runs);
                        if (requiresMigration)
                            SaveState();
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(
                        ex,
                        "Failed to load maintenance workflow state from {Path}",
                        _storePath
                    );
                }
            }

            var stateChanged = false;
            if (_workflows.Count == 0)
            {
                _workflows.Add(CreateDefaultWorkflow());
                stateChanged = true;
            }

            var defaultWorkflow = _workflows.FirstOrDefault(workflow =>
                SameId(workflow.Id, "default-daily-maintenance")
            );
            if (
                defaultWorkflow is not null
                && string.Equals(defaultWorkflow.Name, "默认账号维护流程", StringComparison.Ordinal)
            )
            {
                defaultWorkflow.Name = "默认养号流程";
                stateChanged = true;
            }

            if (defaultWorkflow is not null && UpgradeLegacyDefaultWorkflow(defaultWorkflow))
                stateChanged = true;

            if (stateChanged)
                SaveState();

            _loaded = true;
        }
    }

    private void SaveState()
    {
        if (string.IsNullOrWhiteSpace(_storePath))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            var state = new PersistedState
            {
                Workflows = _workflows,
                Runs = _runs.TakeLast(200).ToList(),
            };
            var tempPath = $"{_storePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(tempPath, _storePath, true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "Failed to save maintenance workflow state to {Path}",
                _storePath
            );
        }
    }

    private void BackupLegacyStore()
    {
        if (string.IsNullOrWhiteSpace(_storePath) || !File.Exists(_storePath))
            return;

        var backupPath = $"{_storePath}.backup-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
        if (!File.Exists(backupPath))
            File.Copy(_storePath, backupPath);
    }

    private static MaintenanceWorkflowDefinition MigrateLegacyWorkflow(
        MaintenanceWorkflowDefinition workflow
    )
    {
        var allowedTypes = BuildNodeCatalog()
            .Select(node => node.NodeType)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var steps = workflow
            .Steps.Where(step => allowedTypes.Contains(step.NodeType))
            .OrderBy(step => step.SortOrder)
            .Select(CloneStep)
            .ToList();
        var stepIds = steps.Select(step => step.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edges = new List<MaintenanceWorkflowEdge>();
        foreach (var sourceEdge in workflow.Edges ?? [])
        {
            var edge = CloneEdge(sourceEdge);
            if (
                !stepIds.Contains(edge.SourceStepId)
                || !stepIds.Contains(edge.TargetStepId)
                || SameId(edge.SourceStepId, edge.TargetStepId)
            )
            {
                continue;
            }

            var source = steps.First(step => SameId(step.Id, edge.SourceStepId));
            var existingOutgoing = edges
                .Where(item => SameId(item.SourceStepId, edge.SourceStepId))
                .ToList();
            if (
                !source.NodeType.Equals("control.random", StringComparison.OrdinalIgnoreCase)
                && existingOutgoing.Count > 0
            )
                continue;
            if (
                source.NodeType.Equals("control.random", StringComparison.OrdinalIgnoreCase)
                && existingOutgoing.Count >= 2
            )
                continue;
            if (WouldCreateCycle(edges, edge))
                continue;

            edge.Id = string.IsNullOrWhiteSpace(edge.Id) ? NewId() : edge.Id;
            edges.Add(edge);
        }

        workflow.SchemaVersion = MaintenanceWorkflowDefinition.CurrentSchemaVersion;
        if (
            SameId(workflow.Id, "default-daily-maintenance")
            && string.Equals(workflow.Name, "默认账号维护流程", StringComparison.Ordinal)
        )
        {
            workflow.Name = "默认养号流程";
        }
        workflow.Steps = steps;
        workflow.Edges = edges;
        workflow.ViewportZoom =
            workflow.ViewportZoom <= 0 ? 1 : Math.Clamp(workflow.ViewportZoom, 0.25, 2);
        return workflow;
    }

    private static bool WouldCreateCycle(
        IReadOnlyList<MaintenanceWorkflowEdge> existingEdges,
        MaintenanceWorkflowEdge candidate
    )
    {
        var outgoing = existingEdges
            .Append(candidate)
            .GroupBy(edge => edge.SourceStepId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(edge => edge.TargetStepId).ToList(),
                StringComparer.OrdinalIgnoreCase
            );
        var stack = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        stack.Push(candidate.TargetStepId);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (SameId(current, candidate.SourceStepId))
                return true;
            if (!visited.Add(current) || !outgoing.TryGetValue(current, out var targets))
                continue;
            foreach (var target in targets)
                stack.Push(target);
        }

        return false;
    }

    private static MaintenanceWorkflowDefinition CreateDefaultWorkflow()
    {
        var now = DateTimeOffset.Now;
        const string dailyStepId = "default-daily";
        const string waitStepId = "default-wait";
        const string randomWatchStepId = "default-random-watch";
        const string resultStepId = "default-result";
        return new MaintenanceWorkflowDefinition
        {
            Id = "default-daily-maintenance",
            Name = "默认养号流程",
            IsEnabled = true,
            TimeWindow = "",
            MaxRuntimeMinutes = 120,
            DefaultStepIntervalSeconds = 30,
            FailureStrategy = "skip",
            CreatedAt = now,
            UpdatedAt = now,
            Steps =
            [
                new MaintenanceWorkflowStep
                {
                    Id = dailyStepId,
                    NodeType = "task.daily",
                    DisplayName = "每日任务",
                    Group = "任务组合",
                    SortOrder = 1,
                    FailureStrategy = "skip",
                    X = 120,
                    Y = 120,
                },
                new MaintenanceWorkflowStep
                {
                    Id = waitStepId,
                    NodeType = "control.wait",
                    DisplayName = "等待间隔",
                    Group = "控制节点",
                    SortOrder = 2,
                    IntervalSeconds = 30,
                    FailureStrategy = "skip",
                    X = 420,
                    Y = 120,
                },
                new MaintenanceWorkflowStep
                {
                    Id = randomWatchStepId,
                    NodeType = "daily.random_watch",
                    DisplayName = "随机观看视频",
                    Group = "随机互动",
                    SortOrder = 3,
                    FailureStrategy = "skip",
                    X = 720,
                    Y = 120,
                },
                new MaintenanceWorkflowStep
                {
                    Id = resultStepId,
                    NodeType = "control.log_result",
                    DisplayName = "记录结果",
                    Group = "控制节点",
                    SortOrder = 4,
                    FailureStrategy = "skip",
                    X = 1020,
                    Y = 120,
                },
            ],
            Edges =
            [
                new MaintenanceWorkflowEdge
                {
                    Id = "default-edge-daily-wait",
                    SourceStepId = dailyStepId,
                    TargetStepId = waitStepId,
                    Label = "next",
                },
                new MaintenanceWorkflowEdge
                {
                    Id = "default-edge-wait-random-watch",
                    SourceStepId = waitStepId,
                    TargetStepId = randomWatchStepId,
                    Label = "next",
                },
                new MaintenanceWorkflowEdge
                {
                    Id = "default-edge-random-watch-result",
                    SourceStepId = randomWatchStepId,
                    TargetStepId = resultStepId,
                    Label = "next",
                },
            ],
        };
    }

    private static bool UpgradeLegacyDefaultWorkflow(MaintenanceWorkflowDefinition workflow)
    {
        if (!SameId(workflow.Id, "default-daily-maintenance") || workflow.Steps.Count != 4)
            return false;

        var legacyStep = workflow.Steps.FirstOrDefault(step =>
            SameId(step.Id, "default-manga")
            && step.NodeType.Equals("task.manga", StringComparison.OrdinalIgnoreCase)
        );
        if (legacyStep is null)
            return false;

        const string newStepId = "default-random-watch";
        legacyStep.Id = newStepId;
        legacyStep.NodeType = "daily.random_watch";
        legacyStep.DisplayName = "随机观看视频";
        legacyStep.Group = "随机互动";
        legacyStep.Permission = "";
        if (string.Equals(workflow.TimeWindow, "09:00-18:00", StringComparison.Ordinal))
            workflow.TimeWindow = "";

        foreach (var edge in workflow.Edges)
        {
            if (SameId(edge.SourceStepId, "default-manga"))
                edge.SourceStepId = newStepId;
            if (SameId(edge.TargetStepId, "default-manga"))
                edge.TargetStepId = newStepId;
        }

        return true;
    }

    private static List<MaintenanceWorkflowNodeDefinition> BuildNodeCatalog()
    {
        var nodes = new List<MaintenanceWorkflowNodeDefinition>
        {
            Executable(
                "task.daily",
                "每日任务",
                "任务组合",
                "自动完成签到、观看、分享和投币等日常任务。"
            ),
            Executable("task.manga", "漫画任务", "任务组合", "自动完成漫画签到和阅读任务。"),
            Executable(
                "task.manga_privilege",
                "领取漫画权益",
                "任务组合",
                "领取当前账号可用的漫画权益。",
                permission: "big_vip"
            ),
            Executable(
                "task.vip_privilege",
                "领取会员福利",
                "任务组合",
                "领取当前账号可用的会员福利。",
                permission: "big_vip"
            ),
            Executable(
                "task.vip_big_point",
                "会员积分任务",
                "任务组合",
                "完成当前账号可参与的会员积分任务。",
                permission: "big_vip"
            ),
            Executable("task.silver2coin", "银瓜子兑换", "任务组合", "将可用银瓜子兑换为硬币。"),
            Executable(
                "task.charge",
                "B币券充电",
                "任务组合",
                "使用当月可用的 B 币券完成充电。",
                permission: "monthly_bcoin_coupon"
            ),
            Executable(
                "task.live_fans_medal",
                "直播粉丝牌任务",
                "任务组合",
                "完成粉丝牌直播间的日常互动。"
            ),
            Executable(
                "task.live_lottery",
                "直播抽奖",
                "任务组合",
                "参与当前账号符合条件的直播抽奖。"
            ),
            Executable(
                "task.unfollow_batched",
                "批量取关",
                "任务组合",
                "按已保存的规则清理关注列表。"
            ),
            Executable(
                "daily.watch_share",
                "随机观看并分享",
                "随机互动",
                "随机选择视频并执行观看、分享动作。"
            ),
            Executable(
                "daily.random_watch",
                "随机观看视频",
                "随机互动",
                "从热门内容中随机选择视频并上报观看进度。"
            ),
            Executable(
                "daily.random_like",
                "随机点赞视频",
                "随机互动",
                "从推荐内容中随机选择视频并点赞。"
            ),
            Executable(
                "daily.random_share",
                "随机分享视频",
                "随机互动",
                "从排行榜随机选择视频并执行分享。"
            ),
            Executable(
                "daily.donate_coin",
                "随机投币",
                "随机互动",
                "随机挑选可投币视频，并按养号配置执行投币。"
            ),
            Executable(
                "social.random_follow",
                "随机关注 UP",
                "随机互动",
                "从随机视频中识别 UP 主并执行关注。"
            ),
            Executable(
                "live_fans_medal.danmaku",
                "发送直播弹幕",
                "直播互动",
                "向粉丝牌直播间发送弹幕。"
            ),
            Executable("live_fans_medal.like", "点赞直播间", "直播互动", "点赞粉丝牌直播间。"),
            Executable(
                "live_fans_medal.heartbeat",
                "直播时长挂机",
                "直播互动",
                "发送直播心跳并累计观看时长。"
            ),
            Executable("live_lottery.tianxuan", "天选抽奖", "直播互动", "参与天选时刻抽奖。"),
            new()
            {
                NodeType = "control.random",
                DisplayName = "随机判断",
                Group = "控制节点",
                Description = "按设置的概率选择一个后续分支。",
                IsExecutable = true,
                SupportsRetry = false,
                ParameterSchema = new Dictionary<string, string>
                {
                    ["branchProbability"] = "命中概率",
                },
            },
            new()
            {
                NodeType = "control.wait",
                DisplayName = "等待间隔",
                Group = "控制节点",
                Description = "在两个动作之间等待一段时间。",
                IsExecutable = true,
                ParameterSchema = new Dictionary<string, string>
                {
                    ["intervalSeconds"] = "等待秒数",
                },
            },
            new()
            {
                NodeType = "control.log_result",
                DisplayName = "记录结果",
                Group = "控制节点",
                Description = "将当前执行结果写入任务记录。",
                IsExecutable = true,
                SupportsRetry = false,
            },
        };

        return nodes;
    }

    private static MaintenanceWorkflowNodeDefinition Executable(
        string nodeType,
        string displayName,
        string group,
        string description,
        string permission = ""
    )
    {
        return new MaintenanceWorkflowNodeDefinition
        {
            NodeType = nodeType,
            DisplayName = displayName,
            Group = group,
            Description = description,
            Permission = permission,
            IsExecutable = true,
        };
    }

    private static MaintenanceWorkflowStepLog StepLog(
        MaintenanceWorkflowStep step,
        string displayName,
        DateTimeOffset startedAt,
        MaintenanceWorkflowStepStatus status,
        string message,
        int executionCount = 0,
        IReadOnlyList<MaintenanceWorkflowActionEvidence>? evidence = null
    )
    {
        return new MaintenanceWorkflowStepLog
        {
            StepId = step.Id,
            NodeType = step.NodeType,
            DisplayName = displayName,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.Now,
            Status = status,
            ExecutionCount = executionCount,
            Message = message,
            Evidence = evidence ?? [],
        };
    }

    private static string NormalizeFailureStrategy(string value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "stop" => "stop",
            "retry" => "retry",
            _ => "skip",
        };
    }

    private static IReadOnlyList<string> NormalizeList(IEnumerable<string> values)
    {
        return values
            .Select(x => x?.Trim() ?? "")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<MaintenanceWorkflowStep> GetExecutionSteps(
        MaintenanceWorkflowDefinition workflow
    )
    {
        var ordered = workflow.Steps.OrderBy(x => x.SortOrder).ToList();
        var edges = workflow.Edges ?? Array.Empty<MaintenanceWorkflowEdge>();
        if (edges.Count == 0)
            return ordered;

        var byId = ordered.ToDictionary(step => step.Id, StringComparer.OrdinalIgnoreCase);
        var indegree = ordered.ToDictionary(
            step => step.Id,
            _ => 0,
            StringComparer.OrdinalIgnoreCase
        );
        var outgoing = ordered.ToDictionary(
            step => step.Id,
            _ => new List<string>(),
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var edge in edges)
        {
            if (!byId.ContainsKey(edge.SourceStepId) || !byId.ContainsKey(edge.TargetStepId))
                continue;
            indegree[edge.TargetStepId]++;
            outgoing[edge.SourceStepId].Add(edge.TargetStepId);
        }

        var available = ordered
            .Where(step => indegree[step.Id] == 0)
            .OrderBy(step => step.SortOrder)
            .ToList();
        var result = new List<MaintenanceWorkflowStep>();
        while (available.Count > 0)
        {
            var current = available[0];
            available.RemoveAt(0);
            result.Add(current);
            foreach (var targetId in outgoing[current.Id])
            {
                indegree[targetId]--;
                if (indegree[targetId] == 0)
                {
                    available.Add(byId[targetId]);
                    available.Sort((left, right) => left.SortOrder.CompareTo(right.SortOrder));
                }
            }
        }

        if (result.Count != ordered.Count)
            throw new InvalidOperationException("流程中存在循环连接，请调整节点关系后再保存。");
        return result;
    }

    private static void ValidateWorkflowGraph(
        IReadOnlyList<MaintenanceWorkflowStep> steps,
        IReadOnlyList<MaintenanceWorkflowEdge> edges
    )
    {
        var byId = steps.ToDictionary(step => step.Id, StringComparer.OrdinalIgnoreCase);
        if (edges.Any(edge => SameId(edge.SourceStepId, edge.TargetStepId)))
            throw new InvalidOperationException("节点不能连接到自身。");

        foreach (
            var group in edges.GroupBy(edge => edge.SourceStepId, StringComparer.OrdinalIgnoreCase)
        )
        {
            if (!byId.TryGetValue(group.Key, out var source))
                continue;

            var outgoing = group.ToList();
            if (
                !source.NodeType.Equals("control.random", StringComparison.OrdinalIgnoreCase)
                && outgoing.Count > 1
            )
            {
                throw new InvalidOperationException(
                    $"“{source.DisplayName}”只能连接一个后续步骤。"
                );
            }

            if (source.NodeType.Equals("control.random", StringComparison.OrdinalIgnoreCase))
            {
                if (outgoing.Count > 2)
                    throw new InvalidOperationException("随机判断最多连接两个分支。");

                var explicitBranches = outgoing
                    .Select(NormalizeBranchLabel)
                    .Where(label => label is "matched" or "otherwise")
                    .ToList();
                if (
                    explicitBranches.Count
                    != explicitBranches.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                )
                    throw new InvalidOperationException(
                        "随机判断的两个出口必须分别连接命中和未命中分支。"
                    );
            }
        }

        var workflow = new MaintenanceWorkflowDefinition { Steps = steps, Edges = edges };
        _ = GetExecutionSteps(workflow);
    }

    private static string SelectRandomBranch(MaintenanceWorkflowStep step)
    {
        var probability = GetIntParameter(step, "branchProbability", 50, 0, 100);
        return probability == 100 || (probability > 0 && Random.Shared.Next(1, 101) <= probability)
            ? "matched"
            : "otherwise";
    }

    private static string NormalizeBranchLabel(MaintenanceWorkflowEdge edge)
    {
        var value =
            !string.IsNullOrWhiteSpace(edge.SourceHandle)
            && !edge.SourceHandle.Equals("default", StringComparison.OrdinalIgnoreCase)
                ? edge.SourceHandle
                : edge.Label;
        return value?.Trim().ToLowerInvariant() switch
        {
            "matched" or "yes" or "true" => "matched",
            "otherwise" or "no" or "false" => "otherwise",
            _ => "default",
        };
    }

    private static int GetIntParameter(
        MaintenanceWorkflowStep step,
        string name,
        int defaultValue,
        int minimum,
        int maximum
    )
    {
        if (step.Parameters.TryGetValue(name, out var value) && int.TryParse(value, out var parsed))
            return Math.Clamp(parsed, minimum, maximum);
        return Math.Clamp(defaultValue, minimum, maximum);
    }

    private int CountSuccessfulExecutionsToday(string workflowId, string stepId)
    {
        var today = DateTimeOffset.Now.Date;
        lock (_lock)
        {
            return _runs
                .Where(run =>
                    SameId(run.WorkflowId, workflowId) && run.StartedAt.LocalDateTime.Date == today
                )
                .SelectMany(run => run.StepLogs)
                .Where(log =>
                    SameId(log.StepId, stepId)
                    && log.Status == MaintenanceWorkflowStepStatus.Success
                )
                .Sum(log => Math.Max(1, log.ExecutionCount));
        }
    }

    private static string ResolveStorePath(string configuredPath)
    {
        var value = string.IsNullOrWhiteSpace(configuredPath)
            ? "./config/maintenance-workflows.json"
            : configuredPath.Trim();
        return Path.GetFullPath(value, AppContext.BaseDirectory);
    }

    private static bool SameId(string left, string right)
    {
        return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static MaintenanceWorkflowDefinition CloneWorkflow(
        MaintenanceWorkflowDefinition workflow
    )
    {
        return new MaintenanceWorkflowDefinition
        {
            SchemaVersion = workflow.SchemaVersion,
            Id = workflow.Id,
            Name = workflow.Name,
            IsEnabled = workflow.IsEnabled,
            AccountIds = workflow.AccountIds.ToList(),
            TimeWindow = workflow.TimeWindow,
            MaxRuntimeMinutes = workflow.MaxRuntimeMinutes,
            DefaultStepIntervalSeconds = workflow.DefaultStepIntervalSeconds,
            FailureStrategy = workflow.FailureStrategy,
            Steps = workflow.Steps.Select(CloneStep).ToList(),
            Edges = workflow.Edges.Select(CloneEdge).ToList(),
            ViewportX = workflow.ViewportX,
            ViewportY = workflow.ViewportY,
            ViewportZoom = workflow.ViewportZoom,
            CreatedAt = workflow.CreatedAt,
            UpdatedAt = workflow.UpdatedAt,
        };
    }

    private static MaintenanceWorkflowStep CloneStep(MaintenanceWorkflowStep step)
    {
        return new MaintenanceWorkflowStep
        {
            Id = step.Id,
            NodeType = step.NodeType,
            DisplayName = step.DisplayName,
            Group = step.Group,
            Enabled = step.Enabled,
            SortOrder = step.SortOrder,
            Permission = step.Permission,
            FailureStrategy = step.FailureStrategy,
            RetryCount = step.RetryCount,
            IntervalSeconds = step.IntervalSeconds,
            MaxRuntimeSeconds = step.MaxRuntimeSeconds,
            X = step.X,
            Y = step.Y,
            Parameters = new Dictionary<string, string>(step.Parameters ?? []),
        };
    }

    private static MaintenanceWorkflowEdge CloneEdge(MaintenanceWorkflowEdge edge)
    {
        return new MaintenanceWorkflowEdge
        {
            Id = edge.Id,
            SourceStepId = edge.SourceStepId,
            TargetStepId = edge.TargetStepId,
            SourceHandle = edge.SourceHandle,
            TargetHandle = edge.TargetHandle,
            Label = edge.Label,
        };
    }

    private static MaintenanceWorkflowRunRecord CloneRun(MaintenanceWorkflowRunRecord run)
    {
        return new MaintenanceWorkflowRunRecord
        {
            Id = run.Id,
            WorkflowId = run.WorkflowId,
            WorkflowName = run.WorkflowName,
            TaskType = run.TaskType,
            DryRun = run.DryRun,
            TriggerSource = run.TriggerSource,
            StartedAt = run.StartedAt,
            EndedAt = run.EndedAt,
            Status = run.Status,
            SuccessCount = run.SuccessCount,
            FailedCount = run.FailedCount,
            SkippedCount = run.SkippedCount,
            FailureReason = run.FailureReason,
            ExecutionConfig = run.ExecutionConfig,
            AccountSnapshots = run.AccountSnapshots.Select(CloneSnapshot).ToList(),
            StepLogs = run
                .StepLogs.Select(x => new MaintenanceWorkflowStepLog
                {
                    StepId = x.StepId,
                    NodeType = x.NodeType,
                    DisplayName = x.DisplayName,
                    StartedAt = x.StartedAt,
                    EndedAt = x.EndedAt,
                    Status = x.Status,
                    ExecutionCount = x.ExecutionCount,
                    Message = x.Message,
                    Evidence = x.Evidence.Select(CloneEvidence).ToList(),
                })
                .ToList(),
        };
    }

    private static MaintenanceWorkflowAccountSnapshot CloneSnapshot(
        MaintenanceWorkflowAccountSnapshot snapshot
    ) =>
        new()
        {
            AccountUid = snapshot.AccountUid,
            AccountName = snapshot.AccountName,
            Phase = snapshot.Phase,
            CapturedAt = snapshot.CapturedAt,
            Available = snapshot.Available,
            Level = snapshot.Level,
            Experience = snapshot.Experience,
            CoinBalance = snapshot.CoinBalance,
            FollowingCount = snapshot.FollowingCount,
            DailyLoginCompleted = snapshot.DailyLoginCompleted,
            DailyWatchCompleted = snapshot.DailyWatchCompleted,
            DailyShareCompleted = snapshot.DailyShareCompleted,
            DailyCoinExperience = snapshot.DailyCoinExperience,
            ErrorMessage = snapshot.ErrorMessage,
        };

    private static MaintenanceWorkflowActionEvidence CloneEvidence(
        MaintenanceWorkflowActionEvidence evidence
    ) =>
        new()
        {
            EvidenceId = evidence.EvidenceId,
            AccountUid = evidence.AccountUid,
            Action = evidence.Action,
            TargetId = evidence.TargetId,
            TargetTitle = evidence.TargetTitle,
            TargetUrl = evidence.TargetUrl,
            Outcome = evidence.Outcome,
            Attempt = evidence.Attempt,
            Quantity = evidence.Quantity,
            Unit = evidence.Unit,
            PlatformCode = evidence.PlatformCode,
            PlatformMessage = evidence.PlatformMessage,
            ConfirmationMethod = evidence.ConfirmationMethod,
            Detail = evidence.Detail,
            OccurredAt = evidence.OccurredAt,
        };

    public async Task<bool> RunSingleStepAsync(
        string workflowId,
        string stepId,
        CancellationToken cancellationToken = default
    )
    {
        var workflow = await GetWorkflowAsync(workflowId, cancellationToken);
        if (workflow == null)
            return false;

        var step = workflow.Steps.FirstOrDefault(x => x.Id == stepId);
        if (step == null)
            return false;

        var request = new MaintenanceWorkflowRunRequest { DryRun = false };

        var log = await ExecuteStepAsync(workflow, step, request, cancellationToken);
        return log.Status == MaintenanceWorkflowStepStatus.Success;
    }

    private class PersistedState
    {
        public List<MaintenanceWorkflowDefinition> Workflows { get; set; } = [];

        public List<MaintenanceWorkflowRunRecord> Runs { get; set; } = [];
    }

    private sealed class GranularExecutionResult
    {
        public MaintenanceWorkflowStepStatus Status { get; init; }

        public string Message { get; init; } = "";

        public IReadOnlyList<MaintenanceWorkflowActionEvidence> Evidence { get; init; } = [];
    }

    private sealed class AppServiceExecutionResult
    {
        public MaintenanceWorkflowStepStatus Status { get; init; }

        public string Message { get; init; } = "";

        public IReadOnlyList<MaintenanceWorkflowActionEvidence> Evidence { get; init; } = [];
    }
}
