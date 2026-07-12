using System.Text.Json;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos;
using Xunit;

namespace BiliAgentTest;

public class BiliApiResponseSerializationTest
{
    [Fact]
    public void ErrorResponseWithoutData_ShouldStillDeserialize()
    {
        const string json = """{"code":-352,"message":"-352"}""";

        var response = JsonSerializer.Deserialize<BiliApiResponse<Ranking>>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        Assert.NotNull(response);
        Assert.Equal(-352, response.Code);
        Assert.Null(response.Data);
    }
}
