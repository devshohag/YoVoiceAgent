using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace CCaaS.Infrastructure.Messaging;

// Section 13 - RabbitMQ event examples: CallStarted, CallAnswered, CallEnded, RecordingReady,
// MessageReceived, MessageSent, MessageDelivered, MessageRead, ConversationAssigned,
// DispositionSubmitted, FollowUpScheduled, CampaignStarted, InvoiceGenerated,
// TranscriptCompleted, AiSummaryCompleted.
public interface IEventBus
{
    Task PublishAsync(string eventType, object payload, CancellationToken ct = default);
}

/// <summary>
/// Thin RabbitMQ publisher. In production this should be called ONLY by the Outbox
/// publisher background service (see OutboxPublisherService) - never directly from a
/// request handler - so a crash between the SQL commit and the publish can never lose
/// or duplicate an event (Section 13 - Transactional Outbox).
/// </summary>
public class RabbitMqEventBus : IEventBus, IDisposable
{
    private readonly IConnection _connection;
    private readonly IModel _channel;
    private readonly ILogger<RabbitMqEventBus> _logger;
    private const string ExchangeName = "ccaas.domain-events";

    public RabbitMqEventBus(IConfiguration configuration, ILogger<RabbitMqEventBus> logger)
    {
        _logger = logger;
        var factory = new ConnectionFactory
        {
            HostName = configuration["RabbitMq:Host"] ?? "localhost",
            Port = int.Parse(configuration["RabbitMq:Port"] ?? "5672"),
            UserName = configuration["RabbitMq:Username"] ?? "guest",
            Password = configuration["RabbitMq:Password"] ?? "guest",
            DispatchConsumersAsync = true
        };
        _connection = factory.CreateConnection("ccaas-api");
        _channel = _connection.CreateModel();
        _channel.ExchangeDeclare(ExchangeName, ExchangeType.Topic, durable: true);
    }

    public Task PublishAsync(string eventType, object payload, CancellationToken ct = default)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        var properties = _channel.CreateBasicProperties();
        properties.Persistent = true;
        properties.Type = eventType;
        properties.MessageId = Guid.NewGuid().ToString();

        _channel.BasicPublish(ExchangeName, routingKey: eventType, basicProperties: properties, body: body);
        _logger.LogInformation("Published event {EventType} ({MessageId})", eventType, properties.MessageId);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _channel?.Close();
        _connection?.Close();
    }
}
