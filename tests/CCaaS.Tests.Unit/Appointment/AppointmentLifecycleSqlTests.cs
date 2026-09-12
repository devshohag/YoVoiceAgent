using System.Data.Common;
using CCaaS.Application.Appointment;
using CCaaS.Domain.Appointment;
using CCaaS.Infrastructure.Appointment;
using CCaaS.Infrastructure.Migrations;
using CCaaS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CCaaS.Tests.Unit.Appointment;

public sealed class AppointmentLifecycleSqlTests : IClassFixture<AppointmentSqlFixture>
{
    private readonly AppointmentSqlFixture _fixture;
    public AppointmentLifecycleSqlTests(AppointmentSqlFixture fixture) => _fixture = fixture;

    [AppointmentSqlFact]
    public async Task Cancel_is_retry_safe_and_retains_the_historical_slot_index()
    {
        var seed = await Seed();
        await using var db = _fixture.Open();
        var service = new AppointmentService(db);
        Assert.Equal(AppointmentChangeOutcome.Cancelled,
            (await service.CancelAsync(seed.Tenant, seed.Booking, "Caller requested cancellation")).Outcome);
        Assert.Equal(AppointmentChangeOutcome.AlreadyCancelled,
            (await service.CancelAsync(seed.Tenant, seed.Booking, "Retry")).Outcome);
        var booking = await db.AppointmentBookings.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(b => b.TenantId == seed.Tenant && b.Id == seed.Booking);
        Assert.Equal(AppointmentBookingStatus.Cancelled, booking.Status);
        Assert.Equal("Caller requested cancellation", booking.CancellationReason);
        Assert.NotNull(booking.CancelledAtUtc);
        var slot = await db.AppointmentAvailabilitySlots.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(s => s.TenantId == seed.Tenant && s.Id == seed.OldSlot);
        Assert.Equal(AppointmentSlotStatus.Blocked, slot.Status);
        Assert.Equal(0, slot.BookedCount);
        Assert.Equal(BookingOutcome.InvalidDetails, (await service.TryBookAsync(seed.Tenant,
            new(seed.OldSlot, "Rahim", "+8801712345678", null))).Outcome);
    }

