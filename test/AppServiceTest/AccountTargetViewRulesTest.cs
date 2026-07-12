using Ray.BiliBiliTool.Application.Contracts.ContentAutomation;
using Ray.BiliBiliTool.Application.Contracts.MaintenanceWorkflows;
using Ray.BiliBiliTool.Web.Services;

namespace AppServiceTest;

public sealed class AccountTargetViewRulesTest
{
    [Fact]
    public void CommentTargetUrl_ShouldPreferDirectCommentThenVideoThenBvid()
    {
        var record = new ContentAutomationLedgerRecord
        {
            CommentUrl = "https://www.bilibili.com/video/BV1DIRECT?comment_root_id=1",
            VideoUrl = "https://www.bilibili.com/video/BV1VIDEO",
            Bvid = "BV1FALLBACK",
        };

        Assert.Equal(record.CommentUrl, AccountTargetViewRules.CommentTargetUrl(record));
        record.CommentUrl = "";
        Assert.Equal(record.VideoUrl, AccountTargetViewRules.CommentTargetUrl(record));
        record.VideoUrl = "";
        Assert.Equal(
            "https://www.bilibili.com/video/BV1FALLBACK",
            AccountTargetViewRules.CommentTargetUrl(record)
        );
    }

    [Fact]
    public void WorkflowTargetUrl_ShouldFallBackOnlyForBvidTargets()
    {
        var evidence = new MaintenanceWorkflowActionEvidence
        {
            TargetUrl = "https://www.bilibili.com/video/BV1DIRECT",
            TargetId = "BV1FALLBACK",
        };

        Assert.Equal(evidence.TargetUrl, AccountTargetViewRules.WorkflowTargetUrl(evidence));
        evidence.TargetUrl = "";
        Assert.Equal(
            "https://www.bilibili.com/video/BV1FALLBACK",
            AccountTargetViewRules.WorkflowTargetUrl(evidence)
        );
        evidence.TargetId = "123456";
        Assert.Equal("", AccountTargetViewRules.WorkflowTargetUrl(evidence));
    }

    [Fact]
    public void AccountButtons_ShouldRequireBothAccountReferenceAndTargetUrl()
    {
        var comment = new ContentAutomationLedgerRecord
        {
            AccountId = "7",
            VideoUrl = "https://www.bilibili.com/video/BV1COMMENT",
        };
        var workflow = new MaintenanceWorkflowActionEvidence
        {
            AccountUid = "10001",
            TargetUrl = "https://www.bilibili.com/video/BV1WORKFLOW",
        };

        Assert.True(AccountTargetViewRules.CanOpenCommentWithAccount(comment));
        Assert.True(AccountTargetViewRules.CanOpenWorkflowWithAccount(workflow));
        comment.AccountId = "";
        workflow.TargetUrl = "";
        Assert.False(AccountTargetViewRules.CanOpenCommentWithAccount(comment));
        Assert.False(AccountTargetViewRules.CanOpenWorkflowWithAccount(workflow));
    }
}
