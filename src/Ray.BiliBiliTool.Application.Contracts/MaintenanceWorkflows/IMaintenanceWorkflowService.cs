namespace Ray.BiliBiliTool.Application.Contracts.MaintenanceWorkflows;

public interface IMaintenanceWorkflowService
{
    Task<IReadOnlyList<MaintenanceWorkflowNodeDefinition>> GetNodeCatalogAsync(
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<MaintenanceWorkflowDefinition>> GetWorkflowsAsync(
        CancellationToken cancellationToken = default
    );

    Task<MaintenanceWorkflowDefinition?> GetWorkflowAsync(
        string workflowId,
        CancellationToken cancellationToken = default
    );

    Task<MaintenanceWorkflowDefinition> SaveWorkflowAsync(
        MaintenanceWorkflowDefinition workflow,
        CancellationToken cancellationToken = default
    );

    Task<MaintenanceWorkflowDefinition?> CopyWorkflowAsync(
        string workflowId,
        CancellationToken cancellationToken = default
    );

    Task<bool> DeleteWorkflowAsync(
        string workflowId,
        CancellationToken cancellationToken = default
    );

    Task<int> RemoveAccountAsync(string accountUid, CancellationToken cancellationToken = default);

    Task<MaintenanceWorkflowRunRecord> RunWorkflowAsync(
        string workflowId,
        MaintenanceWorkflowRunRequest request,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<MaintenanceWorkflowRunRecord>> GetRunsAsync(
        CancellationToken cancellationToken = default
    );

    Task<MaintenanceWorkflowRunRecord?> GetRunAsync(
        string runId,
        CancellationToken cancellationToken = default
    );

    Task<bool> StopRunAsync(string runId, CancellationToken cancellationToken = default);

    Task<bool> DeleteRunAsync(string runId, CancellationToken cancellationToken = default);

    Task<int> ClearRunsAsync(string workflowId, CancellationToken cancellationToken = default);

    Task<bool> RunSingleStepAsync(
        string workflowId,
        string stepId,
        CancellationToken cancellationToken = default
    );
}
