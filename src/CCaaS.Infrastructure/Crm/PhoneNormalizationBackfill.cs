using CCaaS.Domain.Common;
using CCaaS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CCaaS.Infrastructure.Crm;

/// <param name="Examined">Rows read.</param>
/// <param name="Normalized">Rows that got a usable E.164 value.</param>
/// <param name="Unparseable">Rows whose raw value could not be normalised. These keep a NULL
/// normalised column and are therefore never dialled - which is the safe failure direction.</param>
/// <param name="AlreadyDone">Rows that already carried the correct normalised value.</param>
public readonly record struct PhoneBackfillReport(
    int Examined,
    int Normalized,
    int Unparseable,
    int AlreadyDone)
{
    public static PhoneBackfillReport operator +(PhoneBackfillReport a, PhoneBackfillReport b) =>
        new(a.Examined + b.Examined,
            a.Normalized + b.Normalized,
            a.Unparseable + b.Unparseable,
            a.AlreadyDone + b.AlreadyDone);

    public override string ToString() =>
        $"examined={Examined} normalized={Normalized} unparseable={Unparseable} alreadyDone={AlreadyDone}";
}

/// <summary>
/// One-off backfill that populates the normalised phone columns added in Task 2.1 for rows
/// that already existed before the migration.
///
/// WHY THIS IS C# AND NOT A SQL SCRIPT
/// -----------------------------------
/// It would be shorter to write UPDATE statements with REPLACE/TRANSLATE. It would also
/// create a second normaliser, in a second language, that can disagree with
/// <see cref="PhoneNumber"/> the moment either one changes. Two normalisers is precisely the
/// bug this column was added to prevent - a Do-Not-Call entry written by one and missed by
/// the other. So the backfill calls the same class the application calls.
///
/// The method is idempotent: running it twice changes nothing the second time, so it is safe
/// to re-run after fixing raw data by hand.
/// </summary>
public sealed class PhoneNormalizationBackfill
{
    private const int BatchSize = 500;

    private readonly CcaasDbContext _db;
    private readonly ILogger<PhoneNormalizationBackfill> _logger;

