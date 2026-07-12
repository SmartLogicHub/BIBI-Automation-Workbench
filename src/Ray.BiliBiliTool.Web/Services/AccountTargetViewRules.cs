using Ray.BiliBiliTool.Application.Contracts.ContentAutomation;
using Ray.BiliBiliTool.Application.Contracts.MaintenanceWorkflows;

namespace Ray.BiliBiliTool.Web.Services;

public static class AccountTargetViewRules
{
    public static string CommentTargetUrl(ContentAutomationLedgerRecord comment) =>
        !string.IsNullOrWhiteSpace(comment.CommentUrl) ? comment.CommentUrl.Trim()
        : !string.IsNullOrWhiteSpace(comment.VideoUrl) ? comment.VideoUrl.Trim()
        : VideoUrl(comment.Bvid);

    public static string WorkflowTargetUrl(MaintenanceWorkflowActionEvidence evidence) =>
        !string.IsNullOrWhiteSpace(evidence.TargetUrl)
            ? evidence.TargetUrl.Trim()
            : VideoUrl(evidence.TargetId);

    public static bool CanOpenCommentWithAccount(ContentAutomationLedgerRecord comment) =>
        !string.IsNullOrWhiteSpace(comment.AccountId)
        && !string.IsNullOrWhiteSpace(CommentTargetUrl(comment));

    public static bool CanOpenWorkflowWithAccount(MaintenanceWorkflowActionEvidence evidence) =>
        !string.IsNullOrWhiteSpace(evidence.AccountUid)
        && !string.IsNullOrWhiteSpace(WorkflowTargetUrl(evidence));

    private static string VideoUrl(string value)
    {
        var normalized = value?.Trim() ?? "";
        return normalized.StartsWith("BV", StringComparison.OrdinalIgnoreCase)
            ? $"https://www.bilibili.com/video/{normalized}"
            : "";
    }
}
