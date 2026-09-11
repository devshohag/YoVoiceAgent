using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CCaaS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboundComplianceAndCallTiming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "compliance");

            migrationBuilder.AddColumn<string>(
                name: "PhoneE164",
                schema: "crm",
                table: "Lead",
                type: "nvarchar(450)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhoneE164",
                schema: "crm",
                table: "Customer",
                type: "nvarchar(450)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                schema: "crm",
                table: "Customer",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ValueNormalized",
                schema: "crm",
                table: "Contact",
                type: "nvarchar(450)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FinalDisposition",
                schema: "campaign",
                table: "CampaignLead",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LastCallSessionId",
                schema: "campaign",
                table: "CampaignLead",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "State",
                schema: "campaign",
                table: "CampaignLead",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "SuppressedAtUtc",
                schema: "campaign",
                table: "CampaignLead",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AmdResult",
                schema: "calls",
                table: "CallSession",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "CampaignLeadId",
                schema: "calls",
                table: "CallSession",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DispositionSetBy",
                schema: "calls",
                table: "CallSession",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                schema: "calls",
                table: "CallSession",
                type: "nvarchar(450)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecordingConsentAnnouncedAt",
                schema: "calls",
                table: "CallSession",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TransferredToAgentId",
                schema: "calls",
                table: "CallSession",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CallStageTiming",
                schema: "calls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CallSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TurnId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TurnNo = table.Column<int>(type: "int", nullable: true),
                    Stage = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DurationMs = table.Column<double>(type: "float", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Concurrency = table.Column<int>(type: "int", nullable: true),
                    AudioDurationMs = table.Column<int>(type: "int", nullable: true),
                    VadAudioDurationMs = table.Column<int>(type: "int", nullable: true),
                    Provider = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Model = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequestId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    PipelineVersion = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CallStageTiming", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CallStageTiming_CallSession_CallSessionId",
                        column: x => x.CallSessionId,
                        principalSchema: "calls",
                        principalTable: "CallSession",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContactConsent",
                schema: "compliance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeadId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PhoneE164 = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    ConsentType = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EvidenceUri = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceCallSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GrantedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactConsent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DoNotCallEntry",
                schema: "compliance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PhoneE164 = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    AddedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AddedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceCallSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DoNotCallEntry", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SuppressionCheck",
                schema: "compliance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PhoneE164 = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeadId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CampaignLeadId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CheckedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Result = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<int>(type: "int", nullable: false),
                    ConsentIdUsed = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RuleSetVersion = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DetailJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SuppressionCheck", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Lead_TenantId_PhoneE164",
                schema: "crm",
                table: "Lead",
                columns: new[] { "TenantId", "PhoneE164" },
                filter: "[PhoneE164] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Customer_TenantId_PhoneE164",
                schema: "crm",
                table: "Customer",
                columns: new[] { "TenantId", "PhoneE164" },
                filter: "[PhoneE164] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Contact_TenantId_ValueNormalized",
                schema: "crm",
                table: "Contact",
                columns: new[] { "TenantId", "ValueNormalized" },
                filter: "[ValueNormalized] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignLead_TenantId_CampaignId_State_NextAttemptAt",
                schema: "campaign",
                table: "CampaignLead",
                columns: new[] { "TenantId", "CampaignId", "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CallSession_TenantId_CampaignId_Status",
                schema: "calls",
                table: "CallSession",
                columns: new[] { "TenantId", "CampaignId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CallSession_TenantId_IdempotencyKey",
                schema: "calls",
                table: "CallSession",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CallStageTiming_CallSessionId",
                schema: "calls",
                table: "CallStageTiming",
                column: "CallSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_CallStageTiming_TenantId_CallSessionId_TurnNo",
                schema: "calls",
                table: "CallStageTiming",
                columns: new[] { "TenantId", "CallSessionId", "TurnNo" });

            migrationBuilder.CreateIndex(
                name: "IX_CallStageTiming_TenantId_Stage_StartedAt",
                schema: "calls",
                table: "CallStageTiming",
                columns: new[] { "TenantId", "Stage", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ContactConsent_TenantId_PhoneE164_Channel_GrantedAtUtc",
                schema: "compliance",
                table: "ContactConsent",
                columns: new[] { "TenantId", "PhoneE164", "Channel", "GrantedAtUtc" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_DoNotCallEntry_TenantId_PhoneE164_Scope",
                schema: "compliance",
                table: "DoNotCallEntry",
                columns: new[] { "TenantId", "PhoneE164", "Scope" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SuppressionCheck_TenantId_CampaignId_Result_Reason",
                schema: "compliance",
                table: "SuppressionCheck",
                columns: new[] { "TenantId", "CampaignId", "Result", "Reason" });

            migrationBuilder.CreateIndex(
                name: "IX_SuppressionCheck_TenantId_PhoneE164_CheckedAtUtc",
                schema: "compliance",
                table: "SuppressionCheck",
                columns: new[] { "TenantId", "PhoneE164", "CheckedAtUtc" },
                descending: new[] { false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CallStageTiming",
                schema: "calls");

            migrationBuilder.DropTable(
                name: "ContactConsent",
                schema: "compliance");

            migrationBuilder.DropTable(
                name: "DoNotCallEntry",
                schema: "compliance");

            migrationBuilder.DropTable(
                name: "SuppressionCheck",
                schema: "compliance");

            migrationBuilder.DropIndex(
                name: "IX_Lead_TenantId_PhoneE164",
                schema: "crm",
                table: "Lead");

            migrationBuilder.DropIndex(
                name: "IX_Customer_TenantId_PhoneE164",
                schema: "crm",
                table: "Customer");

            migrationBuilder.DropIndex(
                name: "IX_Contact_TenantId_ValueNormalized",
                schema: "crm",
                table: "Contact");

            migrationBuilder.DropIndex(
                name: "IX_CampaignLead_TenantId_CampaignId_State_NextAttemptAt",
                schema: "campaign",
                table: "CampaignLead");

            migrationBuilder.DropIndex(
                name: "IX_CallSession_TenantId_CampaignId_Status",
                schema: "calls",
                table: "CallSession");

            migrationBuilder.DropIndex(
                name: "IX_CallSession_TenantId_IdempotencyKey",
                schema: "calls",
                table: "CallSession");

            migrationBuilder.DropColumn(
                name: "PhoneE164",
                schema: "crm",
                table: "Lead");

            migrationBuilder.DropColumn(
                name: "PhoneE164",
                schema: "crm",
                table: "Customer");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                schema: "crm",
                table: "Customer");

            migrationBuilder.DropColumn(
                name: "ValueNormalized",
                schema: "crm",
                table: "Contact");

            migrationBuilder.DropColumn(
                name: "FinalDisposition",
                schema: "campaign",
                table: "CampaignLead");

            migrationBuilder.DropColumn(
                name: "LastCallSessionId",
                schema: "campaign",
                table: "CampaignLead");

            migrationBuilder.DropColumn(
                name: "State",
                schema: "campaign",
                table: "CampaignLead");

            migrationBuilder.DropColumn(
                name: "SuppressedAtUtc",
                schema: "campaign",
                table: "CampaignLead");

            migrationBuilder.DropColumn(
                name: "AmdResult",
                schema: "calls",
                table: "CallSession");

            migrationBuilder.DropColumn(
                name: "CampaignLeadId",
                schema: "calls",
                table: "CallSession");

            migrationBuilder.DropColumn(
                name: "DispositionSetBy",
                schema: "calls",
                table: "CallSession");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                schema: "calls",
                table: "CallSession");

            migrationBuilder.DropColumn(
                name: "RecordingConsentAnnouncedAt",
                schema: "calls",
                table: "CallSession");

            migrationBuilder.DropColumn(
                name: "TransferredToAgentId",
                schema: "calls",
                table: "CallSession");
        }
    }
}
