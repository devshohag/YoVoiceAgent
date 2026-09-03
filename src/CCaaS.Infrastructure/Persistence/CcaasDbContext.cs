using System.Linq.Expressions;
using CCaaS.Domain.Audit;
using CCaaS.Domain.Ai;
using CCaaS.Domain.Appointment;
using CCaaS.Domain.Billing;
using CCaaS.Domain.Calls;
using CCaaS.Domain.Campaign;
using CCaaS.Domain.Channel;
using CCaaS.Domain.Common;
using CCaaS.Domain.Conversation;
using CCaaS.Domain.Crm;
using CCaaS.Domain.Followup;
using CCaaS.Domain.Identity;
using CCaaS.Domain.Integration;
using CCaaS.Domain.Organization;
using CCaaS.Domain.Reporting;
using CCaaS.Domain.Telephony;
using CCaaS.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;
using TenantEntity = CCaaS.Domain.Tenant.Tenant;

namespace CCaaS.Infrastructure.Persistence;

/// <summary>
/// Section 12 - Data Architecture: "Use one SQL Server database initially, separated with
/// explicit schemas to preserve modular boundaries. All tenant-owned business entities
/// include TenantId and standard audit columns."
///
/// The schema names below match the proposal's table exactly: identity, tenant, organization,
/// crm, campaign, conversation, channel, telephony, calls, followup, billing, reporting, audit,
/// integration.
/// </summary>
public class CcaasDbContext : DbContext
{
    private readonly ICurrentTenant _currentTenant;

    // currentTenant defaults to an unresolved null-object (never null itself) so design-time
    // tooling (e.g. `dotnet ef migrations add`, which builds this context outside a request
    // scope) never hits a NullReferenceException while evaluating the global query filters.
    public CcaasDbContext(DbContextOptions<CcaasDbContext> options, ICurrentTenant? currentTenant = null) : base(options)
    {
        _currentTenant = currentTenant ?? new UnresolvedCurrentTenant();
    }

    private sealed class UnresolvedCurrentTenant : ICurrentTenant
    {
        public Guid TenantId => Guid.Empty;
        public bool IsResolved => false;
    }

    // identity
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Session> Sessions => Set<Session>();

    // tenant
    public DbSet<TenantEntity> Tenants => Set<TenantEntity>();
    public DbSet<CCaaS.Domain.Tenant.TenantSettings> TenantSettings => Set<CCaaS.Domain.Tenant.TenantSettings>();
    public DbSet<CCaaS.Domain.Tenant.FeatureEntitlement> FeatureEntitlements => Set<CCaaS.Domain.Tenant.FeatureEntitlement>();
    public DbSet<CCaaS.Domain.Tenant.SystemSetting> SystemSettings => Set<CCaaS.Domain.Tenant.SystemSetting>();
    public DbSet<CCaaS.Domain.Tenant.AiProviderProfile> AiProviderProfiles => Set<CCaaS.Domain.Tenant.AiProviderProfile>();
    public DbSet<CCaaS.Domain.Tenant.LanguageProfile> LanguageProfiles => Set<CCaaS.Domain.Tenant.LanguageProfile>();

    // organization
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<AgentSkill> AgentSkills => Set<AgentSkill>();

    // crm
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<CustomerTag> CustomerTags => Set<CustomerTag>();

    // campaign
    public DbSet<CCaaS.Domain.Campaign.Campaign> Campaigns => Set<CCaaS.Domain.Campaign.Campaign>();
    public DbSet<CampaignLead> CampaignLeads => Set<CampaignLead>();
    public DbSet<CampaignAgent> CampaignAgents => Set<CampaignAgent>();
    public DbSet<Script> Scripts => Set<Script>();
    public DbSet<RetryPolicy> RetryPolicies => Set<RetryPolicy>();

    // conversation
    public DbSet<CCaaS.Domain.Conversation.Conversation> Conversations => Set<CCaaS.Domain.Conversation.Conversation>();
    public DbSet<Participant> Participants => Set<Participant>();
    public DbSet<Interaction> Interactions => Set<Interaction>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<SlaEvent> SlaEvents => Set<SlaEvent>();

    // channel
    public DbSet<ChannelAccount> ChannelAccounts => Set<ChannelAccount>();
    public DbSet<ChannelCredentialRef> ChannelCredentialRefs => Set<ChannelCredentialRef>();
    public DbSet<MessageTemplate> MessageTemplates => Set<MessageTemplate>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();

