using System;
using Xunit;

namespace Ray.BiliBiliTool.Agent.FunctionalTests;

/// <summary>Marks a test that calls the live Bilibili service or mutates account state.</summary>
public sealed class LiveBilibiliFactAttribute : FactAttribute
{
    public LiveBilibiliFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BIBI_RUN_LIVE_TESTS"), "1", StringComparison.Ordinal))
        {
            Skip = "Set BIBI_RUN_LIVE_TESTS=1 to run tests that call the live Bilibili service.";
        }
    }
}
