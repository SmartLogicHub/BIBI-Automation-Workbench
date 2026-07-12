namespace Ray.BiliBiliTool.DomainService.Dtos;

public class ProductCommentPlanItem
{
    public string AccountUserId { get; set; } = "";

    public string Bvid { get; set; } = "";

    public long Aid { get; set; }

    public string Url { get; set; } = "";

    public string Keyword { get; set; } = "";

    public string VideoTitle { get; set; } = "";

    public string Author { get; set; } = "";

    public string TemplateText { get; set; } = "";

    public string CommentText { get; set; } = "";

    public bool DryRun { get; set; }
}
