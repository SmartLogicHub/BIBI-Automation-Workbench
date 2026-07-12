using Ray.BiliBiliTool.Web.Services;

namespace AppServiceTest;

public sealed class GeneratedDataFileStoreTest : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"bibi-generated-files-{Guid.NewGuid():N}"
    );

    [Fact]
    public void List_ShouldOnlyReturnManagedBackupAndExportFiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, "backups"));
        Directory.CreateDirectory(Path.Combine(_root, "exports"));
        Directory.CreateDirectory(Path.Combine(_root, "other"));
        File.WriteAllText(Path.Combine(_root, "backups", "BIBI-backup-20260711.zip"), "backup");
        File.WriteAllText(Path.Combine(_root, "backups", "ignore.txt"), "ignore");
        File.WriteAllText(Path.Combine(_root, "exports", "BIBI-data-20260711.json"), "export");
        File.WriteAllText(Path.Combine(_root, "other", "hidden.zip"), "hidden");

        var files = new GeneratedDataFileStore(_root).List();

        Assert.Collection(
            files.OrderBy(x => x.Id),
            item => Assert.Equal("backups/BIBI-backup-20260711.zip", item.Id),
            item => Assert.Equal("exports/BIBI-data-20260711.json", item.Id)
        );
    }

    [Fact]
    public void Delete_ShouldRejectTraversalAndOnlyDeleteManagedFiles()
    {
        var backupDirectory = Path.Combine(_root, "backups");
        Directory.CreateDirectory(backupDirectory);
        var backup = Path.Combine(backupDirectory, "BIBI-backup-20260711.zip");
        var outside = Path.Combine(_root, "keep.txt");
        File.WriteAllText(backup, "backup");
        File.WriteAllText(outside, "keep");
        var store = new GeneratedDataFileStore(_root);

        Assert.False(store.Delete("../keep.txt"));
        Assert.True(File.Exists(outside));

        using (var download = store.Open("backups/BIBI-backup-20260711.zip"))
        {
            Assert.NotNull(download);
            Assert.Equal("BIBI-backup-20260711.zip", download.FileName);
        }
        Assert.Null(store.Open("../keep.txt"));

        Assert.True(store.Delete("backups/BIBI-backup-20260711.zip"));
        Assert.False(File.Exists(backup));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
