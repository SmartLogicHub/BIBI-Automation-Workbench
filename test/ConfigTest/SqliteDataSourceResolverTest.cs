using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Ray.BiliBiliTool.Config.SQLite;
using Xunit;

namespace ConfigTest;

public sealed class SqliteDataSourceResolverTest
{
    [Fact]
    public void NormalizeFileConnectionString_CreatesDataDirectoryInFreshPublishRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "bibi-sqlite-test",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            var normalized = SqliteDataSourceResolver.NormalizeFileConnectionString(
                "Data Source=./data/BiliBiliTool.db",
                root,
                out var dataSource
            );

            var expectedPath = Path.GetFullPath(Path.Combine(root, "data", "BiliBiliTool.db"));
            Assert.Equal(expectedPath, dataSource);
            Assert.True(Directory.Exists(Path.GetDirectoryName(expectedPath)));
            Assert.Equal(expectedPath, new SqliteConnectionStringBuilder(normalized).DataSource);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
