using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SAW.Infrastructure.Persistence;
using SAW.Application.Features.Authentication.Commands;
using SAW.Application.Repositories;
using SAW.Infrastructure.Authentication;
using SAW.Infrastructure.Repositories;
using SAW.Application.Features.CropTypes;
using SAW.Application.Features.UserAccess;
using SAW.Application.Features.AdminDashboard;
using SAW.Application.Features.InspectionStandards;
using SAW.Application.Features.AuditLogs;
using SAW.Application.Repositories.Suppliers;
using SAW.Infrastructure.Repositories.Suppliers;

namespace SAW.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("DefaultConnection"),
                sql =>
                {
                    sql.MigrationsHistoryTable("__EFMigrationsHistory");
                    sql.CommandTimeout(60);
                    sql.EnableRetryOnFailure(
                        maxRetryCount: 3,
                        maxRetryDelay: TimeSpan.FromSeconds(10),
                        errorNumbersToAdd: null);
                }
            ));

        services.AddScoped<IAuthRepository, AuthRepository>();
        services.AddScoped<ICropTypeRepository, CropTypeRepository>();
        services.AddScoped<IUserAccessRepository, UserAccessRepository>();
        services.AddScoped<IAdminDashboardRepository, AdminDashboardRepository>();
        services.AddScoped<IInspectionStandardRepository, InspectionStandardRepository>();
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        var emailDeliveryMode = configuration["Email:DeliveryMode"];
        if (string.Equals(emailDeliveryMode, "Log", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IEmailSender, LogEmailSender>();
        else
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddSingleton<IGoogleIdentityService, GoogleIdentityService>();

        services.AddScoped<ISupplierRepository, SupplierRepository>();
        services.AddScoped<IProductBatchRepository, ProductBatchRepository>();
        services.AddScoped<IProductBatchQueryRepository, ProductBatchQueryRepository>();
        services.AddScoped<IProductBatchVerificationRepository, ProductBatchVerificationRepository>();

        return services;
    }
}
