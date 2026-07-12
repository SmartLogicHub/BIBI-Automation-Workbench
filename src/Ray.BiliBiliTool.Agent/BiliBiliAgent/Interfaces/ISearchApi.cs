using Ray.BiliBiliTool.Agent.BiliBiliAgent.Attributes;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.ProductComment;
using WebApiClientCore.Attributes;

namespace Ray.BiliBiliTool.Agent.BiliBiliAgent.Interfaces;

[Header("Host", "api.bilibili.com")]
public interface ISearchApi : IBiliBiliApi
{
    [Header("Referer", "https://search.bilibili.com/")]
    [Header("Origin", "https://search.bilibili.com")]
    [HttpGet("/x/web-interface/wbi/search/type")]
    Task<BiliApiResponse<SearchVideoResultDto>> SearchVideosAsync(
        [WbiParameter] [PathQuery] SearchVideoRequestDto request,
        [Header("Cookie")] string ck
    );

    [Header("Referer", "https://www.bilibili.com/")]
    [HttpGet("/x/web-interface/view?bvid={bvid}")]
    Task<BiliApiResponse<VideoDetailByBvidDto>> GetVideoDetailByBvidAsync(
        string bvid,
        [Header("Cookie")] string ck
    );
}
