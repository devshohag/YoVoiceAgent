namespace CCaaS.Shared.Constants;

/// <summary>
/// Central catalog of permission keys (Section 14: "Role + permission based policies,
/// tenant-scoped resource checks"). Keep this list in sync with the RolePermission seed data.
/// </summary>
public static class PermissionKeys
{
    public const string TenantManage = "tenant.manage";
    public const string OrganizationManage = "organization.manage";
    public const string AgentManage = "agent.manage";
    public const string CrmManage = "crm.manage";
    public const string CampaignManage = "campaign.manage";
    public const string CampaignDialerManage = "campaign.dialer.manage";
    public const string TelephonyManage = "telephony.manage";
    public const string CallRecordingDownload = "calls.recording.download";
    public const string ConversationView = "conversation.view";
    public const string ConversationAssign = "conversation.assign";
    public const string SupervisorMonitor = "supervisor.monitor";
    public const string SupervisorWhisperJoin = "supervisor.whisper-join";
    public const string BillingView = "billing.view";
    public const string BillingManage = "billing.manage";
    public const string ReportingView = "reporting.view";
    public const string AuditView = "audit.view";
}

public static class RoleNames
{
    public const string PlatformAdmin = "PlatformAdmin";
    public const string TenantAdmin = "TenantAdmin";
    public const string Supervisor = "Supervisor";
    public const string Agent = "Agent";
}
