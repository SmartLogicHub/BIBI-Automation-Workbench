using System;
using Xunit;

namespace LogTest;

/// <summary>Marks a test that sends a real notification to an external webhook.</summary>
public sealed class ExternalWebhookFactAttribute : FactAttribute
{
    public ExternalWebhookFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BIBI_RUN_WEBHOOK_TESTS"), "1", StringComparison.Ordinal))
        {
            Skip = "Set BIBI_RUN_WEBHOOK_TESTS=1 to send real external webhook notifications.";
        }
    }
}