    [AppointmentSqlFact]
    public async Task Reschedule_is_atomic_and_retries_return_the_same_replacement()
    {
        var seed = await Seed();
        await using var db = _fixture.Open();
        var service = new AppointmentService(db);
        var first = await service.RescheduleAsync(seed.Tenant, seed.Booking, seed.NewSlots[0]);
        var retry = await service.RescheduleAsync(seed.Tenant, seed.Booking, seed.NewSlots[0]);
        Assert.Equal(AppointmentChangeOutcome.Rescheduled, first.Outcome);
        Assert.Equal(AppointmentChangeOutcome.AlreadyRescheduled, retry.Outcome);
        Assert.Equal(first.BookingId, retry.BookingId);
        Assert.Equal(first.BookingReference, retry.BookingReference);
        var rows = await db.AppointmentBookings.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.TenantId == seed.Tenant).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows.Where(b => b.Status == AppointmentBookingStatus.Confirmed));
        Assert.Equal(seed.Booking, rows.Single(b => b.Id == first.BookingId).RescheduledFromBookingId);
        Assert.Equal(AppointmentBookingStatus.Cancelled, rows.Single(b => b.Id == seed.Booking).Status);
    }

    [AppointmentSqlFact]
    public async Task Taken_target_leaves_original_confirmed()
    {
        var seed = await Seed();
        await using var db = _fixture.Open();
        var service = new AppointmentService(db);
        await service.BookAsync(seed.Tenant, new(seed.NewSlots[0], "Other", "+14155552671", null));
        Assert.Equal(AppointmentChangeOutcome.SlotTaken,
            (await service.RescheduleAsync(seed.Tenant, seed.Booking, seed.NewSlots[0])).Outcome);
        Assert.Equal(AppointmentBookingStatus.Confirmed, await OriginalStatus(seed));
    }

    [AppointmentSqlFact]
    public async Task Wrong_tenant_cannot_find_cancel_or_reschedule()
    {
        var seed = await Seed();
        await using var db = _fixture.Open();
        var service = new AppointmentService(db);
        var other = Guid.NewGuid();
        Assert.Empty(await service.FindUpcomingAsync(other, "+8801712345678", DateTime.UtcNow));
        Assert.Equal(AppointmentChangeOutcome.NotFound, (await service.CancelAsync(other, seed.Booking, "test")).Outcome);
        Assert.Equal(AppointmentChangeOutcome.NotFound, (await service.RescheduleAsync(other, seed.Booking, seed.NewSlots[0])).Outcome);
        Assert.Equal(AppointmentBookingStatus.Confirmed, await OriginalStatus(seed));
    }

    [AppointmentSqlFact]
    public async Task Upcoming_lookup_normalizes_contact_and_excludes_cancelled()
    {
        var seed = await Seed();
        await using var db = _fixture.Open();
        var service = new AppointmentService(db);
        Assert.Single(await service.FindUpcomingAsync(seed.Tenant, "01712345678", DateTime.UtcNow));
        await service.CancelAsync(seed.Tenant, seed.Booking, "test");
        Assert.Empty(await service.FindUpcomingAsync(seed.Tenant, "+8801712345678", DateTime.UtcNow));
    }

    [AppointmentSqlFact]
    public async Task Concurrent_different_targets_cannot_create_two_replacements()
    {
        var seed = await Seed();
        var gate = new OldSlotReadGate();
        var results = await Task.WhenAll(seed.NewSlots.Select(async id =>
        {
            await using var db = _fixture.Open(gate);
            return await new AppointmentService(db).RescheduleAsync(seed.Tenant, seed.Booking, id);
        }));
        Assert.Single(results.Where(r => r.Outcome == AppointmentChangeOutcome.Rescheduled));
        Assert.Single(results.Where(r => r.Outcome == AppointmentChangeOutcome.Conflict));
        await using var verify = _fixture.Open();
        Assert.Equal(1, await verify.AppointmentBookings.IgnoreQueryFilters().CountAsync(b =>
            b.TenantId == seed.Tenant && b.Status == AppointmentBookingStatus.Confirmed));
        Assert.Equal(1, await verify.AppointmentBookings.IgnoreQueryFilters().CountAsync(b =>
            b.TenantId == seed.Tenant && b.RescheduledFromBookingId == seed.Booking));
    }

    [AppointmentSqlFact]
    public async Task Concurrent_identical_targets_return_one_reference()
    {
        var seed = await Seed();
        var gate = new OldSlotReadGate();
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var db = _fixture.Open(gate);
            return await new AppointmentService(db).RescheduleAsync(seed.Tenant, seed.Booking, seed.NewSlots[0]);
        }));
        Assert.Single(results.Where(r => r.Outcome == AppointmentChangeOutcome.Rescheduled));
        Assert.Single(results.Where(r => r.Outcome == AppointmentChangeOutcome.AlreadyRescheduled));
        Assert.Single(results.Select(r => r.BookingReference).Distinct());
    }

    [AppointmentSqlFact]
    public async Task Database_rejection_rolls_back_old_cancellation_and_new_booking()
    {
        var seed = await Seed();
        await using var db = _fixture.Open();
        // A real constraint failure during SaveChanges, after validation, exercises rollback.
        // Unique name and tenant predicate isolate this fault from concurrent test tenants.
        var constraint = "CK_Task26_Reject_" + seed.Tenant.ToString("N");
        var addConstraintSql = $"ALTER TABLE [appointment].[AppointmentBooking] ADD CONSTRAINT [{constraint}] CHECK ([RescheduledFromBookingId] IS NULL OR [TenantId] <> '{seed.Tenant:D}')";
        var dropConstraintSql = $"ALTER TABLE [appointment].[AppointmentBooking] DROP CONSTRAINT [{constraint}]";
        await db.Database.ExecuteSqlRawAsync(addConstraintSql);
        try
        {
            Assert.Equal(AppointmentChangeOutcome.SystemError,
                (await new AppointmentService(db).RescheduleAsync(seed.Tenant, seed.Booking, seed.NewSlots[0])).Outcome);
            Assert.Equal(AppointmentBookingStatus.Confirmed, await OriginalStatus(seed));
            Assert.Equal(1, await db.AppointmentBookings.IgnoreQueryFilters().AsNoTracking().CountAsync(b => b.TenantId == seed.Tenant));
            Assert.Equal(0, await db.AppointmentAvailabilitySlots.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.TenantId == seed.Tenant && s.Id == seed.NewSlots[0]).Select(s => s.BookedCount).SingleAsync());
        }
        finally { await db.Database.ExecuteSqlRawAsync(dropConstraintSql); }
    }

    [AppointmentSqlFact]
    public async Task Lifecycle_migration_applies_and_reverts_with_existing_booking_rows()
    {
        var connection = AppointmentSqlFixture.IsolatedConnection(_fixture.ConnectionString);
        await using var baseline = new BeforeLifecycleContext(AppointmentSqlFixture.Options(connection));
        await baseline.Database.EnsureCreatedAsync();
        var tenant = Guid.NewGuid();
        var provider = new AppointmentProvider { TenantId = tenant, Name = "Legacy" };
        var slot = new AppointmentAvailabilitySlot { TenantId = tenant, AppointmentProviderId = provider.Id,
            StartsAtUtc = DateTime.UtcNow.AddDays(7), EndsAtUtc = DateTime.UtcNow.AddDays(7).AddHours(1),
            BookedCount = 1, Status = AppointmentSlotStatus.Booked };
        var booking = new AppointmentBooking { TenantId = tenant, AppointmentProviderId = provider.Id,
            AvailabilitySlotId = slot.Id, BookingReference = "LEGACY-26", CustomerName = "Rahim",
            CustomerContact = "+8801712345678", IdempotencyKey = BookingIdempotency.Derive(tenant, slot.Id, "+8801712345678") };
        baseline.AddRange(provider, slot, booking);
        await baseline.SaveChangesAsync();
        await using var current = new CcaasDbContext(AppointmentSqlFixture.Options(connection));
        var migration = new AddAppointmentLifecycle();
        var generator = current.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.UpOperations))
            await current.Database.ExecuteSqlRawAsync(command.CommandText);
        var row = await current.AppointmentBookings.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(b => b.TenantId == tenant && b.Id == booking.Id);
        Assert.Null(row.CancelledAtUtc);
        Assert.Null(row.CancellationReason);
        Assert.Null(row.RescheduledFromBookingId);
        foreach (var command in generator.Generate(migration.DownOperations))
            await current.Database.ExecuteSqlRawAsync(command.CommandText);
        Assert.Equal(1, await current.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [appointment].[AppointmentBooking] WHERE [TenantId] = {tenant}").SingleAsync());
    }

    private async Task<SeedData> Seed()
    {
        await using var db = _fixture.Open();
        var tenant = Guid.NewGuid();
        var provider = new AppointmentProvider { TenantId = tenant, Name = "Provider " + tenant };
        var slots = Enumerable.Range(0, 3).Select(i => new AppointmentAvailabilitySlot
        {
            TenantId = tenant, AppointmentProviderId = provider.Id,
            StartsAtUtc = DateTime.UtcNow.Date.AddDays(7).AddHours(10 + i),
            EndsAtUtc = DateTime.UtcNow.Date.AddDays(7).AddHours(11 + i)
        }).ToArray();
        db.Add(provider);
        db.AddRange(slots);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var booked = await new AppointmentService(db).BookAsync(tenant, new(slots[0].Id, "Rahim", "+8801712345678", null));
        return new(tenant, booked.BookingId, slots[0].Id, slots.Skip(1).Select(s => s.Id).ToArray());
    }

    private async Task<AppointmentBookingStatus> OriginalStatus(SeedData seed)
    {
        await using var db = _fixture.Open();
        return await db.AppointmentBookings.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.TenantId == seed.Tenant && b.Id == seed.Booking).Select(b => b.Status).SingleAsync();
    }

    private sealed record SeedData(Guid Tenant, Guid Booking, Guid OldSlot, Guid[] NewSlots);

    private sealed class BeforeLifecycleContext : CcaasDbContext
    {
        public BeforeLifecycleContext(DbContextOptions<CcaasDbContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<AppointmentBooking>().Ignore(b => b.CancelledAtUtc)
                .Ignore(b => b.CancellationReason).Ignore(b => b.RescheduledFromBookingId);
        }
    }

    private sealed class OldSlotReadGate : DbCommandInterceptor
    {
        private int _reads;
        private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM [appointment].[AppointmentAvailabilitySlot]", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _reads) >= 2) _ready.TrySetResult(true);
                await _ready.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return result;
        }
    }
}
