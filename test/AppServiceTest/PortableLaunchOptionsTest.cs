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
        Assert.True(options.ExitWhenBrowserCloses);
    }

    [Fact]
    public void Development_launch_does_not_override_host_or_open_browser()
    {
        var options = PortableLaunchOptions.Create(isProjectRun: true, arguments: []);

        Assert.Null(options.Url);
        Assert.False(options.OpenBrowser);
        Assert.False(options.ExitWhenBrowserCloses);
    }

    [Fact]
    public void No_browser_argument_suppresses_browser_opening()
    {
        var options = PortableLaunchOptions.Create(
            isProjectRun: false,
            arguments: ["--no-browser"]
        );

        Assert.False(options.OpenBrowser);
        Assert.False(options.ExitWhenBrowserCloses);
    }

    [Fact]
    public void Custom_urls_disable_browser_opening_and_automatic_exit()
    {
        var options = PortableLaunchOptions.Create(
            isProjectRun: false,
            arguments: ["--urls", "http://0.0.0.0:8080"]
        );

        Assert.Null(options.Url);
        Assert.False(options.OpenBrowser);
        Assert.False(options.ExitWhenBrowserCloses);
    }

    [Fact]
    public void Container_launch_does_not_enable_portable_browser_lifecycle()
    {
        var options = PortableLaunchOptions.Create(
            isProjectRun: false,
            arguments: [],
            isContainer: true
        );

        Assert.Null(options.Url);
        Assert.False(options.OpenBrowser);
        Assert.False(options.ExitWhenBrowserCloses);
    }
}
