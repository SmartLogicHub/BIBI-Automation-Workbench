namespace Ray.BiliBiliTool.DomainService.Dtos;

public class ProductCommentLedgerRecord
{
    public string Id { get; set; } = "";

    public string AccountUserId { get; set; } = "";

    public string Bvid { get; set; } = "";

    public long Aid { get; set; }

    public string Url { get; set; } = "";

    public string Keyword { get; set; } = "";

    public string VideoTitle { get; set; } = "";

    public string Author { get; set; } = "";

    public string CommentText { get; set; } = "";

    public string Status { get; set; } = "";

    public int? ErrorCode { get; set; }

    public string ErrorMessage { get; set; } = "";

    public bool DryRun { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }
}
