using System.Reflection;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Attributes;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.ProductComment;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Interfaces;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Services;
using WebApiClientCore.Attributes;

namespace DomainServiceTest;

public class ProductCommentSearchApiContractTest
{
    [Fact]
    public void SearchVideoRequestDto_ShouldHaveWbiDefaults()
    {
        var request = new SearchVideoRequestDto();

        Assert.IsAssignableFrom<IWrid>(request);
        Assert.Equal("video", request.search_type);
        Assert.Equal("pubdate", request.order);
        Assert.Equal(1, request.page);
        Assert.Equal("", request.w_rid);
        Assert.Equal(0, request.wts);
    }

    [Fact]
    public void SearchVideosAsync_ShouldUseWbiParameterAndCookieHeader()
    {
        var method = typeof(ISearchApi).GetMethod(nameof(ISearchApi.SearchVideosAsync));

        Assert.NotNull(method);
        Assert.Contains(method!.GetCustomAttributes(), x => x is HttpGetAttribute);

        var parameters = method.GetParameters();
        Assert.Contains(parameters[0].GetCustomAttributes(), x => x is WbiParameterAttribute);
        Assert.Contains(parameters[1].GetCustomAttributes(), x => x is HeaderAttribute);
    }

    [Fact]
    public void GetVideoDetailByBvidAsync_ShouldUseBvidRouteAndCookieHeader()
    {
        var method = typeof(ISearchApi).GetMethod(nameof(ISearchApi.GetVideoDetailByBvidAsync));

        Assert.NotNull(method);
        Assert.Contains(method!.GetCustomAttributes(), x => x is HttpGetAttribute);

        var parameters = method.GetParameters();
        Assert.Contains(parameters[1].GetCustomAttributes(), x => x is HeaderAttribute);
    }
}
