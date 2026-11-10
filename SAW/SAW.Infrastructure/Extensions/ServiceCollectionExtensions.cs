using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SAW.Infrastructure.AiChat.Rag;
using SAW.Infrastructure.AiChat.Rag.Loaders;
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
using SAW.Application.Features.QrCodes;
using SAW.Application.Features.QrCodes.Interfaces;
using SAW.Application.Features.WarehouseInventory;
using SAW.Infrastructure.QrCodes;
using SAW.Application.Features.SupplyContracts;
using SAW.Infrastructure.Notifications;
using SAW.Infrastructure.AiChat;
using SAW.Application.Features.AiChat;
using SAW.Application.Features.SpoilageForecast;
using SAW.Infrastructure.AiForecast;

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
        services.AddScoped<IGoodsReceiptQueryRepository, GoodsReceiptQueryRepository>();
        services.AddScoped<IGoodsReceiptRepository, GoodsReceiptRepository>();
        services.AddScoped<SAW.Application.Features.DistributorOrders.IDistributorOrderRepository, DistributorOrderRepository>();
        services.AddScoped<IWarehouseInventoryRepository, WarehouseInventoryRepository>();
        services.AddScoped<SAW.Application.Features.Picking.IPickingRepository, PickingRepository>();
        services.AddScoped<SAW.Application.Features.GoodsIssues.IGoodsIssueRepository, GoodsIssueRepository>();
        services.AddScoped<SAW.Application.Features.StockAdjustments.IStockAdjustmentRepository, StockAdjustmentRepository>();
        services.AddScoped<SAW.Application.Features.StockAdjustments.IStockAdjustmentResolutionRepository>(sp => (StockAdjustmentRepository)sp.GetRequiredService<SAW.Application.Features.StockAdjustments.IStockAdjustmentRepository>());
        services.AddScoped<SAW.Application.Features.Picking.IOrderReservationAllocator, OrderReservationAllocator>();
        services.AddScoped<IProductBatchVerificationRepository, ProductBatchVerificationRepository>();
        services.AddScoped<IQcInspectionRepository, QcInspectionRepository>();
        services.AddScoped<IQrCodeRepository, QrCodeRepository>();
        services.AddScoped<ITraceabilityRepository, TraceabilityRepository>();
        services.AddScoped<ISupplyContractRepository, SupplyContractRepository>();
        services.AddSingleton(configuration.GetSection("QrCode").Get<QrCodeOptions>() ?? new QrCodeOptions());
        services.AddSingleton(configuration.GetSection("Cloudinary").Get<CloudinaryOptions>() ?? new CloudinaryOptions());
        services.AddSingleton<IQrCodeRenderer, PngQrCodeRenderer>();
        services.AddHttpClient<IQrImageStorage, CloudinaryQrImageStorage>(client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddHostedService<QrCodeGenerationWorker>();
        services.AddHostedService<SupplyContractDeliveryReminderWorker>();

        // AI Chat (OpenAI)
        var aiChatOptions = configuration.GetSection("AiChat").Get<AiChatOptions>() ?? new AiChatOptions();
        var aiExecution = configuration.GetSection("AiExecution").Get<AiExecutionOptions>() ?? new AiExecutionOptions();
        var openAIOptions = configuration.GetSection("OpenAI").Get<OpenAIOptions>() ?? new OpenAIOptions();
        var qdrantOptions = configuration.GetSection("Qdrant").Get<QdrantOptions>() ?? new QdrantOptions();
        services.AddSingleton(aiChatOptions);
        services.AddSingleton(openAIOptions);
        services.AddSingleton(aiExecution);

        services.AddHttpClient<OpenAIChatClient>(client =>
        {
            client.BaseAddress = OpenAIHttp.BaseAddress;
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(openAIOptions.ChatRequestTimeoutSeconds, 1, 120));
        }).RedactLoggedHeaders(new[] { "Authorization" });

        services.AddHttpClient<MafAiChatClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(openAIOptions.ChatRequestTimeoutSeconds, 1, 120));
        }).RedactLoggedHeaders(new[] { "Authorization" });
        services.AddScoped<IAiChatModelClient>(sp => aiExecution.UseMafForChat
            ? sp.GetRequiredService<MafAiChatClient>()
            : sp.GetRequiredService<OpenAIChatClient>());

        services.AddHttpClient(OpenAIEmbeddingService.HttpClientName, client =>
        {
            client.BaseAddress = OpenAIHttp.BaseAddress;
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(openAIOptions.EmbeddingRequestTimeoutSeconds, 1, 120));
        }).RedactLoggedHeaders(new[] { "Authorization" });

        services.AddSingleton(qdrantOptions);
        services.AddHttpClient(QdrantKnowledgeStore.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            if (qdrantOptions.IsConfigured)
            {
                client.BaseAddress = qdrantOptions.EndpointUri;
                client.DefaultRequestHeaders.Add("api-key", qdrantOptions.ApiKey.Trim());
            }
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
        .RedactLoggedHeaders(new[] { "api-key" });

        // Document loaders — mỗi loader hỗ trợ một định dạng file
        services.AddSingleton<IDocumentLoader, MarkdownTextLoader>();
        services.AddSingleton<IDocumentLoader, PlainTextLoader>();
        services.AddSingleton<IDocumentLoader, PdfDocumentLoader>();
        services.AddSingleton<IDocumentLoader, WordDocumentLoader>();

        // RAG core services
        services.AddSingleton<OpenAIEmbeddingService>();
        services.AddSingleton<QdrantKnowledgeStore>();
        services.AddSingleton<KnowledgeBaseService>();

        // Seed or refresh the collection during startup.
        services.AddHostedService<KnowledgeBaseIndexingService>();

        services.AddScoped<AiToolExecutor>();
        services.AddScoped<IAiChatService, AiChatService>();

        // AI Spoilage Forecast (OpenAI)
        services.AddHttpClient<OpenAIForecastClient>(client =>
        {
            client.BaseAddress = OpenAIHttp.BaseAddress;
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).RedactLoggedHeaders(new[] { "Authorization" });
        services.AddHttpClient<MafForecastClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .RedactLoggedHeaders(new[] { "Authorization" });
        services.AddScoped<IForecastModelClient>(sp => aiExecution.UseMafForForecast
            ? sp.GetRequiredService<MafForecastClient>()
            : sp.GetRequiredService<OpenAIForecastClient>());
        services.AddScoped<ForecastInputCollector>();
        services.AddScoped<ISpoilageForecastService, SpoilageForecastService>();

        return services;
    }
}
