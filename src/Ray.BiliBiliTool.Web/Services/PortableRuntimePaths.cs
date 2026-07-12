namespace Ray.BiliBiliTool.Web.Services;

/// <summary>
/// Resolves the private runtimes bundled with the portable Windows package.
/// Development runs continue to use the developer's configured environment.
/// </summary>
public static class PortableRuntimePaths
{
    public static string ResolvePythonExecutable(string appBaseDirectory, string? configuredPython)
    {
        var privatePython = Path.Combine(appBaseDirectory, "Runtime", "python", "python.exe");
        if (File.Exists(privatePython))
            return privatePython;

        return string.IsNullOrWhiteSpace(configuredPython) ? "python" : configuredPython.Trim();
    }

    public static string? ResolvePlaywrightBrowsersPath(string appBaseDirectory)
    {
        var browserDirectory = Path.Combine(appBaseDirectory, "Runtime", "playwright-browsers");
        return Directory.Exists(browserDirectory) ? browserDirectory : null;
    }
}
