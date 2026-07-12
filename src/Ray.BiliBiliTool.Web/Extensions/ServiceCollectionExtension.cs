using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using MudBlazor;
using Ray.BiliBiliTool.Web.Auth;
using Ray.BiliBiliTool.Web.Services;

namespace Ray.BiliBiliTool.Web.Extensions;

public static class ServiceCollectionExtension
{
    public static IServiceCollection AddWebServices(this IServiceCollection services)
    {
        services.AddScoped<IAuthService, AuthService>();
        services.AddSingleton<IWebAuditLog, FileWebAuditLog>();
        services.AddScoped<IDataMaintenanceService, DataMaintenanceService>();
        services.AddSingleton<IAsyncDelay, SystemAsyncDelay>();
        services.AddSingleton<IGracefulShutdownCoordinator, GracefulShutdownCoordinator>();
        services.AddSingleton<PortableUiLifecycleCoordinator>();
        services.AddSingleton<CircuitHandler, PortableUiCircuitHandler>();
        services.AddHostedService<ContentExecutorHostedService>();
        services.AddTransient<MudLocalizer, ChineseMudLocalizer>();

        return services;
    }

    public static IServiceCollection AddAuthServices(this IServiceCollection services)
    {
        services.AddAuthenticationCore();
        services.AddAuthorizationCore();
        services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.Cookie.Name = "BiliToolWebAuth";
                options.LoginPath = "/login";
                options.ExpireTimeSpan = TimeSpan.FromDays(30);
            });
        services.AddHttpContextAccessor();
        services.AddScoped<AuthenticationStateProvider, CustomAuthStateProvider>();

        return services;
    }
}