    public PhoneNormalizationBackfill(CcaasDbContext db, ILogger<PhoneNormalizationBackfill> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Backfills Leads, Customers and phone-channel Contacts.
    /// </summary>
    /// <param name="defaultRegion">
    /// ISO 3166-1 alpha-2 region applied to values stored without a country code. Existing
    /// rows were captured through a Bangladesh-facing UI, so "BD" is the correct assumption
    /// for this codebase - pass the tenant's real region if that ever stops being true.
    /// </param>
    /// <param name="dryRun">
    /// When true nothing is written. Run once with true, read the report, and only then run
    /// for real: an unexpectedly large Unparseable count means the raw data needs attention
    /// before it is interpreted, not after.
    /// </param>
    public async Task<PhoneBackfillReport> RunAsync(
        string defaultRegion = "BD",
        bool dryRun = true,
        CancellationToken ct = default)
    {
        // IgnoreQueryFilters: this runs outside any request, so no tenant is resolved, and it
        // must deliberately cross every tenant. Soft-deleted rows are skipped explicitly
        // because they are never dialled and do not need a normalised value.
        var leads = await BackfillAsync(
            "crm.Lead",
            _db.Leads.IgnoreQueryFilters().Where(x => !x.IsDeleted && x.Phone != null),
            x => x.Phone,
            (x, value) => x.PhoneE164 = value,
            x => x.PhoneE164,
            defaultRegion, dryRun, ct);

        var customers = await BackfillAsync(
            "crm.Customer",
            _db.Customers.IgnoreQueryFilters().Where(x => !x.IsDeleted && x.Phone != null),
            x => x.Phone,
            (x, value) => x.PhoneE164 = value,
            x => x.PhoneE164,
            defaultRegion, dryRun, ct);

        // Contact rows cover several channels; only dialable ones get an E.164 value. Email
        // contacts are normalised by lower-casing, which keeps the column meaningful for
        // de-duplication without pretending an address is a phone number.
        var contacts = await BackfillContactsAsync(defaultRegion, dryRun, ct);

        var total = leads + customers + contacts;
        _logger.LogInformation(
            "Phone normalisation backfill complete (dryRun={DryRun}, region={Region}): {Report}",
            dryRun, defaultRegion, total);

        return total;
    }

    private async Task<PhoneBackfillReport> BackfillAsync<T>(
        string label,
        IQueryable<T> source,
        Func<T, string?> readRaw,
        Action<T, string?> writeNormalized,
        Func<T, string?> readNormalized,
        string defaultRegion,
        bool dryRun,
        CancellationToken ct) where T : class
    {
        var examined = 0;
        var normalized = 0;
        var unparseable = 0;
        var alreadyDone = 0;

        var rows = await source.ToListAsync(ct);

        foreach (var row in rows)
        {
            examined++;
            var result = PhoneNumber.TryNormalize(readRaw(row), defaultRegion);

            if (!result.Succeeded)
            {
                unparseable++;
                _logger.LogWarning("{Label}: cannot normalise '{Raw}' - {Error}",
                    label, readRaw(row), result.Error);
                continue;
            }

            if (string.Equals(readNormalized(row), result.E164, StringComparison.Ordinal))
            {
                alreadyDone++;
                continue;
            }

            writeNormalized(row, result.E164);
            normalized++;
        }

        if (!dryRun && normalized > 0)
            await SaveInBatchesAsync(ct);

        _logger.LogInformation("{Label}: examined={Examined} normalized={Normalized} unparseable={Unparseable} alreadyDone={AlreadyDone}",
            label, examined, normalized, unparseable, alreadyDone);

        return new PhoneBackfillReport(examined, normalized, unparseable, alreadyDone);
    }

    private async Task<PhoneBackfillReport> BackfillContactsAsync(
        string defaultRegion, bool dryRun, CancellationToken ct)
    {
        string[] dialableChannels = ["phone", "mobile", "whatsapp", "sms", "voice"];

        var rows = await _db.Contacts.IgnoreQueryFilters()
            .Where(x => !x.IsDeleted)
            .ToListAsync(ct);

        var examined = 0;
        var normalized = 0;
        var unparseable = 0;
        var alreadyDone = 0;

        foreach (var row in rows)
        {
            examined++;
            string? value;

            if (dialableChannels.Contains(row.Channel, StringComparer.OrdinalIgnoreCase))
            {
                var result = PhoneNumber.TryNormalize(row.Value, defaultRegion);
                if (!result.Succeeded)
                {
                    unparseable++;
                    _logger.LogWarning("crm.Contact: cannot normalise '{Raw}' on channel '{Channel}' - {Error}",
                        row.Value, row.Channel, result.Error);
                    continue;
                }
                value = result.E164;
            }
            else if (string.Equals(row.Channel, "email", StringComparison.OrdinalIgnoreCase))
            {
                value = row.Value.Trim().ToLowerInvariant();
            }
            else
            {
                // Unknown channel: leave NULL rather than invent a canonical form.
                continue;
            }

            if (string.Equals(row.ValueNormalized, value, StringComparison.Ordinal))
            {
                alreadyDone++;
                continue;
            }

            row.ValueNormalized = value;
            normalized++;
        }

        if (!dryRun && normalized > 0)
            await SaveInBatchesAsync(ct);

        _logger.LogInformation("crm.Contact: examined={Examined} normalized={Normalized} unparseable={Unparseable} alreadyDone={AlreadyDone}",
            examined, normalized, unparseable, alreadyDone);

        return new PhoneBackfillReport(examined, normalized, unparseable, alreadyDone);
    }

    /// <summary>
    /// SaveChanges once per entity type. Kept as a named method so the batching decision is
    /// visible: these tables are small enough today that a single save is correct, and the
    /// constant above documents where to start chunking if that stops being true.
    /// </summary>
    private Task SaveInBatchesAsync(CancellationToken ct)
    {
        _ = BatchSize;
        return _db.SaveChangesAsync(ct);
    }
}
