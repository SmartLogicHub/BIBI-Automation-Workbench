using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Application.Contracts;
using Ray.BiliBiliTool.Config.Extensions;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.Console;
using Ray.BiliBiliTool.Infrastructure;
using Xunit;

namespace ConfigTest
{
    /// <summary>
    /// ProductCommentTaskOptions 配置绑定测试（Task 1 — GPT 修复版）
    /// 验证：
    ///   - 构造函数级别安全默认值（不依赖 appsettings.json）
    ///   - 配置节能正确绑定到 ProductCommentTaskOptions
    ///   - Interval 校验
    ///   - ToConfigDictionary 包含 Keywords/Templates
    /// </summary>
    public class TestProductCommentOptions
    {
        public TestProductCommentOptions()
        {
            Program.CreateHost(null);
        }

        // ---- Fix 1: 构造函数默认值不依赖 appsettings.json ----

        [Fact]
        public void ConstructorDefaults_ShouldBeSafe_WithoutAppSettings()
        {
            // 直接 new，不通过 DI/配置绑定——确保即使缺失 config section 也安全
            var opts = new ProductCommentTaskOptions();

            Assert.False(
                opts.IsEnable,
                "IsEnable must default to false (safe) regardless of appsettings.json"
            );
            Assert.True(opts.DryRun, "DryRun must default to true (safe)");
            Assert.False(opts.EnableAutoPublish, "EnableAutoPublish must default to false (safe)");
            Assert.Equal("DryRun", opts.PublishMode);
        }

        // ---- 通过 DI 绑定验证 appsettings.json 中的默认值 ----

        [Fact]
        public void ProductCommentOptions_ShouldBind()
        {
            using var scope = Global.ServiceProviderRoot.CreateScope();

            var options = scope.ServiceProvider.GetRequiredService<
                IOptionsMonitor<ProductCommentTaskOptions>
            >();
            var opts = options.CurrentValue;

            // 验证配置节名称
            Assert.Equal("ProductCommentTaskConfig", opts.SectionName);

            // 验证安全默认值——来自 appsettings.json
            Assert.False(opts.IsEnable, "IsEnable should default to false (safe)");
            Assert.True(opts.DryRun, "DryRun should default to true (safe)");
            Assert.False(
                opts.EnableAutoPublish,
                "EnableAutoPublish should default to false (safe)"
            );
            Assert.Equal("DryRun", opts.PublishMode);

            Debug.WriteLine($"IsEnable={opts.IsEnable}");
            Debug.WriteLine($"DryRun={opts.DryRun}");
            Debug.WriteLine($"EnableAutoPublish={opts.EnableAutoPublish}");
            Debug.WriteLine($"PublishMode={opts.PublishMode}");
        }

        [Fact]
        public void ProductCommentOptions_ShouldOnlyHaveOneConfigureRegistration()
        {
            var configuration = new ConfigurationBuilder().Build();
            var services = new ServiceCollection();

            services.AddBiliBiliConfigs(configuration);

            var configureCount = services.Count(x =>
                x.ServiceType == typeof(IConfigureOptions<ProductCommentTaskOptions>)
            );

            Assert.Equal(1, configureCount);
        }

        [Fact]
        public void ProductCommentTaskAppService_ShouldResolveFromContainer()
        {
            using var scope = Global.ServiceProviderRoot.CreateScope();

            var appService =
                scope.ServiceProvider.GetRequiredService<IProductCommentTaskAppService>();

            Assert.NotNull(appService);
        }

        [Fact]
        public void ProductCommentOptions_HasExpectedDefaults()
        {
            using var scope = Global.ServiceProviderRoot.CreateScope();

            var options = scope.ServiceProvider.GetRequiredService<
                IOptionsMonitor<ProductCommentTaskOptions>
            >();
            var opts = options.CurrentValue;

            // 搜索配置默认值
            Assert.Equal("pubdate", opts.SearchOrder);
            Assert.Equal(2, opts.MaxSearchPagesPerKeyword);
            Assert.Equal(5, opts.MaxVideosPerKeyword);
            Assert.Equal(20, opts.MaxCandidatesPerRun);

            // 发布限流默认值
            Assert.Equal(5, opts.MaxPublishPerRun);
            Assert.Equal(10, opts.MaxDailyCommentsPerAccount);
            Assert.Equal(60, opts.CommentIntervalMinSeconds);
            Assert.Equal(180, opts.CommentIntervalMaxSeconds);
            Assert.Equal(30, opts.AccountCooldownMinutes);

            // 持久化默认值
            Assert.Equal("Data/product-comment.json", opts.StoragePath);
            Assert.True(opts.SkipAlreadyProcessedBvid);
            Assert.True(opts.SkipCommentedBvid);

            // Keywords 和 Templates 默认为空
            Assert.NotNull(opts.Keywords);
            Assert.NotNull(opts.Templates);
            Assert.Empty(opts.Keywords);
            Assert.Empty(opts.Templates);
        }

