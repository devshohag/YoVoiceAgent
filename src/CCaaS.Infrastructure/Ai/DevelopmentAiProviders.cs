using System.Text.RegularExpressions;
using CCaaS.Application.Ai;
using CCaaS.Application.Appointment;

namespace CCaaS.Infrastructure.Ai;

/// <summary>
/// Free local providers for learning and UI development. They make the complete pipeline
/// runnable without an API key; replace them through DI when connecting a real provider.
/// </summary>
internal sealed class DevelopmentAiProvider :
    ISpeechToTextProvider, ISummaryProvider, IQaScoringProvider, ISentimentProvider
{
    public Task<TranscriptResult> TranscribeAsync(string recordingObjectStorageKey, CancellationToken ct = default)
    {
        var text = recordingObjectStorageKey.StartsWith("text:", StringComparison.OrdinalIgnoreCase)
            ? recordingObjectStorageKey[5..]
            : $"Development transcript for recording {recordingObjectStorageKey}. The customer requested an appointment and a follow-up call.";
        return Task.FromResult(new TranscriptResult(text, 1.0));
    }

    public Task<SummaryResult> SummarizeAsync(string transcriptText, CancellationToken ct = default)
    {
        var preview = transcriptText.Length <= 180 ? transcriptText : transcriptText[..180] + "...";
        return Task.FromResult(new SummaryResult(
            $"Development summary: {preview}",
            "follow-up-required",
            "Confirm the customer's requested next step."));
    }

    public Task<QaScoreResult> ScoreAsync(string transcriptText, string qaFormDefinitionJson, CancellationToken ct = default)
        => Task.FromResult(new QaScoreResult(85, new Dictionary<string, string>
        {
            ["greeting"] = "pass",
            ["verification"] = "review",
            ["resolution"] = "pass"
        }));

    public Task<SentimentResult> AnalyzeAsync(string text, CancellationToken ct = default)
    {
        var negative = text.Contains("angry", StringComparison.OrdinalIgnoreCase) ||
                       text.Contains("cancel", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(new SentimentResult(negative ? "negative" : "neutral", negative ? 0.82 : 0.65));
    }
}

public sealed partial class PiiTranscriptRedactor : ITranscriptRedactor
{
    [GeneratedRegex(@"\b(?:\d[ -]*?){13,19}\b")]
    private static partial Regex CardNumberRegex();

    [GeneratedRegex(@"\b[\w.%+-]+@[\w.-]+\.[A-Za-z]{2,}\b")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<!\d)(?:\+?\d[\d ()-]{7,}\d)(?!\d)")]
    private static partial Regex PhoneRegex();

    public string Redact(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return rawText;
        var redacted = CardNumberRegex().Replace(rawText, "[CARD-REDACTED]");
        redacted = EmailRegex().Replace(redacted, "[EMAIL-REDACTED]");
        return PhoneRegex().Replace(redacted, "[PHONE-REDACTED]");
    }
}

internal sealed class CreateSupportTicketTool : IAiToolHandler
{
    public string Name => "CreateSupportTicket";

    public Task<string> ExecuteAsync(Guid tenantId, string argumentsJson, CancellationToken ct = default)
    {
        using var document = System.Text.Json.JsonDocument.Parse(argumentsJson);
        if (!document.RootElement.TryGetProperty("subject", out var subject) || string.IsNullOrWhiteSpace(subject.GetString()))
            throw new ArgumentException("The subject field is required.");

        return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new
        {
            ticketId = Guid.NewGuid(),
            tenantId,
            subject = subject.GetString(),
            status = "created-development-demo"
        }));
    }
}

internal sealed class CheckAppointmentAvailabilityTool : IAiToolHandler
{
    private readonly IAppointmentService _appointments;
    public CheckAppointmentAvailabilityTool(IAppointmentService appointments) => _appointments = appointments;
    public string Name => "check_appointment_availability";

    public async Task<string> ExecuteAsync(Guid tenantId, string argumentsJson, CancellationToken ct = default)
    {
        var input = AppointmentToolInput.Parse(argumentsJson);
        var date = input.RequiredDate();
        if (date < DateOnly.FromDateTime(DateTime.UtcNow.Date))
            throw new ArgumentException("Appointment date cannot be in the past.");
        var available = await _appointments.GetAvailabilityAsync(tenantId, date, ct);
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            available = available.Count > 0,
            date = date.ToString("yyyy-MM-dd"),
            slots = available.Select(x => new
            {
                slotId = x.SlotId,
                providerId = x.ProviderId,
                providerName = x.ProviderName,
                startsAtUtc = x.StartsAtUtc,
                endsAtUtc = x.EndsAtUtc,
                displayTime = x.StartsAtUtc.ToString("HH:mm 'UTC'")
            }),
            source = "sql-server"
        });
    }
}

internal sealed class BookAppointmentTool : IAiToolHandler
{
    private readonly IAppointmentService _appointments;
    public BookAppointmentTool(IAppointmentService appointments) => _appointments = appointments;
    public string Name => "book_appointment";

    public async Task<string> ExecuteAsync(Guid tenantId, string argumentsJson, CancellationToken ct = default)
    {
        var input = AppointmentToolInput.Parse(argumentsJson);
        if (input.SlotId is null || input.SlotId == Guid.Empty) throw new ArgumentException("Availability slot id is required.");
        if (string.IsNullOrWhiteSpace(input.CustomerName)) throw new ArgumentException("Customer name is required.");
        if (string.IsNullOrWhiteSpace(input.Contact)) throw new ArgumentException("Customer contact is required.");
        var booking = await _appointments.BookAsync(tenantId, new BookAppointmentCommand(
            input.SlotId.Value, input.CustomerName, input.Contact, input.Purpose), ct);
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            success = true,
            bookingId = booking.BookingId,
            bookingReference = booking.BookingReference,
            startsAtUtc = booking.StartsAtUtc,
            status = booking.Status,
            wasExisting = booking.WasExisting,
            voucherUrl = $"/api/appointments/{booking.BookingId}/voucher",
            source = "sql-server"
        });
    }
}

internal sealed class RequestHumanHandoffTool : IAiToolHandler
{
    public string Name => "request_human_handoff";

    public Task<string> ExecuteAsync(Guid tenantId, string argumentsJson, CancellationToken ct = default)
    {
        using var document = System.Text.Json.JsonDocument.Parse(argumentsJson);
        var reason = document.RootElement.TryGetProperty("reason", out var value) ? value.GetString() : null;
        return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new
        {
            success = true,
            status = "queued-development",
            queue = "general-support",
            reason = string.IsNullOrWhiteSpace(reason) ? "AI requested assistance." : reason
        }));
    }
}

internal sealed record AppointmentToolInput(Guid? SlotId, string? Date, string? Time, string? CustomerName, string? Contact, string? Purpose)
{
    public static AppointmentToolInput Parse(string json)
        => System.Text.Json.JsonSerializer.Deserialize<AppointmentToolInput>(json,
               new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
           ?? throw new ArgumentException("Appointment tool arguments are required.");

    public DateOnly RequiredDate()
        => DateOnly.TryParse(Date, out var date) ? date : throw new ArgumentException("A valid appointment date is required.");
}
