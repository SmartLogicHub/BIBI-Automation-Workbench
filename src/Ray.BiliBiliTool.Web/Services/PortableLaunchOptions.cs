namespace Ray.BiliBiliTool.Web.Services;

public sealed record PortableLaunchOptions(
    string? Url,
    bool OpenBrowser,
    bool ExitWhenBrowserCloses
)
{
    private const string DefaultUrl = "http://127.0.0.1:5091";

    public static PortableLaunchOptions Create(
        bool isProjectRun,
        IEnumerable<string> arguments,
        bool isContainer = false
    )
    {
        if (isProjectRun || isContainer)
            return new PortableLaunchOptions(null, false, false);

        var suppliedArguments = arguments.ToArray();
        var hasCustomUrls = suppliedArguments.Any(argument =>
            string.Equals(argument, "--urls", StringComparison.OrdinalIgnoreCase)
            || argument.StartsWith("--urls=", StringComparison.OrdinalIgnoreCase)
        );
        var suppressBrowser = suppliedArguments.Any(argument =>
            string.Equals(argument, "--no-browser", StringComparison.OrdinalIgnoreCase)
        );

        var openBrowser = !suppressBrowser && !hasCustomUrls;
        return new PortableLaunchOptions(
            hasCustomUrls ? null : DefaultUrl,
            openBrowser,
            openBrowser
        );
    }
}
