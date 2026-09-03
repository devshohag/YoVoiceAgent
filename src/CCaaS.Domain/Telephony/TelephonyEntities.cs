using CCaaS.Domain.Common;

namespace CCaaS.Domain.Telephony;

// Schema: telephony  (Section 8 - Voice & Telephony Architecture / Section 12 - Telephony domain objects)

public class SipTrunk : BaseEntity
{
    // "Provider host, auth/IP mode, outbound routing, tenant ownership"
    public string ProviderName { get; set; } = default!; // licensed BTRC IPTSP/SIP provider name
    public string Host { get; set; } = default!;
    public int Port { get; set; } = 5060;
    public SipAuthMode AuthMode { get; set; } = SipAuthMode.UserPassword;
    public string? Username { get; set; }
    public string? SecretStoreReference { get; set; } // never store the raw SIP password here
    public bool IsActive { get; set; } = true;
}

public enum SipAuthMode { UserPassword, IpBased }

public class DidNumber : BaseEntity
{
    // "Inbound number mapped to IVR/queue/campaign"
    public string Number { get; set; } = default!;
    public Guid SipTrunkId { get; set; }
    public Guid? IvrId { get; set; }
    public Guid? QueueId { get; set; }
}

public class Extension : BaseEntity
{
    // "Agent/browser SIP identity and provisioning state" - e.g. 1001, 1002 in the demo scenario
    public string ExtensionNumber { get; set; } = default!;
    public Guid AgentId { get; set; }
    public string? SecretStoreReference { get; set; }
    public bool IsRegistered { get; set; }
}

public class Queue : BaseEntity
{
    public string Name { get; set; } = default!;
    public string Strategy { get; set; } = "ringall"; // Asterisk queue strategy
    public int? MaxWaitSeconds { get; set; }
}

public class QueueMember : BaseEntity
{
    public Guid QueueId { get; set; }
    public Guid AgentId { get; set; }
    public int Priority { get; set; } = 100;
    public int Penalty { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Ivr : BaseEntity
{
    public string Name { get; set; } = default!;
    public string MenuDefinitionJson { get; set; } = "{}"; // DTMF option -> destination mapping
}

public class RoutingRule : BaseEntity
{
    public Guid? QueueId { get; set; }
    public string MatchExpression { get; set; } = default!; // e.g. skill/priority/time-of-day rule
    public int Priority { get; set; }
}

public class AsteriskNode : BaseEntity
{
    // "Telephony node registration/health/capacity metadata" - used by the scale-out path (Section 18).
    public string Name { get; set; } = default!;
    public string AriBaseUrl { get; set; } = default!;
    public string AmiHost { get; set; } = default!;
    public int AmiPort { get; set; } = 5038;
    public bool IsHealthy { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }
}
