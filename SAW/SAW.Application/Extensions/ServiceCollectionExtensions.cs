using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using SAW.Application.Features.Authentication.Commands;
using SAW.Application.Features.CropTypes;
using SAW.Application.Features.UserAccess;
using SAW.Application.Features.AdminDashboard;
using SAW.Application.Features.InspectionStandards;
using SAW.Application.Features.AuditLogs;
using SAW.Application.Features.Suppliers.Commands;
using SAW.Application.Features.ProductBatches.Interfaces;
using SAW.Application.Features.ProductBatches.Services;

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
        services.AddScoped<IAuditLogService, AuditLogService>();

        services.AddScoped<ISupplierCommandService, SupplierCommandService>();
        services.AddScoped<ISupplierBatchCommandService, SupplierBatchCommandService>();
        services.AddScoped<IProductBatchService, ProductBatchService>();

        return services;
    }
}
