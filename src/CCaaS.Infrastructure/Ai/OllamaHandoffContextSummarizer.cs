using System.Net.Http.Json;
using System.Text.Json;
using CCaaS.Application.Ai;
using CCaaS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CCaaS.Infrastructure.Ai;

internal sealed class OllamaHandoffContextSummarizer : IHandoffContextSummarizer
{
    private sealed record OllamaMessage(string Role, string Content);
    private sealed record OllamaRequest(string Model, OllamaMessage[] Messages, bool Stream,
        string Format, bool Think, object Options);
    private sealed class OllamaResponse { public OllamaResponseMessage? Message { get; set; } }
    private sealed class OllamaResponseMessage { public string? Content { get; set; } }
    private sealed class StructuredSummary
    {
        public string? Summary { get; set; }
        public string? CustomerIntent { get; set; }
        public string? DetectedLanguage { get; set; }
        public string? Sentiment { get; set; }
        public List<string>? CollectedDetails { get; set; }
        public List<string>? UnresolvedItems { get; set; }
        public string? SuggestedOpening { get; set; }
    }

    private readonly CcaasDbContext _db;
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<OllamaHandoffContextSummarizer> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = true };

    public OllamaHandoffContextSummarizer(CcaasDbContext db, IHttpClientFactory clients,
        ILogger<OllamaHandoffContextSummarizer> logger)
    { _db = db; _clients = clients; _logger = logger; }

    public async Task<HandoffContextResult> SummarizeAsync(Guid tenantId, string transcriptText,
        string handoffReason, CancellationToken ct = default)
    {
        var cleanTranscript = Limit(transcriptText, 16_000);
        var cleanReason = Limit(handoffReason, 500);
        var profile = await _db.AiProviderProfiles.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.IsActive
                        && x.Capability == "Llm" && x.ProviderType == "Ollama")
            .OrderBy(x => x.Priority).FirstOrDefaultAsync(ct);
        if (profile is null)
            return Fallback(cleanTranscript, cleanReason, "deterministic-fallback");

        var endpoint = BuildChatEndpoint(profile.BaseUrl);
        var prompt = """
            You prepare a live AI-to-human call handoff. Read the Bangla/English transcript and
            return JSON only with exactly these keys: summary, customerIntent, detectedLanguage,
            sentiment, collectedDetails, unresolvedItems, suggestedOpening. Preserve confirmed
            names, dates, times, locations, booking references and requested actions. Do not invent
            facts. collectedDetails and unresolvedItems must be JSON string arrays. detectedLanguage
            must be bn-BD, en-US, or mixed. suggestedOpening must be one short sentence in the
            caller's language that lets the human agent continue without asking the caller to repeat.
            """;
        var request = new OllamaRequest(
            string.IsNullOrWhiteSpace(profile.Model) ? "qwen3:4b" : profile.Model,
            [new("system", prompt), new("user", $"Handoff reason: {cleanReason}\nTranscript:\n{cleanTranscript}")],
            false, "json", false, new { temperature = 0.1 });

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(profile.TimeoutSeconds, 5, 30)));
                using var response = await _clients.CreateClient("ollama")
                    .PostAsJsonAsync(endpoint, request, JsonOptions, timeout.Token);
                response.EnsureSuccessStatusCode();
                var envelope = await response.Content.ReadFromJsonAsync<OllamaResponse>(JsonOptions, timeout.Token);
                var parsed = JsonSerializer.Deserialize<StructuredSummary>(
                    envelope?.Message?.Content ?? string.Empty, JsonOptions);
                if (parsed is null || string.IsNullOrWhiteSpace(parsed.Summary)
                    || string.IsNullOrWhiteSpace(parsed.SuggestedOpening))
                    throw new JsonException("Ollama returned an incomplete handoff summary.");
                return new HandoffContextResult(
                    Limit(parsed.Summary, 1500), Limit(parsed.CustomerIntent, 300),
                    NormalizeLanguage(parsed.DetectedLanguage, cleanTranscript),
                    Limit(parsed.Sentiment, 30, "neutral"),
                    CleanList(parsed.CollectedDetails), CleanList(parsed.UnresolvedItems),
                    Limit(parsed.SuggestedOpening, 500), $"Ollama/{request.Model}", false);
            }
            catch (Exception ex) when (attempt < 2 && !ct.IsCancellationRequested)
            { _logger.LogWarning(ex, "Ollama handoff summary attempt {Attempt} failed; retrying once.", attempt); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { _logger.LogWarning(ex, "Ollama handoff summary unavailable; using the safe local fallback."); }
        }
        return Fallback(cleanTranscript, cleanReason, $"Ollama/{request.Model}");
    }

    private static Uri BuildChatEndpoint(string baseUrl)
    {
        var value = string.IsNullOrWhiteSpace(baseUrl) ? "http://ollama:11434" : baseUrl.Trim().TrimEnd('/');
        if (value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) value = value[..^3];
        return new Uri(value + "/api/chat", UriKind.Absolute);
    }

    private static HandoffContextResult Fallback(string transcript, string reason, string provider)
    {
        var language = NormalizeLanguage(null, transcript);
        var lines = transcript.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var customerLines = lines.Where(x => x.StartsWith("Customer:", StringComparison.OrdinalIgnoreCase))
            .Select(x => x["Customer:".Length..].Trim()).Where(x => x.Length > 0).ToList();
        var last = customerLines.LastOrDefault() ?? transcript.Trim();
        var summary = string.IsNullOrWhiteSpace(last) ? reason : Limit(last, 700);
        var opening = language == "bn-BD"
            ? "আপনার আগের কথোপকথনের তথ্য আমার সামনে আছে—আমি এখান থেকেই আপনাকে সাহায্য করছি।"
            : "I have your earlier conversation in front of me, so I can continue from here.";
        return new HandoffContextResult(summary, reason, language, "unknown", [],
            ["Human agent should confirm the requested resolution."], opening, provider, true);
    }

    private static string NormalizeLanguage(string? value, string transcript)
    {
        if (string.Equals(value, "bn-BD", StringComparison.OrdinalIgnoreCase)) return "bn-BD";
        if (string.Equals(value, "en-US", StringComparison.OrdinalIgnoreCase)) return "en-US";
        if (string.Equals(value, "mixed", StringComparison.OrdinalIgnoreCase)) return "mixed";
        return transcript.Any(x => x is >= '\u0980' and <= '\u09FF') ? "bn-BD" : "en-US";
    }

    private static IReadOnlyList<string> CleanList(IEnumerable<string>? values) =>
        (values ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Limit(x, 300)).Take(12).ToList();
    private static string Limit(string? value, int max, string fallback = "") =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim()[..Math.Min(value.Trim().Length, max)];
}
