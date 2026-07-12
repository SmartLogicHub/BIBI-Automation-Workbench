namespace Ray.BiliBiliTool.DomainService.Dtos;

public class ProductCommentCandidate
{
    public string Bvid { get; set; } = "";

    public long Aid { get; set; }

    public string Url { get; set; } = "";

    public string Title { get; set; } = "";

    public string Author { get; set; } = "";

    public string Keyword { get; set; } = "";

    public string Status { get; set; } = "pending";

    public string LastError { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
