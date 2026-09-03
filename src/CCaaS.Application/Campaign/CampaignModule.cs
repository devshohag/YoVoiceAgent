using CCaaS.Application.Common;
using CCaaS.Domain.Campaign;

namespace CCaaS.Application.Campaign;

public record CreateCampaignRequest(string Name, CampaignChannel Channel, DialMode DialMode);
public record AddLeadsToCampaignRequest(Guid CampaignId, List<Guid> LeadIds);

public interface ICampaignService
{
    Task<Domain.Campaign.Campaign> CreateCampaignAsync(Guid tenantId, CreateCampaignRequest request, CancellationToken ct = default);
    Task AddLeadsAsync(Guid tenantId, AddLeadsToCampaignRequest request, CancellationToken ct = default);

    /// <summary>
    /// Preview/Progressive dialer "next eligible lead" selection (Section 10). Predictive
    /// dialing is intentionally NOT implemented here - see the DialMode.Predictive comment
    /// in the Domain layer and Section 25 risk register ("defer Kamailio/predictive dialing
    /// until core acceptance criteria pass").
    /// </summary>
    Task<CampaignLead?> GetNextLeadForDialAsync(Guid tenantId, Guid campaignId, CancellationToken ct = default);

    Task RecordAttemptOutcomeAsync(Guid tenantId, Guid campaignLeadId, string outcome, CancellationToken ct = default);
}

public class CampaignService : ICampaignService
{
    private readonly IRepository<Domain.Campaign.Campaign> _campaigns;
    private readonly IRepository<CampaignLead> _campaignLeads;
    private readonly IRepository<RetryPolicy> _retryPolicies;
    private readonly IUnitOfWork _unitOfWork;

    public CampaignService(
        IRepository<Domain.Campaign.Campaign> campaigns,
        IRepository<CampaignLead> campaignLeads,
        IRepository<RetryPolicy> retryPolicies,
        IUnitOfWork unitOfWork)
    {
        _campaigns = campaigns;
        _campaignLeads = campaignLeads;
        _retryPolicies = retryPolicies;
        _unitOfWork = unitOfWork;
    }

    public async Task<Domain.Campaign.Campaign> CreateCampaignAsync(Guid tenantId, CreateCampaignRequest request, CancellationToken ct = default)
    {
        var campaign = new Domain.Campaign.Campaign
        {
            TenantId = tenantId,
            Name = request.Name,
            Channel = request.Channel,
            DialMode = request.DialMode,
            Status = CampaignStatus.Draft
        };
        await _campaigns.AddAsync(campaign, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return campaign;
    }

    public async Task AddLeadsAsync(Guid tenantId, AddLeadsToCampaignRequest request, CancellationToken ct = default)
    {
        foreach (var leadId in request.LeadIds)
        {
            await _campaignLeads.AddAsync(new CampaignLead
            {
                TenantId = tenantId,
                CampaignId = request.CampaignId,
                LeadId = leadId
            }, ct);
        }
        await _unitOfWork.SaveChangesAsync(ct);
    }

    public async Task<CampaignLead?> GetNextLeadForDialAsync(Guid tenantId, Guid campaignId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var candidates = await _campaignLeads.ToListAsync(
            _campaignLeads.Query().Where(cl =>
                cl.TenantId == tenantId &&
                cl.CampaignId == campaignId &&
                (cl.NextAttemptAt == null || cl.NextAttemptAt <= now)),
            ct);

        return candidates.OrderBy(cl => cl.Attempts).FirstOrDefault();
    }

    public async Task RecordAttemptOutcomeAsync(Guid tenantId, Guid campaignLeadId, string outcome, CancellationToken ct = default)
    {
        var campaignLead = await _campaignLeads.FirstOrDefaultAsync(cl => cl.Id == campaignLeadId && cl.TenantId == tenantId, ct)
            ?? throw new KeyNotFoundException("Campaign lead not found.");

        campaignLead.Attempts += 1;
        campaignLead.LastOutcome = outcome;

        RetryPolicy? policy = campaignLead.CampaignId != Guid.Empty
            ? await _retryPolicies.FirstOrDefaultAsync(r => r.TenantId == tenantId, ct)
            : null;

        var retryMinutes = outcome switch
        {
            "busy" => policy?.BusyRetryMinutes ?? 30,
            "no-answer" => policy?.NoAnswerRetryMinutes ?? 60,
            "failed" => policy?.FailedRetryMinutes ?? 120,
            _ => (int?)null
        };

        campaignLead.NextAttemptAt = retryMinutes is null ? null : DateTime.UtcNow.AddMinutes(retryMinutes.Value);
        _campaignLeads.Update(campaignLead);
        await _unitOfWork.SaveChangesAsync(ct);
    }
}
