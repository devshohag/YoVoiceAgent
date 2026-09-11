using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CCaaS.Infrastructure.Migrations
{
    public partial class AddAppointmentConcurrencyAndIdempotency : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Do not silently invent a phone interpretation for unexpected legacy data.
            // The old service wrote ASCII BD national numbers or validated email addresses.
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM [appointment].[AppointmentBooking]
    WHERE CustomerContact IS NULL OR LTRIM(RTRIM(CustomerContact)) = ''
       OR CustomerContact COLLATE Latin1_General_100_BIN2 LIKE '%[^ -~]%')
    THROW 51025, 'Review empty or non-ASCII legacy appointment contacts before applying this migration.', 1;
IF EXISTS (SELECT 1 FROM [appointment].[AppointmentBooking]
    WHERE CustomerContact NOT LIKE '%@%'
      AND NOT (LEN(LTRIM(RTRIM(CustomerContact))) = 11 AND LTRIM(RTRIM(CustomerContact)) LIKE '01[3-9]%'
               AND LTRIM(RTRIM(CustomerContact)) COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9]%')
      AND NOT (LEFT(LTRIM(RTRIM(CustomerContact)), 1) = '+' AND LEN(LTRIM(RTRIM(CustomerContact))) BETWEEN 9 AND 16
               AND SUBSTRING(LTRIM(RTRIM(CustomerContact)), 2, 100) COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9]%'))
    THROW 51025, 'Review legacy appointment phone formats before applying this migration.', 1;
");
            migrationBuilder.AddColumn<int>(name: "Capacity", schema: "appointment",
                table: "AppointmentAvailabilitySlot", type: "int", nullable: false, defaultValue: 1);
            migrationBuilder.AddColumn<int>(name: "BookedCount", schema: "appointment",
                table: "AppointmentAvailabilitySlot", type: "int", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<byte[]>(name: "RowVersion", schema: "appointment",
                table: "AppointmentAvailabilitySlot", type: "rowversion", rowVersion: true, nullable: false);
            migrationBuilder.AddColumn<string>(name: "IdempotencyKey", schema: "appointment",
                table: "AppointmentBooking", type: "nvarchar(64)", maxLength: 64, nullable: true);

            migrationBuilder.Sql(@"
UPDATE [appointment].[AppointmentBooking]
SET CustomerContact = CASE
    WHEN CustomerContact LIKE '%@%' THEN LOWER(LTRIM(RTRIM(CustomerContact)) COLLATE Latin1_General_100_CI_AS)
    WHEN LEFT(LTRIM(RTRIM(CustomerContact)), 1) = '+' THEN LTRIM(RTRIM(CustomerContact))
    ELSE '+880' + SUBSTRING(LTRIM(RTRIM(CustomerContact)), 2, 10) END;

-- Guid:N = 32 lower-case hex digits. UTF8 collation makes varchar bytes match
-- Encoding.UTF8, rather than hashing SQL nvarchar's UTF-16 representation.
UPDATE [appointment].[AppointmentBooking]
SET IdempotencyKey = CONVERT(nvarchar(64), HASHBYTES('SHA2_256',
    CONVERT(varchar(max), (
        LOWER(REPLACE(CONVERT(nvarchar(36), TenantId), '-', '')) + ':' +
        LOWER(REPLACE(CONVERT(nvarchar(36), AvailabilitySlotId), '-', '')) + ':' +
        LOWER(LTRIM(RTRIM(CustomerContact)) COLLATE Latin1_General_100_CI_AS)
    ) COLLATE Latin1_General_100_BIN2_UTF8)), 2);

-- The original unfiltered slot index still reserves slots with historical rows.
UPDATE s SET BookedCount = 1,
    Status = CASE WHEN s.Status = 0 THEN 2 ELSE s.Status END
FROM [appointment].[AppointmentAvailabilitySlot] s
WHERE s.Status = 2 OR EXISTS (
    SELECT 1 FROM [appointment].[AppointmentBooking] b
    WHERE b.TenantId = s.TenantId AND b.AvailabilitySlotId = s.Id);
");
            migrationBuilder.AlterColumn<string>(name: "IdempotencyKey", schema: "appointment",
                table: "AppointmentBooking", type: "nvarchar(64)", maxLength: 64, nullable: false,
                oldClrType: typeof(string), oldType: "nvarchar(64)", oldMaxLength: 64, oldNullable: true);
            migrationBuilder.CreateIndex(name: "IX_AppointmentBooking_TenantId_IdempotencyKey",
                schema: "appointment", table: "AppointmentBooking",
                columns: new[] { "TenantId", "IdempotencyKey" }, unique: true);
            migrationBuilder.AddCheckConstraint(name: "CK_AppointmentSlot_Capacity", schema: "appointment",
                table: "AppointmentAvailabilitySlot", sql: "[Capacity] = 1");
            migrationBuilder.AddCheckConstraint(name: "CK_AppointmentSlot_BookedCount", schema: "appointment",
                table: "AppointmentAvailabilitySlot", sql: "[BookedCount] >= 0 AND [BookedCount] <= [Capacity]");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_AppointmentBooking_TenantId_IdempotencyKey",
                schema: "appointment", table: "AppointmentBooking");
            migrationBuilder.DropColumn(name: "IdempotencyKey", schema: "appointment", table: "AppointmentBooking");
            migrationBuilder.DropCheckConstraint(name: "CK_AppointmentSlot_Capacity", schema: "appointment", table: "AppointmentAvailabilitySlot");
            migrationBuilder.DropCheckConstraint(name: "CK_AppointmentSlot_BookedCount", schema: "appointment", table: "AppointmentAvailabilitySlot");
            migrationBuilder.DropColumn(name: "Capacity", schema: "appointment", table: "AppointmentAvailabilitySlot");
            migrationBuilder.DropColumn(name: "BookedCount", schema: "appointment", table: "AppointmentAvailabilitySlot");
            migrationBuilder.DropColumn(name: "RowVersion", schema: "appointment", table: "AppointmentAvailabilitySlot");
            // Normalized contacts are retained. The previous service accepts +880 numbers.
            // The original tenant/slot unique index was never removed.
        }
    }
}
