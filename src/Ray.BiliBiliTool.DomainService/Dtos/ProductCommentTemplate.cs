namespace Ray.BiliBiliTool.DomainService.Dtos;

public class ProductCommentTemplate
{
    public string Id { get; set; } = "";

    public string Text { get; set; } = "";

    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
}
