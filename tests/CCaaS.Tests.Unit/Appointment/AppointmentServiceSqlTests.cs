using System.Data.Common;
using CCaaS.Application.Appointment;
using CCaaS.Domain.Appointment;
using CCaaS.Domain.Tenant;
using CCaaS.Infrastructure.Appointment;
using CCaaS.Infrastructure.Migrations;
using CCaaS.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CCaaS.Tests.Unit.Appointment;

public sealed class AppointmentServiceSqlTests : IClassFixture<AppointmentSqlFixture>
{
    private readonly AppointmentSqlFixture _fixture;
    public AppointmentServiceSqlTests(AppointmentSqlFixture fixture) => _fixture = fixture;

    [AppointmentSqlFact]
    public async Task Retry_returns_same_reference_without_a_second_write()
    {
        var seed = await Seed();
        await using var db = _fixture.Open();
        var service = new AppointmentService(db);
        var first = await service.TryBookAsync(seed.Tenant, Command(seed.First));
        var again = await service.TryBookAsync(seed.Tenant, Command(seed.First));
        Assert.Equal(BookingOutcome.Booked, first.Outcome);
        Assert.Equal(BookingOutcome.AlreadyYours, again.Outcome);
        Assert.Equal(first.BookingReference, again.BookingReference);
        var legacy = await service.BookAsync(seed.Tenant, Command(seed.First));
        Assert.True(legacy.WasExisting);
        Assert.Equal(first.BookingId, legacy.BookingId);
        Assert.Equal(1, await db.AppointmentBookings.IgnoreQueryFilters().CountAsync(x => x.TenantId == seed.Tenant));
        var slot = await ReadSlot(db, seed.First);
        Assert.Equal(1, slot.BookedCount);
        Assert.Equal(8, slot.RowVersion.Length);
    }

    [AppointmentSqlFact]
    public async Task Parallel_different_contacts_have_exactly_one_winner()
    {
        var seed = await Seed();
        var results = await Race(seed, Enumerable.Range(0, 3).Select(i => "+1415555267" + i).ToArray());
        Assert.Single(results.Where(x => x.Outcome == BookingOutcome.Booked));
        Assert.Equal(2, results.Count(x => x.Outcome == BookingOutcome.SlotTaken));
        await AssertSingleBooking(seed);
    }

    [AppointmentSqlFact]
    public async Task Five_parallel_retries_return_one_reference()
    {
        var seed = await Seed();
        var results = await Race(seed, Enumerable.Repeat("+14155552671", 5).ToArray());
        Assert.Single(results.Where(x => x.Outcome == BookingOutcome.Booked));
        Assert.All(results, x => Assert.Contains(x.Outcome, new[] { BookingOutcome.Booked, BookingOutcome.AlreadyYours }));
        Assert.Single(results.Select(x => x.BookingReference).Distinct());
        await AssertSingleBooking(seed);
    }

