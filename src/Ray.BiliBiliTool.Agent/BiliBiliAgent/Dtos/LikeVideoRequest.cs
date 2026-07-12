namespace Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos;

public sealed class LikeVideoRequest(long aid, string csrf)
{
    public long Aid { get; set; } = aid;

    public int Like { get; set; } = 1;

    public string Csrf { get; set; } = csrf;
}
