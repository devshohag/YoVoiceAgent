using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace CCaaS.Workers.Telephony;

/// <summary>
/// Consumes the "CallOriginateRequested" command published by
/// CCaaS.Infrastructure.Messaging.RabbitMqTelephonyDispatcher (Section 8 outbound flow step
/// 3-4) and turns it into a real ARI originate call.
/// </summary>
public class CallOriginateConsumer : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly AriClient _ariClient;
    private readonly ILogger<CallOriginateConsumer> _logger;
    private const string ExchangeName = "ccaas.domain-events";
    private const string QueueName = "telephony.call-originate-requested";

    public CallOriginateConsumer(IConfiguration configuration, AriClient ariClient, ILogger<CallOriginateConsumer> logger)
    {
        _configuration = configuration;
        _ariClient = ariClient;
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

        var connection = factory.CreateConnection("ccaas-telephony-worker");
        var channel = connection.CreateModel();
        channel.ExchangeDeclare(ExchangeName, ExchangeType.Topic, durable: true);
        channel.QueueDeclare(QueueName, durable: true, exclusive: false, autoDelete: false);
        channel.QueueBind(QueueName, ExchangeName, routingKey: "CallOriginateRequested");
        channel.BasicQos(0, prefetchCount: 10, global: false);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.Received += async (_, ea) =>
        {
            try
            {
                var json = Encoding.UTF8.GetString(ea.Body.ToArray());
                using var doc = JsonDocument.Parse(json);
                var callSessionId = doc.RootElement.GetProperty("callSessionId").GetGuid();
                var fromNumber = doc.RootElement.GetProperty("fromNumber").GetString()!;
                var toNumber = doc.RootElement.GetProperty("toNumber").GetString()!;

                // TODO: resolve the tenant's actual SipTrunk name instead of this "default"
                // placeholder - see Domain.Telephony.SipTrunk.
                await _ariClient.OriginateAsync(callSessionId, fromNumber, toNumber, trunkName: "default", stoppingToken);
                channel.BasicAck(ea.DeliveryTag, multiple: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to originate call from queued request - nacking for retry.");
                channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: true);
            }
        };

        channel.BasicConsume(QueueName, autoAck: false, consumer);
        _logger.LogInformation("Listening for CallOriginateRequested on {Queue}", QueueName);

        stoppingToken.Register(() =>
        {
            channel.Close();
            connection.Close();
        });

        return Task.CompletedTask;
    }
}
