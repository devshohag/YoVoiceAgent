using CCaaS.Infrastructure;
using CCaaS.Workers.Telephony;

var builder = Host.CreateApplicationBuilder(args);

// Reuses the same DbContext/repositories/services/RabbitMQ/Redis/Hangfire wiring as the API
// (Section 6 core architecture principle: "modular monolith for the business application,
// with event-driven independent workers for telephony..." - the worker is a separate PROCESS
// but shares the same application/domain code, it does not re-implement business rules).
builder.Services.AddCcaasInfrastructure(builder.Configuration);

builder.Services.AddHttpClient("ari");
builder.Services.AddHttpClient("local-ai");
builder.Services.AddSingleton<AriClient>();
builder.Services.AddScoped<IInboundTenantResolver, InboundTenantResolver>();
builder.Services.AddSingleton<AriEventListener>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AriEventListener>());
builder.Services.AddHostedService<VoiceResponsePoller>();
builder.Services.AddHostedService<CallOriginateConsumer>();
builder.Services.AddHostedService<CallLifecycleReconciler>();
builder.Services.AddHostedService<LocalAiVoiceProcessor>();

var host = builder.Build();
host.Run();
