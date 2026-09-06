using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CCaaS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAppointmentModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "appointment");

            migrationBuilder.CreateTable(
                name: "AppointmentAvailabilitySlot",
                schema: "appointment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppointmentProviderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartsAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndsAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppointmentAvailabilitySlot", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppointmentBooking",
                schema: "appointment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppointmentProviderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AvailabilitySlotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingReference = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    CustomerName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CustomerContact = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ConfirmedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppointmentBooking", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppointmentProvider",
                schema: "appointment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppointmentProvider", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentAvailabilitySlot_TenantId_AppointmentProviderId_StartsAtUtc",
                schema: "appointment",
                table: "AppointmentAvailabilitySlot",
                columns: new[] { "TenantId", "AppointmentProviderId", "StartsAtUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentBooking_TenantId_AvailabilitySlotId",
                schema: "appointment",
                table: "AppointmentBooking",
                columns: new[] { "TenantId", "AvailabilitySlotId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentBooking_TenantId_BookingReference",
                schema: "appointment",
                table: "AppointmentBooking",
                columns: new[] { "TenantId", "BookingReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentProvider_TenantId_Name",
                schema: "appointment",
                table: "AppointmentProvider",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppointmentAvailabilitySlot",
                schema: "appointment");

            migrationBuilder.DropTable(
                name: "AppointmentBooking",
                schema: "appointment");

            migrationBuilder.DropTable(
                name: "AppointmentProvider",
                schema: "appointment");
        }
    }
}
