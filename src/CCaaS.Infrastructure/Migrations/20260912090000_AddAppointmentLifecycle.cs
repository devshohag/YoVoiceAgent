using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CCaaS.Infrastructure.Migrations
{
    public partial class AddAppointmentLifecycle : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(name: "CancelledAtUtc", schema: "appointment",
                table: "AppointmentBooking", type: "datetime2", nullable: true);
            migrationBuilder.AddColumn<string>(name: "CancellationReason", schema: "appointment",
                table: "AppointmentBooking", type: "nvarchar(500)", maxLength: 500, nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "RescheduledFromBookingId", schema: "appointment",
                table: "AppointmentBooking", type: "uniqueidentifier", nullable: true);
            migrationBuilder.CreateIndex(name: "IX_AppointmentBooking_TenantId_RescheduledFromBookingId",
                schema: "appointment", table: "AppointmentBooking",
                columns: new[] { "TenantId", "RescheduledFromBookingId" }, unique: true,
                filter: "[RescheduledFromBookingId] IS NOT NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_AppointmentBooking_TenantId_RescheduledFromBookingId",
                schema: "appointment", table: "AppointmentBooking");
            migrationBuilder.DropColumn(name: "RescheduledFromBookingId", schema: "appointment", table: "AppointmentBooking");
            migrationBuilder.DropColumn(name: "CancellationReason", schema: "appointment", table: "AppointmentBooking");
            migrationBuilder.DropColumn(name: "CancelledAtUtc", schema: "appointment", table: "AppointmentBooking");
        }
    }
}
