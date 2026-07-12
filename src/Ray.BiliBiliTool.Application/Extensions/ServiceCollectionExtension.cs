using Microsoft.Extensions.DependencyInjection;
using Ray.BiliBiliTool.Application.Contracts;
using Ray.BiliBiliTool.Application.Contracts.ContentAutomation;
using Ray.BiliBiliTool.Application.Contracts.MaintenanceWorkflows;
using Ray.BiliBiliTool.Application.Contracts.Runtime;

namespace Ray.BiliBiliTool.Application.Extensions;

public static class ServiceCollectionExtension
{
    public static IServiceCollection AddAppServices(this IServiceCollection services)
    {
        services.Scan(scan =>
            scan.FromAssemblyOf<DailyTaskAppService>()
                .AddClasses(classes => classes.AssignableTo<IAppService>())
                .AsImplementedInterfaces()
                .WithTransientLifetime()
        );
        services.AddHttpClient<IContentAutomationBridge, ContentAutomationBridge>();
        services.AddSingleton<
            IContentAutomationStrategyService,
            ContentAutomationStrategyService
        >();
        services.AddSingleton<ISystemRuntimeStateStore, SystemRuntimeStateStore>();
        services.AddSingleton<IMaintenanceWorkflowService, MaintenanceWorkflowService>();

        return services;
    }
}
