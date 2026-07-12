using Ray.BiliBiliTool.Agent.BiliBiliAgent.Services;

namespace Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.ProductComment;

public class SearchVideoRequestDto : IWrid
{
    public string search_type { get; set; } = "video";

    public string keyword { get; set; } = "";

    public int page { get; set; } = 1;

    public string order { get; set; } = "pubdate";

    public string? w_rid { get; set; } = "";

    public long wts { get; set; }
}
