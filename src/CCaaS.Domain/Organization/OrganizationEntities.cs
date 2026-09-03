using CCaaS.Domain.Common;

namespace CCaaS.Domain.Organization;

// Schema: organization  (Section 4 - Functional Scope: Branches, teams, agents, skills, working states)

public class Branch : BaseEntity
{
    public string Name { get; set; } = default!;
    public string? Address { get; set; }

    public ICollection<Team> Teams { get; set; } = new List<Team>();
}

public class Team : BaseEntity
{
    // "Reusable workforce grouping" - Section 7 hierarchy table
    public Guid? BranchId { get; set; }
    public Branch? Branch { get; set; }
    public string Name { get; set; } = default!;

    public ICollection<Agent> Agents { get; set; } = new List<Agent>();
}

public class Agent : BaseEntity
{
    // "User/extension/presence/skill association" - Section 7
    public Guid UserId { get; set; } // FK to Identity.User
    public Guid? TeamId { get; set; }
    public Team? Team { get; set; }
    public string DisplayName { get; set; } = default!;
    public string? ExtensionNumber { get; set; } // maps to Telephony.Extension
    public AgentPresence Presence { get; set; } = AgentPresence.Offline;
    public DateTime? PresenceChangedAt { get; set; }

    public ICollection<AgentSkill> AgentSkills { get; set; } = new List<AgentSkill>();
}

public enum AgentPresence
{
    // Section 11 - Agent Workspace: Offline, Available, Busy, On Call, Wrap-up, Break, Training, Away
    Offline,
    Available,
    Busy,
    OnCall,
    WrapUp,
    Break,
    Training,
    Away
}

public class Skill : BaseEntity
{
    public string Name { get; set; } = default!;
}

public class AgentSkill : BaseEntity
{
    public Guid AgentId { get; set; }
    public Agent? Agent { get; set; }
    public Guid SkillId { get; set; }
    public Skill? Skill { get; set; }
    public int ProficiencyLevel { get; set; } = 1; // 1-5, used for skill-based routing
}
