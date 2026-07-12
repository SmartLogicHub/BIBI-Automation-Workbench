using Ray.BiliBiliTool.Web.Components;

namespace AppServiceTest;

public class StaticAssetVersionTest
{
    [Fact]
    public void Url_ShouldAppendStableBuildFingerprint()
    {
        var first = StaticAssetVersion.Url("workflow-editor/workflow-editor.js");
        var second = StaticAssetVersion.Url("workflow-editor/workflow-editor.js");

        Assert.Equal(first, second);
        Assert.Matches("^workflow-editor/workflow-editor\\.js\\?v=[0-9a-f]{32}$", first);
    }
}
