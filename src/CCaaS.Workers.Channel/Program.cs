using CCaaS.Infrastructure;
using CCaaS.Workers.Channel;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddCcaasInfrastructure(builder.Configuration);
builder.Services.AddHostedService<InboundChannelMessageConsumer>();

var host = builder.Build();
host.Run();
