using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CCaaS.Api.Controllers;

[Authorize]
[Route("api/local-ai")]
public sealed class LocalAiController : ControllerBase
{
    private readonly IHttpClientFactory _clients;
    private readonly IConfiguration _configuration;

    public LocalAiController(IHttpClientFactory clients, IConfiguration configuration)
    {
        _clients = clients;
        _configuration = configuration;
    }

    [HttpPost("chat")]
    public async Task<IActionResult> Chat([FromBody] LocalChatRequest request, CancellationToken ct)
    {
        if (request.Messages is null || request.Messages.Value.ValueKind != JsonValueKind.Array)
            return BadRequest(new { message = "At least one chat message is required." });

        var baseUrl = (_configuration["LocalAi:OllamaBaseUrl"] ?? "http://ollama:11434").TrimEnd('/');
        var model = _configuration["LocalAi:OllamaModel"] ?? "qwen3:4b";
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = request.Messages.Value,
            ["stream"] = false,
            ["think"] = false
        };
        if (request.Tools.HasValue && request.Tools.Value.ValueKind == JsonValueKind.Array)
            payload["tools"] = request.Tools.Value;

        using var response = await _clients.CreateClient("ollama")
            .PostAsJsonAsync($"{baseUrl}/api/chat", payload, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            return StatusCode((int)response.StatusCode, new { message = "Local language model request failed.", details = body });

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("message", out var message))
            return StatusCode(502, new { message = "Local language model returned no message." });

        var content = message.TryGetProperty("content", out var contentValue) ? contentValue.GetString() ?? "" : "";
        var toolCalls = message.TryGetProperty("tool_calls", out var calls) ? calls.Clone() : (JsonElement?)null;
        return Ok(new { content, toolCalls });
    }

    [HttpPost("transcriptions")]
    [RequestSizeLimit(25_000_000)]
    public async Task<IActionResult> Transcribe(IFormFile file, [FromForm] string language = "auto", CancellationToken ct = default)
    {
        if (file.Length == 0) return BadRequest(new { message = "Audio file is required." });
        var baseUrl = (_configuration["LocalAi:SpeechBaseUrl"] ?? "http://local-ai:8080").TrimEnd('/');
        await using var input = file.OpenReadStream();
        using var form = new MultipartFormDataContent();
        using var audio = new StreamContent(input);
        audio.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType ?? "application/octet-stream");
        form.Add(audio, "file", file.FileName);
        form.Add(new StringContent(language), "language");
        using var response = await _clients.CreateClient("local-ai-speech")
            .PostAsync($"{baseUrl}/v1/audio/transcriptions", form, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return new ContentResult { StatusCode = (int)response.StatusCode, ContentType = "application/json", Content = body };
    }

    [HttpPost("speech")]
    public async Task<IActionResult> Speech([FromBody] LocalSpeechRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Input)) return BadRequest(new { message = "Speech text is required." });
        var baseUrl = (_configuration["LocalAi:SpeechBaseUrl"] ?? "http://local-ai:8080").TrimEnd('/');
        using var response = await _clients.CreateClient("local-ai-speech")
            .PostAsJsonAsync($"{baseUrl}/v1/audio/speech", request, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (!response.IsSuccessStatusCode)
            return StatusCode((int)response.StatusCode, new { message = "Local speech generation failed." });
        return File(bytes, response.Content.Headers.ContentType?.MediaType ?? "audio/wav");
    }

    public sealed record LocalChatRequest(JsonElement? Messages, JsonElement? Tools);
    public sealed record LocalSpeechRequest(string Input, string Language = "bn-BD", string? Voice = null);
}
