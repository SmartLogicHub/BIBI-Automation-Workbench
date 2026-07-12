namespace Ray.BiliBiliTool.Web.Components;

public static class StaticAssetVersion
{
    private static readonly string BuildFingerprint =
        typeof(StaticAssetVersion).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    private static readonly string AssemblyName =
        typeof(StaticAssetVersion).Assembly.GetName().Name ?? "Ray.BiliBiliTool.Web";

    public static string Url(string path) => $"{path}?v={BuildFingerprint}";

    public static string ScopedCssUrl() => Url($"{AssemblyName}.styles.css");
}
