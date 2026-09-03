using System.Text;
using CCaaS.Domain.Organization;
using CCaaS.Domain.Telephony;
using CCaaS.Domain.Tenant;
using CCaaS.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CCaaS.Api.Controllers;

[Authorize(Roles = "PlatformAdmin,TenantAdmin")]
[Route("api/master-data")]
public sealed class MasterDataController : ApiControllerBase
{
    private readonly CcaasDbContext _db;
    public MasterDataController(CcaasDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(new
    {
        settings = await _db.SystemSettings.AsNoTracking().OrderBy(x => x.Category).ThenBy(x => x.Key).ToListAsync(ct),
        aiProviders = await _db.AiProviderProfiles.AsNoTracking().OrderBy(x => x.Capability).ThenBy(x => x.Priority).ToListAsync(ct),
        languages = await _db.LanguageProfiles.AsNoTracking().OrderBy(x => x.Priority).ToListAsync(ct),
        agents = await _db.Agents.AsNoTracking().OrderBy(x => x.DisplayName).ToListAsync(ct),
        extensions = await _db.Extensions.AsNoTracking().OrderBy(x => x.ExtensionNumber).ToListAsync(ct),
        queues = await _db.Queues.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct),
        queueMembers = await _db.QueueMembers.AsNoTracking().OrderBy(x => x.Priority).ToListAsync(ct),
        trunks = await _db.SipTrunks.AsNoTracking().OrderBy(x => x.ProviderName).ToListAsync(ct),
        dids = await _db.DidNumbers.AsNoTracking().OrderBy(x => x.Number).ToListAsync(ct),
        asteriskNodes = await _db.AsteriskNodes.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct)
    });

    [HttpPut("settings/{id:guid}")]
    public async Task<IActionResult> SaveSetting(Guid id, [FromBody] SettingRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Key)) return BadRequest(new { message = "Setting key is required." });
        var item = id == Guid.Empty ? null : await _db.SystemSettings.SingleOrDefaultAsync(x => x.Id == id, ct);
        item ??= new SystemSetting { TenantId = TenantId };
        item.Category = Clean(request.Category, 100, "General");
        item.Key = Clean(request.Key, 150);
        item.Value = request.Value?.Trim() ?? string.Empty;
        item.ValueType = Clean(request.ValueType, 30, "string");
        item.Description = Trim(request.Description, 500);
        item.IsSecret = request.IsSecret;
        item.IsActive = request.IsActive;
        if (id == Guid.Empty) _db.SystemSettings.Add(item);
        await _db.SaveChangesAsync(ct);
        return Ok(item);
    }

    [HttpPut("ai-providers/{id:guid}")]
    public async Task<IActionResult> SaveAiProvider(Guid id, [FromBody] AiProviderRequest request, CancellationToken ct)
    {
        var item = id == Guid.Empty ? null : await _db.AiProviderProfiles.SingleOrDefaultAsync(x => x.Id == id, ct);
        item ??= new AiProviderProfile { TenantId = TenantId };
        item.Name = Clean(request.Name, 120);
        item.ProviderType = Clean(request.ProviderType, 50);
        item.Capability = Clean(request.Capability, 30);
        item.BaseUrl = Clean(request.BaseUrl, 500);
        item.Model = Clean(request.Model, 150);
        item.SecretStoreReference = Trim(request.SecretStoreReference, 250);
        item.TimeoutSeconds = Math.Clamp(request.TimeoutSeconds, 5, 600);
        item.Priority = Math.Clamp(request.Priority, 1, 10000);
        item.IsDevelopment = request.IsDevelopment;
        item.IsActive = request.IsActive;
        item.OptionsJson = string.IsNullOrWhiteSpace(request.OptionsJson) ? "{}" : request.OptionsJson;
        if (id == Guid.Empty) _db.AiProviderProfiles.Add(item);
        await _db.SaveChangesAsync(ct);
        return Ok(item);
    }

    [HttpPut("languages/{id:guid}")]
    public async Task<IActionResult> SaveLanguage(Guid id, [FromBody] LanguageRequest request, CancellationToken ct)
    {
        var item = id == Guid.Empty ? null : await _db.LanguageProfiles.SingleOrDefaultAsync(x => x.Id == id, ct);
        item ??= new LanguageProfile { TenantId = TenantId };
        if (request.IsDefault)
            await _db.LanguageProfiles.Where(x => x.IsDefault && x.Id != item.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.IsDefault, false), ct);
        item.Code = Clean(request.Code, 20);
        item.DisplayName = Clean(request.DisplayName, 80);
        item.SpeechRecognitionCode = Clean(request.SpeechRecognitionCode, 20);
        item.VoiceName = Clean(request.VoiceName, 100, "default");
        item.WelcomeMessage = Clean(request.WelcomeMessage, 1000);
        item.FallbackMessage = Clean(request.FallbackMessage, 1000);
        item.Priority = Math.Clamp(request.Priority, 1, 10000);
        item.IsDefault = request.IsDefault;
        item.IsActive = request.IsActive;
        if (id == Guid.Empty) _db.LanguageProfiles.Add(item);
        await _db.SaveChangesAsync(ct);
        return Ok(item);
    }

    [HttpPut("agents/{id:guid}")]
    public async Task<IActionResult> SaveAgent(Guid id, [FromBody] AgentRequest request, CancellationToken ct)
    {
        var item = id == Guid.Empty ? null : await _db.Agents.SingleOrDefaultAsync(x => x.Id == id, ct);
        item ??= new Agent { TenantId = TenantId, UserId = request.UserId == Guid.Empty ? Guid.NewGuid() : request.UserId };
        item.DisplayName = Clean(request.DisplayName, 120);
        item.ExtensionNumber = Trim(request.ExtensionNumber, 20);
        item.TeamId = request.TeamId;
        item.Presence = request.Presence;
        item.PresenceChangedAt = DateTime.UtcNow;
        if (id == Guid.Empty) _db.Agents.Add(item);
        await _db.SaveChangesAsync(ct);
        return Ok(item);
    }

    [HttpPut("queues/{id:guid}")]
    public async Task<IActionResult> SaveQueue(Guid id, [FromBody] QueueRequest request, CancellationToken ct)
    {
        var item = id == Guid.Empty ? null : await _db.Queues.SingleOrDefaultAsync(x => x.Id == id, ct);
        item ??= new Queue { TenantId = TenantId };
        item.Name = Clean(request.Name, 120);
        item.Strategy = Clean(request.Strategy, 40, "roundrobin");
        item.MaxWaitSeconds = Math.Clamp(request.MaxWaitSeconds ?? 30, 5, 3600);
        if (id == Guid.Empty) _db.Queues.Add(item);
        await _db.SaveChangesAsync(ct);
        return Ok(item);
    }

    [HttpPut("extensions/{id:guid}")]
    public async Task<IActionResult> SaveExtension(Guid id, [FromBody] ExtensionRequest request, CancellationToken ct)
    {
        if (request.ExtensionNumber.Any(x => !char.IsDigit(x))) return BadRequest(new { message = "Extension must contain digits only." });
        var item = id == Guid.Empty ? null : await _db.Extensions.SingleOrDefaultAsync(x => x.Id == id, ct);
        item ??= new Extension { TenantId = TenantId };
        item.ExtensionNumber = Clean(request.ExtensionNumber, 20);
        item.AgentId = request.AgentId;
        item.SecretStoreReference = Trim(request.SecretStoreReference, 250);
        item.IsRegistered = request.IsRegistered;
        if (id == Guid.Empty) _db.Extensions.Add(item);
        await _db.SaveChangesAsync(ct);
        return Ok(item);
    }

    [HttpPut("queue-members/{id:guid}")]
    public async Task<IActionResult> SaveQueueMember(Guid id, [FromBody] QueueMemberRequest request, CancellationToken ct)
    {
        var item = id == Guid.Empty ? null : await _db.QueueMembers.SingleOrDefaultAsync(x => x.Id == id, ct);
        item ??= new QueueMember { TenantId = TenantId };
        item.QueueId = request.QueueId;
        item.AgentId = request.AgentId;
        item.Priority = Math.Clamp(request.Priority, 1, 10000);
        item.Penalty = Math.Clamp(request.Penalty, 0, 100);
        item.IsActive = request.IsActive;
        if (id == Guid.Empty) _db.QueueMembers.Add(item);
        await _db.SaveChangesAsync(ct);
        return Ok(item);
    }

    [HttpGet("routing/next-agent")]
    public async Task<IActionResult> NextAgent([FromQuery] Guid? queueId, CancellationToken ct)
    {
        var query = from member in _db.QueueMembers.AsNoTracking()
                    join agent in _db.Agents.AsNoTracking() on member.AgentId equals agent.Id
                    join extension in _db.Extensions.AsNoTracking() on agent.Id equals extension.AgentId
                    where member.IsActive && (queueId == null || member.QueueId == queueId)
                       && agent.Presence == AgentPresence.Available
                    orderby member.Priority, member.Penalty, agent.PresenceChangedAt
                    select new { agent.Id, agent.DisplayName, extension.ExtensionNumber, member.QueueId };
        var result = await query.FirstOrDefaultAsync(ct);
        return result is null ? NotFound(new { message = "No available human agent is configured." }) : Ok(result);
    }

    [HttpGet("asterisk/preview")]
    public async Task<IActionResult> AsteriskPreview(CancellationToken ct)
    {
        var extensions = await _db.Extensions.AsNoTracking().OrderBy(x => x.ExtensionNumber).ToListAsync(ct);
        var builder = new StringBuilder("; Generated preview. Passwords are resolved by the deployment secret provider.\n");
        foreach (var extension in extensions)
        {
            builder.AppendLine($"[{extension.ExtensionNumber}]").AppendLine("type=aor").AppendLine("max_contacts=1").AppendLine();
            builder.AppendLine($"[{extension.ExtensionNumber}]").AppendLine("type=auth").AppendLine("auth_type=userpass")
                .AppendLine($"username={extension.ExtensionNumber}").AppendLine($"password=${{SECRET:{extension.SecretStoreReference ?? "missing"}}}").AppendLine();
            builder.AppendLine($"[{extension.ExtensionNumber}]").AppendLine("type=endpoint").AppendLine("context=internal")
                .AppendLine("disallow=all").AppendLine("allow=ulaw").AppendLine("direct_media=no")
                .AppendLine("rewrite_contact=yes").AppendLine("force_rport=yes").AppendLine("rtp_symmetric=yes")
                .AppendLine($"auth={extension.ExtensionNumber}").AppendLine($"aors={extension.ExtensionNumber}").AppendLine();
        }
        return Content(builder.ToString(), "text/plain", Encoding.UTF8);
    }

    private static string Clean(string? value, int max, string fallback = "") =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim()[..Math.Min(value.Trim().Length, max)];
    private static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];

    public sealed record SettingRequest(string Category, string Key, string Value, string ValueType, string? Description, bool IsSecret, bool IsActive);
    public sealed record AiProviderRequest(string Name, string ProviderType, string Capability, string BaseUrl, string Model, string? SecretStoreReference, int TimeoutSeconds, int Priority, bool IsDevelopment, bool IsActive, string OptionsJson);
    public sealed record LanguageRequest(string Code, string DisplayName, string SpeechRecognitionCode, string VoiceName, string WelcomeMessage, string FallbackMessage, int Priority, bool IsDefault, bool IsActive);
    public sealed record AgentRequest(Guid UserId, string DisplayName, string? ExtensionNumber, Guid? TeamId, AgentPresence Presence);
    public sealed record QueueRequest(string Name, string Strategy, int? MaxWaitSeconds);
    public sealed record ExtensionRequest(string ExtensionNumber, Guid AgentId, string? SecretStoreReference, bool IsRegistered);
    public sealed record QueueMemberRequest(Guid QueueId, Guid AgentId, int Priority, int Penalty, bool IsActive);
}
