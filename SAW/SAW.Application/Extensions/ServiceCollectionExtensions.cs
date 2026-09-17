using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using SAW.Application.Features.Authentication.Commands;
using SAW.Application.Features.CropTypes;
using SAW.Application.Features.UserAccess;
using SAW.Application.Features.AdminDashboard;
using SAW.Application.Features.InspectionStandards;

namespace SAW.Application.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(
        this IServiceCollection services)
    {
        var assembly = Assembly.GetExecutingAssembly();

        // FluentValidation – tự động đăng ký tất cả validators trong assembly
        services.AddValidatorsFromAssembly(assembly);
        services.AddScoped<IAuthCommandService, AuthCommandService>();
        services.AddScoped<ICropTypeService, CropTypeService>();
        services.AddScoped<IUserAccessService, UserAccessService>();
        services.AddScoped<IAdminDashboardService, AdminDashboardService>();
        services.AddScoped<IInspectionStandardService, InspectionStandardService>();

        return services;
    }
}
