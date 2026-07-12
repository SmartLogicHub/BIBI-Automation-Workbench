using Microsoft.AspNetCore.Mvc;
using Ray.BiliBiliTool.Application.Contracts.MaintenanceWorkflows;

namespace Ray.BiliBiliTool.Web.Controllers;

[ApiController]
[Route("api/maintenance-workflows")]
public class MaintenanceWorkflowsController(IMaintenanceWorkflowService workflowService)
    : ControllerBase
{
    [HttpGet("nodes")]
    public async Task<IActionResult> GetNodeCatalog(CancellationToken cancellationToken)
    {
        return Ok(await workflowService.GetNodeCatalogAsync(cancellationToken));
    }

    [HttpGet]
    public async Task<IActionResult> GetWorkflows(CancellationToken cancellationToken)
    {
        return Ok(await workflowService.GetWorkflowsAsync(cancellationToken));
    }

    [HttpGet("{workflowId}")]
    public async Task<IActionResult> GetWorkflow(
        string workflowId,
        CancellationToken cancellationToken
    )
    {
        var workflow = await workflowService.GetWorkflowAsync(workflowId, cancellationToken);
        return workflow is null ? NotFound() : Ok(workflow);
    }

    [HttpPost]
    public async Task<IActionResult> SaveWorkflow(
        [FromBody] MaintenanceWorkflowDefinition workflow,
        CancellationToken cancellationToken
    )
    {
        var saved = await workflowService.SaveWorkflowAsync(workflow, cancellationToken);
        return Ok(saved);
    }

    [HttpPost("{workflowId}/copy")]
    public async Task<IActionResult> CopyWorkflow(
        string workflowId,
        CancellationToken cancellationToken
    )
    {
        var copied = await workflowService.CopyWorkflowAsync(workflowId, cancellationToken);
        return copied is null ? NotFound() : Ok(copied);
    }

    [HttpDelete("{workflowId}")]
    public async Task<IActionResult> DeleteWorkflow(
        string workflowId,
        CancellationToken cancellationToken
    )
    {
        var deleted = await workflowService.DeleteWorkflowAsync(workflowId, cancellationToken);
        return deleted ? Ok(new { success = true }) : NotFound();
    }

    [HttpPost("{workflowId}/test-run")]
    public async Task<IActionResult> TestRunWorkflow(
        string workflowId,
        [FromBody] MaintenanceWorkflowRunRequest? request,
        CancellationToken cancellationToken
    )
    {
        var runRequest = request ?? new MaintenanceWorkflowRunRequest();
        runRequest.DryRun = true;
        var run = await workflowService.RunWorkflowAsync(workflowId, runRequest, cancellationToken);
        return Ok(run);
    }

    [HttpPost("{workflowId}/run")]
    public async Task<IActionResult> RunWorkflow(
        string workflowId,
        [FromBody] MaintenanceWorkflowRunRequest? request,
        CancellationToken cancellationToken
    )
    {
        var run = await workflowService.RunWorkflowAsync(
            workflowId,
            request ?? new MaintenanceWorkflowRunRequest(),
            cancellationToken
        );
        return Ok(run);
    }

    [HttpGet("runs")]
    public async Task<IActionResult> GetRuns(CancellationToken cancellationToken)
    {
        return Ok(await workflowService.GetRunsAsync(cancellationToken));
    }

    [HttpGet("runs/{runId}")]
    public async Task<IActionResult> GetRun(string runId, CancellationToken cancellationToken)
    {
        var run = await workflowService.GetRunAsync(runId, cancellationToken);
        return run is null ? NotFound() : Ok(run);
    }

    [HttpPost("runs/{runId}/stop")]
    public async Task<IActionResult> StopRun(string runId, CancellationToken cancellationToken)
    {
        var stopped = await workflowService.StopRunAsync(runId, cancellationToken);
        return stopped ? Ok(new { success = true }) : NotFound();
    }

    [HttpDelete("runs/{runId}")]
    public async Task<IActionResult> DeleteRun(string runId, CancellationToken cancellationToken)
    {
        var deleted = await workflowService.DeleteRunAsync(runId, cancellationToken);
        return deleted ? Ok(new { success = true }) : Conflict(new { success = false });
    }

    [HttpDelete("{workflowId}/runs")]
    public async Task<IActionResult> ClearRuns(
        string workflowId,
        CancellationToken cancellationToken
    )
    {
        var removed = await workflowService.ClearRunsAsync(workflowId, cancellationToken);
        return Ok(new { success = true, removed });
    }
}
