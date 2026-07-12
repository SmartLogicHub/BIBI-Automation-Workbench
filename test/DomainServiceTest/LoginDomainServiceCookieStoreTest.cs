using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.DomainService;
using Ray.BiliBiliTool.Infrastructure.Cookie;

namespace DomainServiceTest;

public class LoginDomainServiceCookieStoreTest
{
    [Fact]
    public async Task SaveCookieToJsonFileAsync_ShouldUpdateMinifiedJsonWithoutCorruption()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bibi-cookie-minified-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "data"));
        var path = Path.Combine(root, "data", "cookies.json");
        await File.WriteAllTextAsync(path, "{\"BiliBiliCookies\":[]}");

        try
        {
            var service = CreateService(root);

            await service.SaveCookieToJsonFileAsync(Cookie("100", "first"), default);

            var json = JObject.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal("100", UserIds(json).Single());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SaveCookieToJsonFileAsync_ShouldPreserveEveryAccountDuringConcurrentWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bibi-cookie-concurrent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "data"));
        var path = Path.Combine(root, "data", "cookies.json");
        await File.WriteAllTextAsync(path, "{\r\n}");

        try
        {
            var services = Enumerable.Range(1, 12).Select(_ => CreateService(root)).ToList();

            await Task.WhenAll(
                services.Select(
                    (service, index) =>
                        service.SaveCookieToJsonFileAsync(
                            Cookie((index + 1).ToString(), $"session-{index + 1}"),
                            default
                        )
                )
            );

            var json = JObject.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(
                Enumerable.Range(1, 12).Select(value => value.ToString()).OrderBy(value => value),
                UserIds(json).OrderBy(value => value)
            );
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DeleteCookieFromJsonFileAsync_ShouldKeepOtherAccountsIntact()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bibi-cookie-delete-{Guid.NewGuid():N}");
        try
        {
            var service = CreateService(root);
            await service.SaveCookieToJsonFileAsync(Cookie("100", "first"), default);
            await service.SaveCookieToJsonFileAsync(Cookie("200", "second"), default);

            await service.DeleteCookieFromJsonFileAsync("100", default);

            var path = Path.Combine(root, "data", "cookies.json");
            var json = JObject.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(["200"], UserIds(json));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static LoginDomainService CreateService(string root)
    {
        var environment = new TestHostEnvironment { ContentRootPath = root };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["PlatformType"] = "Web" })
            .Build();
        return new LoginDomainService(
            NullLogger<LoginDomainService>.Instance,
            null!,
            environment,
            null!,
            null!,
            configuration,
            Options.Create(new QingLongOptions())
        );
    }

    private static BiliCookie Cookie(string uid, string session) =>
        CookieStrFactory<BiliCookie>.CreateNew(
            $"DedeUserID={uid}; SESSDATA={session}; bili_jct=csrf-{uid}"
        );

    private static IReadOnlyList<string> UserIds(JObject json) =>
        (json["BiliBiliCookies"] as JArray ?? [])
            .Values<string>()
            .Select(value =>
                value!
                    .Split(
                        ';',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    )
                    .Single(part =>
                        part.StartsWith("DedeUserID=", StringComparison.OrdinalIgnoreCase)
                    )
                    .Split('=', 2)[1]
            )
            .ToList();

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "BIBI";

        public string ContentRootPath { get; set; } = "";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
