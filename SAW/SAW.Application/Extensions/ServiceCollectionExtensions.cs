using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using SAW.Application.Features.Authentication.Commands;

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

        return services;
    }
}
