using Ray.BiliBiliTool.Web.Services;

namespace AppServiceTest;

public sealed class PortableRuntimePathsTest : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "bibi-portable-runtime-",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public void ResolvePythonExecutable_prefers_private_runtime_in_release_folder()
    {
        var runtimePython = Path.Combine(_root, "Runtime", "python", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(runtimePython)!);
        File.WriteAllText(runtimePython, string.Empty);

        var resolved = PortableRuntimePaths.ResolvePythonExecutable(
            _root,
            "C:\\developer\\python.exe"
        );

        Assert.Equal(runtimePython, resolved);
    }

    [Fact]
    public void ResolvePlaywrightBrowserPath_uses_private_browser_folder_when_present()
    {
        var browserDirectory = Path.Combine(_root, "Runtime", "playwright-browsers");
        Directory.CreateDirectory(browserDirectory);

        var resolved = PortableRuntimePaths.ResolvePlaywrightBrowsersPath(_root);

        Assert.Equal(browserDirectory, resolved);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
