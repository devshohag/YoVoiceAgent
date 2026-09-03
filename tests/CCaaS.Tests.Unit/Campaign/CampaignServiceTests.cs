using CCaaS.Application.Campaign;
using CCaaS.Domain.Campaign;
using CCaaS.Tests.Unit.Fakes;
using Xunit;

namespace CCaaS.Tests.Unit.Campaign;

// Section 10 - Retry Policy: "Busy/no-answer/failed retry windows and maximum attempts."
public class CampaignServiceTests
{
    private static CampaignService CreateSut(
        out FakeRepository<CCaaS.Domain.Campaign.Campaign> campaigns,
        out FakeRepository<CampaignLead> campaignLeads,
        out FakeRepository<RetryPolicy> retryPolicies)
    {
        campaigns = new FakeRepository<CCaaS.Domain.Campaign.Campaign>();
        campaignLeads = new FakeRepository<CampaignLead>();
        retryPolicies = new FakeRepository<RetryPolicy>();
        return new CampaignService(campaigns, campaignLeads, retryPolicies, new FakeUnitOfWork());
    }

    [Fact]
    public async Task RecordAttemptOutcomeAsync_BusyOutcome_SchedulesRetryUsingPolicyWindow()
    {
        var tenantId = Guid.NewGuid();
        var sut = CreateSut(out _, out var campaignLeads, out var retryPolicies);

        retryPolicies.Items.Add(new RetryPolicy { TenantId = tenantId, Name = "Default", BusyRetryMinutes = 45 });
        var campaignLead = new CampaignLead { TenantId = tenantId, CampaignId = Guid.NewGuid() };
        campaignLeads.Items.Add(campaignLead);

        var before = DateTime.UtcNow;
        await sut.RecordAttemptOutcomeAsync(tenantId, campaignLead.Id, "busy");

        Assert.Equal(1, campaignLead.Attempts);
        Assert.Equal("busy", campaignLead.LastOutcome);
        Assert.NotNull(campaignLead.NextAttemptAt);
        Assert.True(campaignLead.NextAttemptAt > before.AddMinutes(44) && campaignLead.NextAttemptAt < before.AddMinutes(46));
    }

    [Fact]
    public async Task GetNextLeadForDialAsync_ReturnsLeadWithFewestAttemptsFirst()
    {
        var tenantId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var sut = CreateSut(out _, out var campaignLeads, out _);

        var alreadyTried = new CampaignLead { TenantId = tenantId, CampaignId = campaignId, Attempts = 3 };
        var freshLead = new CampaignLead { TenantId = tenantId, CampaignId = campaignId, Attempts = 0 };
        campaignLeads.Items.Add(alreadyTried);
        campaignLeads.Items.Add(freshLead);

        var next = await sut.GetNextLeadForDialAsync(tenantId, campaignId);

        Assert.Equal(freshLead.Id, next!.Id);
    }
}
