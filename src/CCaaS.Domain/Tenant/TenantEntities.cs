using CCaaS.Domain.Common;

namespace CCaaS.Domain.Tenant;

// Schema: tenant  (Section 7 - Multi-Tenant SaaS Architecture)
// Tenant is the root boundary for data and billing. Note: Tenant itself is NOT
// ITenantOwned (it IS the tenant), everything else in the system hangs off TenantId.

public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = default!;
    public string Slug { get; set; } = default!; // used for subdomain / tenant admin URL
    public TenantEdition Edition { get; set; } = TenantEdition.Voice;
    public TenantStatus Status { get; set; } = TenantStatus.Trial;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public TenantSettings? Settings { get; set; }
    public ICollection<FeatureEntitlement> FeatureEntitlements { get; set; } = new List<FeatureEntitlement>();
}

public enum TenantEdition
{
    Voice,        // Section 3 - Voice CCaaS
    Omnichannel,  // + digital channels
    Enterprise    // dedicated deployment / custom
}

public enum TenantStatus
{
    Trial,
    Active,
    Suspended,
    Cancelled
}

public class TenantSettings : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string TimeZone { get; set; } = "Asia/Dhaka";
    public string DefaultLanguage { get; set; } = "en"; // NFR: English-only UI for V1
    public int RecordingRetentionDays { get; set; } = 90;
    public int CdrRetentionDays { get; set; } = 365;
}

public class FeatureEntitlement : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    // Example keys: "channel.whatsapp", "ai.transcription", "dialer.progressive"
    public string FeatureKey { get; set; } = default!;
    public bool Enabled { get; set; }
    public int? Quota { get; set; } // e.g. seat count, storage GB, AI minutes
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class SystemSetting : BaseEntity
{
    public string Category { get; set; } = "General";
    public string Key { get; set; } = default!;
    public string Value { get; set; } = default!;
    public string ValueType { get; set; } = "string";
    public string? Description { get; set; }
    public bool IsSecret { get; set; }
    public bool IsActive { get; set; } = true;
}

public class AiProviderProfile : BaseEntity
{
    public string Name { get; set; } = default!;
    public string ProviderType { get; set; } = "Ollama";
    public string Capability { get; set; } = "Llm";
    public string BaseUrl { get; set; } = default!;
    public string Model { get; set; } = default!;
    public string? SecretStoreReference { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
    public int Priority { get; set; } = 100;
    public bool IsDevelopment { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public string OptionsJson { get; set; } = "{}";
}

public class LanguageProfile : BaseEntity
{
    public string Code { get; set; } = "en-US";
    public string DisplayName { get; set; } = "English";
    public string SpeechRecognitionCode { get; set; } = "en";
    public string VoiceName { get; set; } = "default";
    public string WelcomeMessage { get; set; } = default!;
    public string FallbackMessage { get; set; } = default!;
    public int Priority { get; set; } = 100;
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
}
