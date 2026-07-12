namespace Ray.BiliBiliTool.Application.Contracts.MaintenanceWorkflows;

public enum MaintenanceWorkflowRunStatus
{
    Pending,
    Running,
    WaitingManualConfirm,
    Completed,
    Failed,
    Cancelled,
    Skipped,
}

public enum MaintenanceWorkflowStepStatus
{
    Pending,
    Running,
    Success,
    Failed,
    Skipped,
    WaitingManualConfirm,
    Cancelled,
}

public class MaintenanceWorkflowNodeDefinition
{
    public string NodeType { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string Group { get; set; } = "";

    public string Description { get; set; } = "";

    public string Permission { get; set; } = "";

    public bool EnabledByDefault { get; set; } = true;

    public bool IsExecutable { get; set; } = true;

    public string DisabledReason { get; set; } = "";

    public bool SupportsRetry { get; set; } = true;

    public bool SupportsManualConfirm { get; set; }

    public Dictionary<string, string> ParameterSchema { get; set; } = [];
}

public class MaintenanceWorkflowDefinition
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public bool IsEnabled { get; set; } = true;

    public IReadOnlyList<string> AccountIds { get; set; } = [];

    public string TimeWindow { get; set; } = "";

    public int MaxRuntimeMinutes { get; set; } = 60;

    public int DefaultStepIntervalSeconds { get; set; } = 30;

    public string FailureStrategy { get; set; } = "stop";

    public IReadOnlyList<MaintenanceWorkflowStep> Steps { get; set; } = [];

    public IReadOnlyList<MaintenanceWorkflowEdge> Edges { get; set; } = [];

    public double ViewportX { get; set; }

    public double ViewportY { get; set; }

    public double ViewportZoom { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public class MaintenanceWorkflowStep
{
    public string Id { get; set; } = "";

    public string NodeType { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string Group { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public int SortOrder { get; set; }

    public string Permission { get; set; } = "";

    public string FailureStrategy { get; set; } = "skip";

    public int RetryCount { get; set; }

    public int IntervalSeconds { get; set; }

    public int MaxRuntimeSeconds { get; set; }

    public int X { get; set; }

    public int Y { get; set; }

    public Dictionary<string, string> Parameters { get; set; } = [];
}

public class MaintenanceWorkflowEdge
{
    public string Id { get; set; } = "";

    public string SourceStepId { get; set; } = "";

    public string TargetStepId { get; set; } = "";

    public string SourceHandle { get; set; } = "default";

    public string TargetHandle { get; set; } = "default";

    public string Label { get; set; } = "";
}

public class MaintenanceWorkflowRunRequest
{
    public bool DryRun { get; set; } = true;

    public string TriggerSource { get; set; } = "manual";

    public IReadOnlyDictionary<string, bool> PermissionSnapshot { get; set; } =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
}

public class MaintenanceWorkflowRunRecord
{
    public string Id { get; set; } = "";

    public string WorkflowId { get; set; } = "";

    public string WorkflowName { get; set; } = "";

    public string TaskType { get; set; } = "account-maintenance";

    public bool DryRun { get; set; }

    public string TriggerSource { get; set; } = "manual";

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public MaintenanceWorkflowRunStatus Status { get; set; } = MaintenanceWorkflowRunStatus.Pending;

    public int SuccessCount { get; set; }

    public int FailedCount { get; set; }

    public int SkippedCount { get; set; }

    public string FailureReason { get; set; } = "";

    public string ExecutionConfig { get; set; } = "";

    public IReadOnlyList<MaintenanceWorkflowAccountSnapshot> AccountSnapshots { get; set; } = [];

    public IReadOnlyList<MaintenanceWorkflowStepLog> StepLogs { get; set; } = [];
}

public class MaintenanceWorkflowAccountSnapshot
{
    public string AccountUid { get; set; } = "";

    public string AccountName { get; set; } = "";

    public string Phase { get; set; } = "before";

    public DateTimeOffset CapturedAt { get; set; }

    public bool Available { get; set; }

    public int Level { get; set; }

    public long Experience { get; set; }

    public decimal CoinBalance { get; set; }

    public int FollowingCount { get; set; }

    public bool DailyLoginCompleted { get; set; }

    public bool DailyWatchCompleted { get; set; }

    public bool DailyShareCompleted { get; set; }

    public long DailyCoinExperience { get; set; }

    public string ErrorMessage { get; set; } = "";
}

public class MaintenanceWorkflowStepLog
{
    public string StepId { get; set; } = "";

    public string NodeType { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public MaintenanceWorkflowStepStatus Status { get; set; } =
        MaintenanceWorkflowStepStatus.Pending;

    public int ExecutionCount { get; set; }

    public string Message { get; set; } = "";

    public IReadOnlyList<MaintenanceWorkflowActionEvidence> Evidence { get; set; } = [];
}

public class MaintenanceWorkflowActionEvidence
{
    public string EvidenceId { get; set; } = "";

    public string AccountUid { get; set; } = "";

    public string Action { get; set; } = "";

    public string TargetId { get; set; } = "";

    public string TargetTitle { get; set; } = "";

    public string TargetUrl { get; set; } = "";

    public string Outcome { get; set; } = "pending_confirmation";

    public int Attempt { get; set; } = 1;

    public int Quantity { get; set; }

    public string Unit { get; set; } = "";

    public int? PlatformCode { get; set; }

    public string PlatformMessage { get; set; } = "";

    public string ConfirmationMethod { get; set; } = "";

    public string Detail { get; set; } = "";

    public DateTimeOffset OccurredAt { get; set; }
}