        // ---- Fix 2: Interval 校验 ----

        [Fact]
        public void IntervalValidation_DefaultsShouldBeValid()
        {
            using var scope = Global.ServiceProviderRoot.CreateScope();

            var options = scope.ServiceProvider.GetRequiredService<
                IOptionsMonitor<ProductCommentTaskOptions>
            >();
            var opts = options.CurrentValue;

            // 默认值 60 <= 180 应该通过
            Assert.True(
                opts.CommentIntervalMinSeconds <= opts.CommentIntervalMaxSeconds,
                $"Default: CommentIntervalMinSeconds ({opts.CommentIntervalMinSeconds}) should be <= CommentIntervalMaxSeconds ({opts.CommentIntervalMaxSeconds})"
            );
        }

        [Fact]
        public void IntervalValidation_ShouldDetectMinGreaterThanMax()
        {
            using var provider = CreateProviderWithProductCommentConfig(
                new KeyValuePair<string, string>(
                    "ProductCommentTaskConfig:CommentIntervalMinSeconds",
                    "300"
                ),
                new KeyValuePair<string, string>(
                    "ProductCommentTaskConfig:CommentIntervalMaxSeconds",
                    "100"
                )
            );

            var options = provider.GetRequiredService<IOptions<ProductCommentTaskOptions>>();

            var ex = Assert.Throws<OptionsValidationException>(() => options.Value);
            Assert.Contains(ex.Failures, x => x.Contains("CommentIntervalMaxSeconds"));
        }

        [Fact]
        public void IntervalValidation_ShouldRejectNegativeValues()
        {
            using var provider = CreateProviderWithProductCommentConfig(
                new KeyValuePair<string, string>(
                    "ProductCommentTaskConfig:CommentIntervalMinSeconds",
                    "-1"
                ),
                new KeyValuePair<string, string>(
                    "ProductCommentTaskConfig:CommentIntervalMaxSeconds",
                    "100"
                )
            );

            var options = provider.GetRequiredService<IOptions<ProductCommentTaskOptions>>();

            var ex = Assert.Throws<OptionsValidationException>(() => options.Value);
            Assert.Contains(ex.Failures, x => x.Contains("CommentIntervalMinSeconds"));
        }

        // ---- Fix 3: ToConfigDictionary 包含 Keywords/Templates ----

        [Fact]
        public void ToConfigDictionary_ShouldIncludeKeywordsAndTemplates()
        {
            var opts = new ProductCommentTaskOptions
            {
                Keywords = ["原子豆ANC", "蓝牙耳机"],
                Templates = ["{产品名}真的很不错", "看了视频对{产品名}心动了"],
            };

            var dict = opts.ToConfigDictionary();

            // Keywords 应该序列化为 JSON 数组
            Assert.True(
                dict.ContainsKey("ProductCommentTaskConfig:Keywords"),
                "ToConfigDictionary must contain Keywords key"
            );
            string keywordsJson = dict["ProductCommentTaskConfig:Keywords"];
            Assert.Contains("原子豆ANC", keywordsJson);
            Assert.Contains("蓝牙耳机", keywordsJson);

            // Templates 应该序列化为 JSON 数组
            Assert.True(
                dict.ContainsKey("ProductCommentTaskConfig:Templates"),
                "ToConfigDictionary must contain Templates key"
            );
            string templatesJson = dict["ProductCommentTaskConfig:Templates"];
            Assert.Contains("{产品名}", templatesJson);

            // 验证可以反序列化回去
            var deserializedKeywords = JsonSerializer.Deserialize<List<string>>(keywordsJson);
            Assert.NotNull(deserializedKeywords);
            Assert.Equal(2, deserializedKeywords!.Count);
        }

        [Fact]
        public void ToConfigDictionary_EmptyKeywordsAndTemplates_ShouldStillBePresent()
        {
            var opts = new ProductCommentTaskOptions(); // 空 Keywords 和 Templates

            var dict = opts.ToConfigDictionary();

            Assert.True(dict.ContainsKey("ProductCommentTaskConfig:Keywords"));
            Assert.True(dict.ContainsKey("ProductCommentTaskConfig:Templates"));
            Assert.Equal("[]", dict["ProductCommentTaskConfig:Keywords"]);
            Assert.Equal("[]", dict["ProductCommentTaskConfig:Templates"]);
        }

        private static ServiceProvider CreateProviderWithProductCommentConfig(
            params KeyValuePair<string, string>[] values
        )
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var services = new ServiceCollection();

            services.AddBiliBiliConfigs(configuration);

            return services.BuildServiceProvider();
        }
    }
}
