using CCaaS.Application.Ai;
using CCaaS.Application.Appointment;
using CCaaS.Application.Billing;
using CCaaS.Application.Calls;
using CCaaS.Application.Campaign;
using CCaaS.Application.Channel;
using CCaaS.Application.Common;
using CCaaS.Application.Conversation;
using CCaaS.Application.Crm;
using CCaaS.Application.Identity;
using CCaaS.Application.Organization;
using CCaaS.Application.Reporting;
using CCaaS.Application.Tenant;
using CCaaS.Infrastructure.Caching;
using CCaaS.Infrastructure.Ai;
using CCaaS.Infrastructure.Appointment;
using CCaaS.Infrastructure.Channels;
using CCaaS.Infrastructure.Identity;
using CCaaS.Infrastructure.Messaging;
using CCaaS.Infrastructure.ObjectStorage;
using CCaaS.Infrastructure.Observability;
using CCaaS.Infrastructure.Persistence;
using CCaaS.Infrastructure.Tenancy;
using CCaaS.Shared.Tenancy;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace CCaaS.Infrastructure;

/// <summary>Single composition-root extension called once from Api/Program.cs.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddCcaasInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // --- Persistence -----------------------------------------------------------------
        services.AddDbContext<CcaasDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("Default"),
                sql =>
                {
                    sql.MigrationsAssembly(typeof(CcaasDbContext).Assembly.FullName);
                    sql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null);
                }));

        services.AddScoped(typeof(IRepository<>), typeof(GenericRepository<>));
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // --- Tenancy / current user -------------------------------------------------------
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentTenant, HttpCurrentTenant>();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();

        // --- Identity -----------------------------------------------------------------
        services.AddScoped<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddScoped<IJwtTokenGenerator, JwtTokenGenerator>();
        services.AddScoped<IAuthService, AuthService>();

        // --- Application services (one line per module - Section 4 Functional Scope) ------
        services.AddScoped<ITenantService, TenantService>();
        services.AddScoped<IOrganizationService, OrganizationService>();
        services.AddScoped<ICrmService, CrmService>();
        services.AddScoped<ICampaignService, CampaignService>();
        services.AddScoped<IConversationService, ConversationService>();
        services.AddScoped<ICallService, CallService>();
        services.AddScoped<IBillingService, BillingService>();
        services.AddScoped<IReportingService, ReportingService>();
        services.AddScoped<IAiAgentService, AiAgentService>();
        services.AddScoped<IAppointmentService, AppointmentService>();

        // --- Messaging (RabbitMQ + transactional outbox) -----------------------------------
        services.AddSingleton<IEventBus, RabbitMqEventBus>();
        services.AddScoped<ITelephonyDispatcher, RabbitMqTelephonyDispatcher>();
        services.AddHostedService<OutboxPublisherService>();

        // --- Caching (Redis) ---------------------------------------------------------------
        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(configuration["Redis:ConnectionString"] ?? "localhost:6379"));
        services.AddSingleton<IPresenceCache, RedisPresenceCache>();

        // --- Object storage (MinIO / S3-compatible) -----------------------------------------
        services.AddSingleton<IObjectStorageService, MinioObjectStorageService>();

        // --- Channel providers (Section 9 - provider adapter pattern) ----------------------
        services.AddHttpClient("whatsapp");
        services.AddScoped<IChannelProvider, WhatsAppChannelProvider>();
        services.AddScoped<IChannelProviderFactory, ChannelProviderFactory>();
        // TODO: register MessengerChannelProvider / InstagramChannelProvider / EmailChannelProvider /
        // SmsChannelProvider / WebChatChannelProvider the same way once you build each adapter
        // (Section 9 - Provider adapter pattern lists all six).

        // --- AI abstractions (Section 16 - disabled/no-op by default until you wire a provider) ---
        services.AddHttpClient("ollama");
        services.AddHttpClient("local-ai-speech");
        services.AddScoped<IHandoffContextSummarizer, OllamaHandoffContextSummarizer>();
        services.AddScoped<IProviderUsageWriter, ProviderUsageWriter>();
        services.AddScoped<DevelopmentAiProvider>();
        services.AddScoped<ISpeechToTextProvider>(sp => new MeteredSpeechToTextProvider(
            sp.GetRequiredService<DevelopmentAiProvider>(), sp.GetRequiredService<IProviderUsageWriter>(),
            sp.GetRequiredService<ICurrentTenant>()));
        services.AddScoped<ISummaryProvider>(sp => sp.GetRequiredService<DevelopmentAiProvider>());
        services.AddScoped<IQaScoringProvider>(sp => sp.GetRequiredService<DevelopmentAiProvider>());
        services.AddScoped<ISentimentProvider>(sp => sp.GetRequiredService<DevelopmentAiProvider>());
        services.AddScoped<ITranscriptRedactor, PiiTranscriptRedactor>();
        services.AddScoped<IAiToolHandler, CreateSupportTicketTool>();
        services.AddScoped<IAiToolHandler, CheckAppointmentAvailabilityTool>();
        services.AddScoped<IAiToolHandler, BookAppointmentTool>();
        services.AddScoped<IAiToolHandler, RequestHumanHandoffTool>();

        // --- Background jobs (Hangfire) ------------------------------------------------------
        services.AddHangfire(config => config
            .UseSqlServerStorage(configuration.GetConnectionString("Default"), new SqlServerStorageOptions
            {
                SchemaName = "hangfire"
            }));
        services.AddHangfireServer();

        // --- Observability (Section 17) ------------------------------------------------------
        services.AddCcaasObservability(configuration);

        return services;
    }
}
