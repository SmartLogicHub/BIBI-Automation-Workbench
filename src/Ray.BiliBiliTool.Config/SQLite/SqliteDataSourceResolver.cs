using Microsoft.Data.Sqlite;

namespace Ray.BiliBiliTool.Config.SQLite;

public static class SqliteDataSourceResolver
{
    public static string NormalizeFileConnectionString(
        string connectionString,
        string applicationRoot,
        out string dataSource
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationRoot);

        var builder = new SqliteConnectionStringBuilder(connectionString);
        var configuredDataSource = builder.DataSource?.Trim();
        if (string.IsNullOrWhiteSpace(configuredDataSource))
            throw new ArgumentException(
                "SQLite connection string must include a data source.",
                nameof(connectionString)
            );

        if (
            string.Equals(configuredDataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
            || configuredDataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
        )
        {
            dataSource = configuredDataSource;
            return builder.ToString();
        }

        var normalizedRoot = Path.GetFullPath(applicationRoot);
        dataSource = Path.GetFullPath(configuredDataSource, normalizedRoot);
        var directory = Path.GetDirectoryName(dataSource);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Unable to resolve the SQLite data directory.");

        Directory.CreateDirectory(directory);
        builder.DataSource = dataSource;
        return builder.ToString();
    }
}
