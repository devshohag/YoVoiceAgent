using CCaaS.Application.Common;
using CCaaS.Domain.Billing;

namespace CCaaS.Application.Billing;

public record CreateSubscriptionRequest(Guid PlanId);
public record RecordUsageRequest(Guid SubscriptionId, string MeterKey, decimal Quantity, DateTime PeriodStart, DateTime PeriodEnd);

public interface IBillingService
{
    Task<Subscription> SubscribeAsync(Guid tenantId, CreateSubscriptionRequest request, CancellationToken ct = default);
    Task RecordUsageAsync(Guid tenantId, RecordUsageRequest request, CancellationToken ct = default);
    Task<Invoice> GenerateInvoiceAsync(Guid tenantId, Guid subscriptionId, CancellationToken ct = default);
}

public class BillingService : IBillingService
{
    private readonly IRepository<Subscription> _subscriptions;
    private readonly IRepository<UsageMeter> _usageMeters;
    private readonly IRepository<Invoice> _invoices;
    private readonly IRepository<Plan> _plans;
    private readonly IUnitOfWork _unitOfWork;

    public BillingService(IRepository<Subscription> subscriptions, IRepository<UsageMeter> usageMeters, IRepository<Invoice> invoices, IRepository<Plan> plans, IUnitOfWork unitOfWork)
    {
        _subscriptions = subscriptions;
        _usageMeters = usageMeters;
        _invoices = invoices;
        _plans = plans;
        _unitOfWork = unitOfWork;
    }

    public async Task<Subscription> SubscribeAsync(Guid tenantId, CreateSubscriptionRequest request, CancellationToken ct = default)
    {
        var subscription = new Subscription { TenantId = tenantId, PlanId = request.PlanId, Status = SubscriptionStatus.Trialing };
        await _subscriptions.AddAsync(subscription, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return subscription;
    }

    public async Task RecordUsageAsync(Guid tenantId, RecordUsageRequest request, CancellationToken ct = default)
    {
        await _usageMeters.AddAsync(new UsageMeter
        {
            TenantId = tenantId,
            SubscriptionId = request.SubscriptionId,
            MeterKey = request.MeterKey,
            Quantity = request.Quantity,
            PeriodStart = request.PeriodStart,
            PeriodEnd = request.PeriodEnd
        }, ct);
        await _unitOfWork.SaveChangesAsync(ct);
    }

    public async Task<Invoice> GenerateInvoiceAsync(Guid tenantId, Guid subscriptionId, CancellationToken ct = default)
    {
        var subscription = await _subscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId && s.TenantId == tenantId, ct)
            ?? throw new KeyNotFoundException("Subscription not found.");
        var plan = await _plans.GetByIdAsync(subscription.PlanId, ct);

        var invoice = new Invoice
        {
            TenantId = tenantId,
            InvoiceNumber = $"INV-{DateTime.UtcNow:yyyyMM}-{Guid.NewGuid().ToString()[..6].ToUpperInvariant()}",
            DueAt = DateTime.UtcNow.AddDays(14),
            Currency = plan?.Currency ?? "BDT",
            TotalAmount = plan?.MonthlyBasePrice ?? 0m
        };
        await _invoices.AddAsync(invoice, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return invoice;

        // TODO: iterate UsageMeters since last invoice and append InvoiceLine rows for
        // overage (recording storage GB-month, AI usage minutes, channel add-ons - Section 15).
    }
}
