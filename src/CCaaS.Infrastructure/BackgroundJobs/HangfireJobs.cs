using Hangfire;
using Microsoft.Extensions.Logging;

namespace CCaaS.Infrastructure.BackgroundJobs;

/// <summary>
/// Section 13 - Hangfire responsibilities: scheduled callbacks/reminders, campaign
/// start/stop schedules, invoice/subscription jobs, retry of recoverable provider
/// operations, retention/cleanup jobs, periodic report aggregation, health/reconciliation
/// jobs. This class only wires up the RECURRING job schedule; the actual job bodies are
/// intentionally left as TODOs for you to implement against the relevant Application
/// services once each module has real data flowing through it.
/// </summary>
public static class HangfireJobs
{
    public static void ConfigureRecurringJobs(
    IRecurringJobManager recurringJobs,
    bool enableIncompleteJobs)
    {
        if (!enableIncompleteJobs)
        {
            foreach (var id in new[]
            {
            "reminders-due-check",
            "campaign-schedule-check",
            "retention-cleanup",
            "report-aggregation",
            "provider-health-reconciliation"
        })
            {
                recurringJobs.RemoveIfExists(id);
            }

            return;
        }

        recurringJobs.AddOrUpdate(
            "reminders-due-check",
            () => ProcessDueReminders(),
            Cron.Minutely);

        recurringJobs.AddOrUpdate(
            "campaign-schedule-check",
            () => ProcessCampaignSchedules(),
            "*/5 * * * *");

        recurringJobs.AddOrUpdate(
            "retention-cleanup",
            () => RunRetentionCleanup(),
            Cron.Daily);

        recurringJobs.AddOrUpdate(
            "report-aggregation",
            () => AggregateDailyReports(),
            "0 1 * * *");

        recurringJobs.AddOrUpdate(
            "provider-health-reconciliation",
            () => ReconcileProviderHealth(),
            "*/2 * * * *");
    }

    public static void ProcessDueReminders() => throw new NotImplementedException("TODO: query followup.Reminders where RemindAt <= now and !IsSent, notify agent, mark IsSent.");
    public static void ProcessCampaignSchedules() => throw new NotImplementedException("TODO: start/pause campaigns based on CallingWindow/Status.");
    public static void RunRetentionCleanup() => throw new NotImplementedException("TODO: purge recordings/messages past TenantSettings retention window.");
    public static void AggregateDailyReports() => throw new NotImplementedException("TODO: aggregate calls/messages into AgentDailyMetric/QueueInterval/ChannelMetric.");
    public static void ReconcileProviderHealth() => throw new NotImplementedException("TODO: ping AsteriskNode AMI/ARI + channel providers, update health/heartbeat fields.");
}
