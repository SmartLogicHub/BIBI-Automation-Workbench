using BlazingQuartz.Core.History;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Ray.BiliBiliTool.Domain;
using Ray.BiliBiliTool.Infrastructure.EF;

namespace InfrastructureTest;

public class ExecutionLogRawSqlProviderTest
{
    [Fact]
    public void DeleteLogsByDays_ShouldUseExistingSqliteColumnName()
    {
        var provider = new BaseExecutionLogRawSqlProvider();

        Assert.Contains("DateAddedUtc", provider.DeleteLogsByDays);
        Assert.DoesNotContain("date_added_utc", provider.DeleteLogsByDays);
    }

    [Fact]
    public async Task DeleteLogsByDays_ShouldDeleteOnlyExpiredExecutionLogs()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bili-log-clean-{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Sqlite"] = $"Data Source={dbPath};Pooling=False",
                }
            )
            .Build();

        await using var dbContext = new BiliDbContext(config);
        try
        {
            await dbContext.Database.EnsureCreatedAsync();

            dbContext.ExecutionLogs.AddRange(
                new ExecutionLog
                {
                    RunInstanceId = "old-log",
                    LogType = LogType.ScheduleJob,
                    DateAddedUtc = DateTimeOffset.UtcNow.AddDays(-90),
                },
                new ExecutionLog
                {
                    RunInstanceId = "new-log",
                    LogType = LogType.ScheduleJob,
                    DateAddedUtc = DateTimeOffset.UtcNow,
                }
            );
            await dbContext.SaveChangesAsync();

            var store = new ExecutionLogStore(
                NullLogger<ExecutionLogStore>.Instance,
                dbContext,
                new BaseExecutionLogRawSqlProvider()
            );

            var deleted = await store.DeleteLogsByDays(30);

            Assert.Equal(1, deleted);
            Assert.DoesNotContain(dbContext.ExecutionLogs, x => x.RunInstanceId == "old-log");
            Assert.Contains(dbContext.ExecutionLogs, x => x.RunInstanceId == "new-log");
        }
        finally
        {
            await dbContext.DisposeAsync();
            File.Delete(dbPath);
        }
    }
}
