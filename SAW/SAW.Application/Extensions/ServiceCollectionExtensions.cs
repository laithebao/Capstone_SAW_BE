using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using SAW.Application.Abstractions.Authentication;
using SAW.Application.Features.Auth.Login;
using System.Reflection;

namespace SAW.Application.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(
        this IServiceCollection services)
    {
        var assembly = Assembly.GetExecutingAssembly();

        // FluentValidation – tự động đăng ký tất cả validators trong assembly
        services.AddValidatorsFromAssembly(assembly);
        services.AddScoped<IAuthService, AuthService>();

        return services;
    }
}
