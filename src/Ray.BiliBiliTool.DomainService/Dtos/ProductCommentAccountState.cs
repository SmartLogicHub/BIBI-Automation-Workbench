namespace Ray.BiliBiliTool.DomainService.Dtos;

public class ProductCommentAccountState
{
    public string AccountUserId { get; set; } = "";

    public int TodayCount { get; set; }

    public DateOnly StatDate { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? NextAvailableAt { get; set; }
}
