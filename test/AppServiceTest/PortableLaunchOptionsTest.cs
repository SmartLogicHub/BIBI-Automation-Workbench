using Ray.BiliBiliTool.Web.Services;

namespace AppServiceTest;

public sealed class PortableLaunchOptionsTest
{
    [Fact]
    public void Published_launch_uses_loopback_url_and_opens_browser()
    {
        var options = PortableLaunchOptions.Create(isProjectRun: false, arguments: []);

        Assert.Equal("http://127.0.0.1:5091", options.Url);
        Assert.True(options.OpenBrowser);
    }

    [Fact]
    public void Development_launch_does_not_override_host_or_open_browser()
    {
        var options = PortableLaunchOptions.Create(isProjectRun: true, arguments: []);

        Assert.Null(options.Url);
        Assert.False(options.OpenBrowser);
    }

    [Fact]
    public void No_browser_argument_suppresses_browser_opening()
    {
        var options = PortableLaunchOptions.Create(
            isProjectRun: false,
            arguments: ["--no-browser"]
        );

        Assert.False(options.OpenBrowser);
    }
}