    // telephony
    public DbSet<SipTrunk> SipTrunks => Set<SipTrunk>();
    public DbSet<DidNumber> DidNumbers => Set<DidNumber>();
    public DbSet<Extension> Extensions => Set<Extension>();
    public DbSet<Queue> Queues => Set<Queue>();
    public DbSet<QueueMember> QueueMembers => Set<QueueMember>();
    public DbSet<Ivr> Ivrs => Set<Ivr>();
    public DbSet<RoutingRule> RoutingRules => Set<RoutingRule>();
    public DbSet<AsteriskNode> AsteriskNodes => Set<AsteriskNode>();

    // calls
    public DbSet<CallSession> CallSessions => Set<CallSession>();
    public DbSet<CallLeg> CallLegs => Set<CallLeg>();
    public DbSet<CallEvent> CallEvents => Set<CallEvent>();
    public DbSet<Recording> Recordings => Set<Recording>();
    public DbSet<Disposition> Dispositions => Set<Disposition>();

    // ai
    public DbSet<AiAgent> AiAgents => Set<AiAgent>();
    public DbSet<AiAgentVersion> AiAgentVersions => Set<AiAgentVersion>();
    public DbSet<AiConversation> AiConversations => Set<AiConversation>();
    public DbSet<AiConversationTurn> AiConversationTurns => Set<AiConversationTurn>();
    public DbSet<AiToolDefinition> AiToolDefinitions => Set<AiToolDefinition>();
    public DbSet<AiToolExecution> AiToolExecutions => Set<AiToolExecution>();
    public DbSet<AiUsageRecord> AiUsageRecords => Set<AiUsageRecord>();

    // appointment
    public DbSet<AppointmentProvider> AppointmentProviders => Set<AppointmentProvider>();
    public DbSet<AppointmentAvailabilitySlot> AppointmentAvailabilitySlots => Set<AppointmentAvailabilitySlot>();
    public DbSet<AppointmentBooking> AppointmentBookings => Set<AppointmentBooking>();

    // followup
    public DbSet<FollowUpTask> FollowUpTasks => Set<FollowUpTask>();
    public DbSet<Callback> Callbacks => Set<Callback>();
    public DbSet<Reminder> Reminders => Set<Reminder>();

    // billing
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Seat> Seats => Set<Seat>();
    public DbSet<UsageMeter> UsageMeters => Set<UsageMeter>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<Payment> Payments => Set<Payment>();

    // reporting
    public DbSet<AgentDailyMetric> AgentDailyMetrics => Set<AgentDailyMetric>();
    public DbSet<QueueInterval> QueueIntervals => Set<QueueInterval>();
    public DbSet<CampaignMetric> CampaignMetrics => Set<CampaignMetric>();
    public DbSet<ChannelMetric> ChannelMetrics => Set<ChannelMetric>();

    // audit
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();

    // integration
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<IntegrationFailure> IntegrationFailures => Set<IntegrationFailure>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ApplySchema(modelBuilder, "identity", typeof(User), typeof(Role), typeof(Permission), typeof(UserRole), typeof(RolePermission), typeof(RefreshToken), typeof(Session));
        ApplySchema(modelBuilder, "tenant", typeof(TenantEntity), typeof(CCaaS.Domain.Tenant.TenantSettings), typeof(CCaaS.Domain.Tenant.FeatureEntitlement), typeof(CCaaS.Domain.Tenant.SystemSetting), typeof(CCaaS.Domain.Tenant.AiProviderProfile), typeof(CCaaS.Domain.Tenant.LanguageProfile));
        ApplySchema(modelBuilder, "organization", typeof(Branch), typeof(Team), typeof(Agent), typeof(Skill), typeof(AgentSkill));
        ApplySchema(modelBuilder, "crm", typeof(Lead), typeof(Customer), typeof(Contact), typeof(Note), typeof(Tag), typeof(CustomerTag));
        ApplySchema(modelBuilder, "campaign", typeof(CCaaS.Domain.Campaign.Campaign), typeof(CampaignLead), typeof(CampaignAgent), typeof(Script), typeof(RetryPolicy));
        ApplySchema(modelBuilder, "conversation", typeof(CCaaS.Domain.Conversation.Conversation), typeof(Participant), typeof(Interaction), typeof(Message), typeof(Attachment), typeof(Assignment), typeof(SlaEvent));
        ApplySchema(modelBuilder, "channel", typeof(ChannelAccount), typeof(ChannelCredentialRef), typeof(MessageTemplate), typeof(WebhookEvent));
        ApplySchema(modelBuilder, "telephony", typeof(SipTrunk), typeof(DidNumber), typeof(Extension), typeof(Queue), typeof(QueueMember), typeof(Ivr), typeof(RoutingRule), typeof(AsteriskNode));
        ApplySchema(modelBuilder, "calls", typeof(CallSession), typeof(CallLeg), typeof(CallEvent), typeof(Recording), typeof(Disposition));
        ApplySchema(modelBuilder, "ai", typeof(AiAgent), typeof(AiAgentVersion), typeof(AiConversation), typeof(AiConversationTurn), typeof(AiToolDefinition), typeof(AiToolExecution), typeof(AiUsageRecord));
        ApplySchema(modelBuilder, "appointment", typeof(AppointmentProvider), typeof(AppointmentAvailabilitySlot), typeof(AppointmentBooking));
        ApplySchema(modelBuilder, "followup", typeof(FollowUpTask), typeof(Callback), typeof(Reminder));
        ApplySchema(modelBuilder, "billing", typeof(Plan), typeof(Subscription), typeof(Seat), typeof(UsageMeter), typeof(Invoice), typeof(InvoiceLine), typeof(Payment));
        ApplySchema(modelBuilder, "reporting", typeof(AgentDailyMetric), typeof(QueueInterval), typeof(CampaignMetric), typeof(ChannelMetric));
        ApplySchema(modelBuilder, "audit", typeof(AuditLog), typeof(SecurityEvent));
        ApplySchema(modelBuilder, "integration", typeof(OutboxMessage), typeof(InboxMessage), typeof(IntegrationFailure));

