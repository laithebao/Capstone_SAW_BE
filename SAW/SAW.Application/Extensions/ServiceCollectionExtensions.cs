using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using SAW.Application.Features.Authentication.Commands;
using SAW.Application.Features.Suppliers.Commands;

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

        services.AddScoped<ISupplierCommandService, SupplierCommandService>();
        services.AddScoped<ISupplierBatchCommandService, SupplierBatchCommandService>();

        return services;
    }
}
