using CCaaS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CCaaS.Infrastructure.Migrations;

[DbContext(typeof(CcaasDbContext))]
[Migration("20260907180000_ActiveVoiceBookingUniqueness")]
public sealed partial class ActiveVoiceBookingUniqueness : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_AppointmentBooking_TenantId_AvailabilitySlotId", schema: "appointment", table: "AppointmentBooking");
        migrationBuilder.CreateIndex(name: "IX_AppointmentBooking_TenantId_AvailabilitySlotId", schema: "appointment", table: "AppointmentBooking",
            columns: new[] { "TenantId", "AvailabilitySlotId" }, unique: true, filter: "[Status] = 0 AND [IsDeleted] = 0");
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Rollback intentionally fails if cancelled history now shares a slot; never delete history to force it.
        migrationBuilder.DropIndex(name: "IX_AppointmentBooking_TenantId_AvailabilitySlotId", schema: "appointment", table: "AppointmentBooking");
        migrationBuilder.CreateIndex(name: "IX_AppointmentBooking_TenantId_AvailabilitySlotId", schema: "appointment", table: "AppointmentBooking",
            columns: new[] { "TenantId", "AvailabilitySlotId" }, unique: true);
    }
}
