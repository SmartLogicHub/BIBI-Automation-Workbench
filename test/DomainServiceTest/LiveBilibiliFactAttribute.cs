namespace DomainServiceTest;

public sealed class LiveBilibiliFactAttribute : FactAttribute
{
    public LiveBilibiliFactAttribute()
    {
        if (
            !string.Equals(
                Environment.GetEnvironmentVariable("BIBI_RUN_LIVE_TESTS"),
                "1",
                StringComparison.Ordinal
            )
        )
        {
            Skip = "Set BIBI_RUN_LIVE_TESTS=1 to run tests that call the live Bilibili service.";
        }
    }
}
