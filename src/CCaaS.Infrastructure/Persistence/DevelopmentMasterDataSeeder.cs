using CCaaS.Domain.Organization;
using CCaaS.Domain.Calls;
using CCaaS.Domain.Telephony;
using CCaaS.Domain.Tenant;
using CCaaS.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CCaaS.Infrastructure.Persistence;

public static class DevelopmentMasterDataSeeder
{
    public static async Task SeedAsync(CcaasDbContext db, ILogger logger, CancellationToken ct = default)
    {
        foreach (var tenantId in await db.Tenants.IgnoreQueryFilters().Select(x => x.Id).ToListAsync(ct))
        {
            if (!await db.SystemSettings.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenantId, ct))
            {
                db.SystemSettings.AddRange(
                    new SystemSetting { TenantId = tenantId, Category = "Telephony", Key = "AiEntryExtension", Value = "7000", Description = "Development dialplan entry point." },
                    new SystemSetting { TenantId = tenantId, Category = "Telephony", Key = "FallbackHandoffExtension", Value = "1002", Description = "Used only when no available queue member is found." },
                    new SystemSetting { TenantId = tenantId, Category = "Telephony", Key = "AllowedCodecs", Value = "ulaw", Description = "Comma-separated Asterisk codec allowlist." },
                    new SystemSetting { TenantId = tenantId, Category = "Recording", Key = "FullCallRecordingEnabled", Value = "true", ValueType = "bool" },
                    new SystemSetting { TenantId = tenantId, Category = "Language", Key = "AutomaticDetectionEnabled", Value = "true", ValueType = "bool" });

            if (!await db.SystemSettings.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenantId
                    && x.Category == "AiVoice" && x.Key == "ProcessingMode", ct))
                db.SystemSettings.Add(new SystemSetting { TenantId = tenantId, Category = "AiVoice",
                    Key = "ProcessingMode", Value = "Local", Description = "Local or Puter development voice processing." });
            if (!await db.SystemSettings.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenantId
                    && x.Category == "AiVoice" && x.Key == "MaxConcurrentCalls", ct))
                db.SystemSettings.Add(new SystemSetting { TenantId = tenantId, Category = "AiVoice",
                    Key = "MaxConcurrentCalls", Value = "2", ValueType = "int",
                    Description = "Restart the telephony worker after changing local AI concurrency." });
            }

            if (!await db.AiProviderProfiles.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenantId, ct))
                db.AiProviderProfiles.AddRange(
                    new AiProviderProfile { TenantId = tenantId, Name = "Local Ollama", ProviderType = "Ollama", Capability = "Llm", BaseUrl = "http://ollama:11434", Model = "qwen3:4b", TimeoutSeconds = 30, Priority = 10 },
                    new AiProviderProfile { TenantId = tenantId, Name = "Production STT", ProviderType = "OpenAICompatible", Capability = "Stt", BaseUrl = "https://api.example.invalid/v1", Model = "configure-later", SecretStoreReference = "AI_STT_API_KEY", Priority = 100, IsDevelopment = false, IsActive = false });

            var localStt = await db.AiProviderProfiles.IgnoreQueryFilters().FirstOrDefaultAsync(x =>
                x.TenantId == tenantId && x.Capability == "Stt" && x.IsDevelopment, ct);
            if (localStt is null)
                db.AiProviderProfiles.Add(new AiProviderProfile { TenantId = tenantId,
                    Name = "Local Faster Whisper", ProviderType = "FasterWhisper", Capability = "Stt",
                    BaseUrl = "http://local-ai:8080", Model = "small", TimeoutSeconds = 120, Priority = 10 });
            else
            {
                localStt.Name = "Local Faster Whisper";
                localStt.ProviderType = "FasterWhisper";
                localStt.BaseUrl = "http://local-ai:8080";
                localStt.Model = "small";
                localStt.TimeoutSeconds = Math.Max(localStt.TimeoutSeconds, 120);
            }
            if (!await db.AiProviderProfiles.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenantId
                    && x.Capability == "Tts" && x.ProviderType == "Piper", ct))
                db.AiProviderProfiles.Add(new AiProviderProfile { TenantId = tenantId,
                    Name = "Local Piper", ProviderType = "Piper", Capability = "Tts",
                    BaseUrl = "http://local-ai:8080", Model = "bn-BD-google/en-US-lessac",
                    TimeoutSeconds = 120, Priority = 10 });

            if (!await db.AiAgents.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenantId, ct))
                db.AiAgents.Add(new AiAgent { TenantId = tenantId, Name = "Appointment Agent",
                    Language = "bn-BD", VoiceName = "default",
                    SystemPrompt = "You are a concise appointment and customer-support voice agent. Collect one detail at a time, never invent confirmed bookings, and transfer to a human when requested or uncertain.",
                    WelcomeMessage = "স্বাগতম। আমি কীভাবে আপনাকে সাহায্য করতে পারি?",
                    FallbackMessage = "আমি আপনাকে একজন মানব প্রতিনিধির কাছে সংযুক্ত করছি।" });

            if (!await db.LanguageProfiles.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenantId, ct))
                db.LanguageProfiles.AddRange(
                    new LanguageProfile { TenantId = tenantId, Code = "bn-BD", DisplayName = "বাংলা", SpeechRecognitionCode = "bn", WelcomeMessage = "স্বাগতম। আমি কীভাবে আপনাকে সাহায্য করতে পারি?", FallbackMessage = "আমি আপনাকে একজন মানব প্রতিনিধির কাছে সংযুক্ত করছি।", Priority = 10, IsDefault = true },
                    new LanguageProfile { TenantId = tenantId, Code = "en-US", DisplayName = "English", SpeechRecognitionCode = "en", WelcomeMessage = "Welcome. How may I help you?", FallbackMessage = "I am connecting you to a human agent.", Priority = 20 });

            if (!await db.Dispositions.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenantId, ct))
                db.Dispositions.AddRange(
                    new Disposition { TenantId = tenantId, Code = "resolved", Description = "Resolved", RequiresFollowUp = false },
                    new Disposition { TenantId = tenantId, Code = "callback", Description = "Callback requested", RequiresFollowUp = true },
                    new Disposition { TenantId = tenantId, Code = "appointment", Description = "Appointment booked", RequiresFollowUp = false },
                    new Disposition { TenantId = tenantId, Code = "not-interested", Description = "Not interested", RequiresFollowUp = false },
                    new Disposition { TenantId = tenantId, Code = "escalated", Description = "Escalated", RequiresFollowUp = true });

            if (!await db.AsteriskNodes.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenantId, ct))
                db.AsteriskNodes.Add(new AsteriskNode { TenantId = tenantId, Name = "Development Asterisk",
                    AriBaseUrl = "http://asterisk:8088/ari", AmiHost = "asterisk", AmiPort = 5038 });

            var queue = await db.Queues.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Name == "Default Human Support", ct);
            if (queue is null)
            {
                queue = new Queue { TenantId = tenantId, Name = "Default Human Support", Strategy = "roundrobin", MaxWaitSeconds = 30 };
                db.Queues.Add(queue);
            }

            var adminUserId = await db.Users.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).Select(x => x.Id).FirstOrDefaultAsync(ct);
            var agent = await db.Agents.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.ExtensionNumber == "1002", ct);
            if (agent is null && adminUserId != Guid.Empty)
            {
                agent = new Agent { TenantId = tenantId, UserId = adminUserId, DisplayName = "Development Human Agent", ExtensionNumber = "1002", Presence = AgentPresence.Available, PresenceChangedAt = DateTime.UtcNow };
                db.Agents.Add(agent);
            }
            await db.SaveChangesAsync(ct);

            if (agent is not null && !await db.Extensions.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenantId && x.ExtensionNumber == "1002", ct))
                db.Extensions.Add(new Extension { TenantId = tenantId, AgentId = agent.Id, ExtensionNumber = "1002", SecretStoreReference = "ASTERISK_EXTENSION_1002_PASSWORD" });
            if (agent is not null && !await db.QueueMembers.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenantId && x.QueueId == queue.Id && x.AgentId == agent.Id, ct))
                db.QueueMembers.Add(new QueueMember { TenantId = tenantId, QueueId = queue.Id, AgentId = agent.Id, Priority = 10 });
            await db.SaveChangesAsync(ct);
        }
        logger.LogInformation("Development master data and dynamic routing configuration ready.");
    }
}