    [AppointmentSqlFact]
    public async Task Availability_obeys_utc_half_open_range_and_fullness()
    {
        var seed = await Seed();
        await using var db = _fixture.Open();
        var service = new AppointmentService(db);
        var slot = await ReadSlot(db, seed.First);
        var from = DateTime.SpecifyKind(slot.StartsAtUtc, DateTimeKind.Utc);
        var one = await service.GetAvailabilityAsync(seed.Tenant, from, from.AddHours(1));
        Assert.Equal(seed.First, Assert.Single(one).SlotId);
        await service.TryBookAsync(seed.Tenant, Command(seed.First));
        Assert.Empty(await service.GetAvailabilityAsync(seed.Tenant, from, from.AddHours(1)));
        var day = await service.GetAvailabilityAsync(seed.Tenant, DateOnly.FromDateTime(from));
        Assert.Equal(seed.Second, Assert.Single(day).SlotId);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetAvailabilityAsync(seed.Tenant,
            DateTime.SpecifyKind(from, DateTimeKind.Unspecified), from.AddHours(1)));
    }

    [AppointmentSqlFact]
    public async Task Foreign_tenant_cannot_see_or_book_slots()
    {
        var seed = await Seed();
        await using var db = _fixture.Open();
        var service = new AppointmentService(db);
        var other = Guid.NewGuid();
        var result = await service.TryBookAsync(other, Command(seed.First));
        Assert.Equal(BookingOutcome.SlotTaken, result.Outcome);
        Assert.Empty(await service.GetBookingsAsync(other));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.BookAsync(other, Command(seed.First)));
    }

    [AppointmentSqlFact]
    public async Task Tenant_region_is_configurable_and_new_contacts_are_e164()
    {
        var seed = await Seed();
        await using var db = _fixture.Open();
        db.SystemSettings.Add(new SystemSetting { TenantId = seed.Tenant, Category = "Appointment",
            Key = "DefaultPhoneRegion", Value = "US" });
        await db.SaveChangesAsync();
        var result = await new AppointmentService(db).TryBookAsync(seed.Tenant, Command(seed.First, "4155552671"));
        Assert.Equal(BookingOutcome.Booked, result.Outcome);
        var row = await db.AppointmentBookings.IgnoreQueryFilters().SingleAsync(x => x.Id == result.BookingId);
        Assert.Equal("+14155552671", row.CustomerContact);
        Assert.Equal(BookingIdempotency.Derive(seed.Tenant, seed.First, row.CustomerContact), row.IdempotencyKey);
    }

    [AppointmentSqlFact]
    public async Task Invalid_details_and_provider_unavailability_are_distinct()
    {
        var seed = await Seed();
        await using var db = _fixture.Open();
        var service = new AppointmentService(db);
        Assert.Equal(BookingOutcome.InvalidDetails, (await service.TryBookAsync(seed.Tenant, Command(seed.First, "junk"))).Outcome);
        await Assert.ThrowsAsync<ArgumentException>(() => service.BookAsync(seed.Tenant, Command(seed.First, "junk")));
        var provider = await db.AppointmentProviders.IgnoreQueryFilters().SingleAsync(x => x.TenantId == seed.Tenant);
        provider.IsActive = false;
        await db.SaveChangesAsync();
        Assert.Equal(BookingOutcome.ProviderUnavailable, (await service.TryBookAsync(seed.Tenant, Command(seed.First))).Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.BookAsync(seed.Tenant, Command(seed.First)));
    }

    [AppointmentSqlFact]
    public async Task Failed_insert_rolls_back_slot_update_and_service_can_be_reused()
    {
        var connection = AppointmentSqlFixture.IsolatedConnection(_fixture.ConnectionString);
        await using var db = new TriggerContext(AppointmentSqlFixture.Options(connection));
        await db.Database.EnsureCreatedAsync();
        var seed = await Seed(db);
        await db.Database.ExecuteSqlRawAsync(@"CREATE TRIGGER [appointment].[RejectScriptedInsert]
ON [appointment].[AppointmentBooking] AFTER INSERT AS
BEGIN
    IF EXISTS (SELECT 1 FROM inserted WHERE Purpose = 'reject-test')
        THROW 51026, 'Scripted SQL failure', 1;
END");
        var service = new AppointmentService(db);
        var failed = await service.TryBookAsync(seed.Tenant, Command(seed.First) with { Purpose = "reject-test" });
        Assert.Equal(BookingOutcome.SystemError, failed.Outcome);
        Assert.DoesNotContain("Scripted", failed.Error!);
        Assert.Equal(0, (await ReadSlot(db, seed.First)).BookedCount);
        Assert.Empty(await service.GetBookingsAsync(seed.Tenant));
        Assert.Equal(BookingOutcome.Booked, (await service.TryBookAsync(seed.Tenant, Command(seed.First))).Outcome);
    }

    [AppointmentSqlFact]
    public async Task Database_rejects_capacity_above_one_and_stale_slot_writes()
    {
        var seed = await Seed();
        await using var one = _fixture.Open();
        await using var two = _fixture.Open();
        var a = await one.AppointmentAvailabilitySlots.IgnoreQueryFilters().SingleAsync(x => x.Id == seed.First);
        a.Capacity = 2;
        await Assert.ThrowsAsync<DbUpdateException>(() => one.SaveChangesAsync());
        one.ChangeTracker.Clear();
        a = await one.AppointmentAvailabilitySlots.IgnoreQueryFilters().SingleAsync(x => x.Id == seed.First);
        var b = await two.AppointmentAvailabilitySlots.IgnoreQueryFilters().SingleAsync(x => x.Id == seed.First);
        a.Status = AppointmentSlotStatus.Blocked;
        await one.SaveChangesAsync();
        b.Status = AppointmentSlotStatus.Reserved;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => two.SaveChangesAsync());
    }

    [AppointmentSqlFact]
    public async Task Migration_backfill_matches_csharp_and_down_preserves_rows()
    {
        var connection = AppointmentSqlFixture.IsolatedConnection(_fixture.ConnectionString);
        await using var baseline = new BaselineContext(AppointmentSqlFixture.Options(connection));
        await baseline.Database.EnsureCreatedAsync();
        var seed = await Seed(baseline);
        var provider = await baseline.AppointmentProviders.IgnoreQueryFilters().SingleAsync(x => x.TenantId == seed.Tenant);
        foreach (var tuple in new[] { (seed.First, "01712345678"), (seed.Second, "USER@EXAMPLE.COM") })
            baseline.AppointmentBookings.Add(new AppointmentBooking { TenantId = seed.Tenant,
                AppointmentProviderId = provider.Id, AvailabilitySlotId = tuple.Item1,
                BookingReference = Guid.NewGuid().ToString("N"), CustomerName = "Legacy", CustomerContact = tuple.Item2 });
        await baseline.SaveChangesAsync();
        await using var migrated = new CcaasDbContext(AppointmentSqlFixture.Options(connection));
        var migration = new AddAppointmentConcurrencyAndIdempotency();
        var generator = migrated.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.UpOperations))
            await migrated.Database.ExecuteSqlRawAsync(command.CommandText);
        var rows = await migrated.AppointmentBookings.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == seed.Tenant).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, x => x.CustomerContact == "+8801712345678");
        Assert.Contains(rows, x => x.CustomerContact == "user@example.com");
        foreach (var row in rows)
            Assert.Equal(BookingIdempotency.Derive(row.TenantId, row.AvailabilitySlotId, row.CustomerContact), row.IdempotencyKey);
        Assert.Equal(1, (await ReadSlot(migrated, seed.First)).BookedCount);
        Assert.Equal(8, (await ReadSlot(migrated, seed.First)).RowVersion.Length);
        Assert.Equal(BookingOutcome.AlreadyYours, (await new AppointmentService(migrated).TryBookAsync(seed.Tenant, Command(seed.First))).Outcome);
        foreach (var command in generator.Generate(migration.DownOperations))
            await migrated.Database.ExecuteSqlRawAsync(command.CommandText);
        // Verify persisted rows directly after the schema rollback.
        var remainingBookings = await migrated.Database
            .SqlQuery<int>($"""
                    SELECT COUNT(*) AS [Value]
                    FROM [appointment].[AppointmentBooking]
                    WHERE [TenantId] = {seed.Tenant}
                    """
            )
            .SingleAsync();

        Assert.Equal(2, remainingBookings);
    }

    private async Task<BookingAttempt[]> Race(SeedData seed, string[] contacts)
    {
        var gate = new SlotReadGate(contacts.Length);
        return await Task.WhenAll(contacts.Select(async contact =>
        {
            await using var db = _fixture.Open(gate);
            return await new AppointmentService(db).TryBookAsync(seed.Tenant, Command(seed.First, contact));
        }));
    }

    private async Task AssertSingleBooking(SeedData seed)
    {
        await using var db = _fixture.Open();
        Assert.Equal(1, await db.AppointmentBookings.IgnoreQueryFilters().CountAsync(x => x.TenantId == seed.Tenant));
        Assert.Equal(1, (await ReadSlot(db, seed.First)).BookedCount);
    }

    private async Task<SeedData> Seed()
    {
        await using var db = _fixture.Open();
        return await Seed(db);
    }

    private static async Task<SeedData> Seed(CcaasDbContext db)
    {
        var tenant = Guid.NewGuid();
        var provider = new AppointmentProvider { TenantId = tenant, Name = "Provider " + tenant };
        var start = DateTime.UtcNow.Date.AddDays(7).AddHours(10);
        var first = new AppointmentAvailabilitySlot { TenantId = tenant, AppointmentProviderId = provider.Id,
            StartsAtUtc = start, EndsAtUtc = start.AddMinutes(30) };
        var second = new AppointmentAvailabilitySlot { TenantId = tenant, AppointmentProviderId = provider.Id,
            StartsAtUtc = start.AddHours(1), EndsAtUtc = start.AddMinutes(90) };
        db.AddRange(provider, first, second);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new(tenant, first.Id, second.Id);
    }

    private sealed record SeedData(Guid Tenant, Guid First, Guid Second);
    private static BookAppointmentCommand Command(Guid slot, string contact = "+8801712345678") => new(slot, "Rahim", contact, "test");
    private static Task<AppointmentAvailabilitySlot> ReadSlot(CcaasDbContext db, Guid id) =>
        db.AppointmentAvailabilitySlots.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id);

    private sealed class SlotReadGate : DbCommandInterceptor
    {
        private readonly int _participants;
        private int _reads;
        private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SlotReadGate(int participants) => _participants = participants;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM [appointment].[AppointmentAvailabilitySlot]", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _reads) >= _participants) _ready.TrySetResult(true);
                await _ready.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return result;
        }
    }

    private sealed class TriggerContext : CcaasDbContext
    {
        public TriggerContext(DbContextOptions<CcaasDbContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<AppointmentBooking>().ToTable("AppointmentBooking", "appointment", table => table.UseSqlOutputClause(false));
        }
    }

    private sealed class BaselineContext : CcaasDbContext
    {
        public BaselineContext(DbContextOptions<CcaasDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            var slot = builder.Entity<AppointmentAvailabilitySlot>();

            slot.Metadata.RemoveCheckConstraint("CK_AppointmentSlot_Capacity");
            slot.Metadata.RemoveCheckConstraint("CK_AppointmentSlot_BookedCount");

            slot.Ignore(x => x.Capacity)
                .Ignore(x => x.BookedCount)
                .Ignore(x => x.RowVersion);

            builder.Entity<AppointmentBooking>()
                .Ignore(x => x.IdempotencyKey);
        }
    }
}
