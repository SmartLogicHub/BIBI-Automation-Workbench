using System.Text.Json.Serialization;

namespace Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.ProductComment;

public class SearchVideoResultDto
{
    public List<SearchVideoItemDto> Result { get; set; } = [];

    [JsonPropertyName("numResults")]
    public int NumResults { get; set; }

    [JsonPropertyName("numPages")]
    public int NumPages { get; set; }

    public int Page { get; set; }
}

public class SearchVideoItemDto
{
    public long Aid { get; set; }

    public string Bvid { get; set; } = "";

    public string Title { get; set; } = "";

    public string? Author { get; set; }

    public long Mid { get; set; }

    public string? Arcurl { get; set; }

    public string? Pic { get; set; }

    public long Pubdate { get; set; }

    public string? Duration { get; set; }
}
