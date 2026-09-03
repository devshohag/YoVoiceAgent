using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace CCaaS.Workers.Channel;

/// <summary>
/// Section 9 - Inbound message flow (the part after signature verification, which the Api
/// project's ChannelWebhookController already did): "Idempotency Store -> Normalize Provider
/// Payload -> MessageReceived Event -> Conversation Match/Create -> Customer Match ->
/// Routing/Assignment -> Persist -> SignalR -> Unified Agent Inbox."
/// </summary>
public class InboundChannelMessageConsumer : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InboundChannelMessageConsumer> _logger;
    private const string ExchangeName = "ccaas.domain-events";
    private const string QueueName = "channel.inbound-message-received";

    public InboundChannelMessageConsumer(IConfiguration configuration, IServiceScopeFactory scopeFactory, ILogger<InboundChannelMessageConsumer> logger)
    {
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _configuration["RabbitMq:Host"] ?? "localhost",
            Port = int.Parse(_configuration["RabbitMq:Port"] ?? "5672"),
            UserName = _configuration["RabbitMq:Username"] ?? "guest",
            Password = _configuration["RabbitMq:Password"] ?? "guest",
            DispatchConsumersAsync = true
        };

        var connection = factory.CreateConnection("ccaas-channel-worker");
        var channel = connection.CreateModel();
        channel.ExchangeDeclare(ExchangeName, ExchangeType.Topic, durable: true);
        channel.QueueDeclare(QueueName, durable: true, exclusive: false, autoDelete: false);
        channel.QueueBind(QueueName, ExchangeName, routingKey: "InboundChannelMessageReceived");
        channel.BasicQos(0, prefetchCount: 20, global: false);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.Received += async (_, ea) =>
        {
            try
            {
                var json = Encoding.UTF8.GetString(ea.Body.ToArray());
                using var doc = JsonDocument.Parse(json);
                var channelKey = doc.RootElement.GetProperty("channelKey").GetString();
                var rawBody = doc.RootElement.GetProperty("rawBody").GetString();

                _logger.LogInformation("Processing inbound {ChannelKey} message ({Length} bytes)", channelKey, rawBody?.Length ?? 0);

                // TODO: this is where a real implementation would:
                //   1. Parse rawBody using the provider-specific shape (WhatsApp Cloud API's
                //      "entry[].changes[].value.messages[]" structure, for example).
                //   2. Resolve the tenant + ChannelAccount from the provider's account/phone id.
                //   3. using var scope = _scopeFactory.CreateScope();
                //      var crm = scope.ServiceProvider.GetRequiredService<ICrmService>();
                //      var conversations = scope.ServiceProvider.GetRequiredService<IConversationService>();
                //      ... FindByPhoneAsync/CreateCustomerAsync -> GetOrCreateConversationAsync -> PostMessageAsync
                //   4. Broadcast the new message to the assigned agent via IHubContext<AgentHub>.

                channel.BasicAck(ea.DeliveryTag, multiple: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process inbound channel message - nacking for retry.");
                channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: true);
            }
        };

        channel.BasicConsume(QueueName, autoAck: false, consumer);
        _logger.LogInformation("Listening for InboundChannelMessageReceived on {Queue}", QueueName);

        stoppingToken.Register(() =>
        {
            channel.Close();
            connection.Close();
        });

        return Task.CompletedTask;
    }
}