        modelBuilder.Entity<AppointmentProvider>()
            .HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        modelBuilder.Entity<AppointmentAvailabilitySlot>()
            .HasIndex(x => new { x.TenantId, x.AppointmentProviderId, x.StartsAtUtc }).IsUnique();
        modelBuilder.Entity<AppointmentBooking>()
            .HasIndex(x => new { x.TenantId, x.AvailabilitySlotId }).IsUnique();
        modelBuilder.Entity<AppointmentBooking>()
            .HasIndex(x => new { x.TenantId, x.BookingReference }).IsUnique();

        // Section 7 security invariant, enforced at the model level:
        // "Tenant A must never be able to read, modify, export, search, stream, or download
        //  any object belonging to Tenant B." Every ITenantOwned entity gets an automatic
        // global query filter - callers never have to remember to add "WHERE TenantId = ...".
        //
        // EF Core only allows ONE HasQueryFilter per entity type, so BaseEntity types get a
        // single combined (tenant + soft-delete) filter, and plain ITenantOwned types (that
        // don't carry IsDeleted) get a tenant-only filter.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            if (typeof(BaseEntity).IsAssignableFrom(clrType))
            {
                var method = typeof(CcaasDbContext)
                    .GetMethod(nameof(BuildBaseEntityFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .MakeGenericMethod(clrType);
                var filter = (LambdaExpression)method.Invoke(this, null)!;
                modelBuilder.Entity(clrType).HasQueryFilter(filter);
            }
            else if (typeof(ITenantOwned).IsAssignableFrom(clrType))
            {
                var method = typeof(CcaasDbContext)
                    .GetMethod(nameof(BuildTenantOnlyFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .MakeGenericMethod(clrType);
                var filter = (LambdaExpression)method.Invoke(this, null)!;
                modelBuilder.Entity(clrType).HasQueryFilter(filter);
            }
        }
    }

    private LambdaExpression BuildBaseEntityFilter<T>() where T : BaseEntity
    {
        Expression<Func<T, bool>> filter = e =>
            !e.IsDeleted && (!_currentTenant.IsResolved || e.TenantId == _currentTenant.TenantId);
        return filter;
    }

    private LambdaExpression BuildTenantOnlyFilter<T>() where T : class, ITenantOwned
    {
        Expression<Func<T, bool>> filter = e => !_currentTenant.IsResolved || e.TenantId == _currentTenant.TenantId;
        return filter;
    }

    private static void ApplySchema(ModelBuilder modelBuilder, string schema, params Type[] entityTypes)
    {
        foreach (var type in entityTypes)
        {
            modelBuilder.Entity(type).ToTable(type.Name, schema);
        }
    }

    public override int SaveChanges()
    {
        ApplyAuditTimestamps();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyAuditTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void ApplyAuditTimestamps()
    {
        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            if (entry.State == Microsoft.EntityFrameworkCore.EntityState.Added)
                entry.Entity.CreatedAt = DateTime.UtcNow;
            if (entry.State is Microsoft.EntityFrameworkCore.EntityState.Added or Microsoft.EntityFrameworkCore.EntityState.Modified)
                entry.Entity.UpdatedAt = DateTime.UtcNow;
        }
    }
}
