using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sps.P2G.Gateway.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BillEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    BillId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorityId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EventTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorType = table.Column<string>(type: "text", nullable: false),
                    ActorId = table.Column<string>(type: "text", nullable: false),
                    SourceSystem = table.Column<string>(type: "text", nullable: false),
                    CorrelationId = table.Column<string>(type: "text", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    PayloadHash = table.Column<string>(type: "text", nullable: false),
                    PreviousEventHash = table.Column<string>(type: "text", nullable: false),
                    EventHash = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BillPaymentAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BillId = table.Column<Guid>(type: "uuid", nullable: true),
                    AuthorityId = table.Column<Guid>(type: "uuid", nullable: true),
                    TxId = table.Column<string>(type: "text", nullable: false),
                    EndToEndId = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    RawPayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillPaymentAttempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BillPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BillId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorityId = table.Column<Guid>(type: "uuid", nullable: false),
                    SipsTransactionId = table.Column<string>(type: "text", nullable: false),
                    EndToEndId = table.Column<string>(type: "text", nullable: false),
                    OriginalMessageId = table.Column<string>(type: "text", nullable: false),
                    TxId = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    PaidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PayerName = table.Column<string>(type: "text", nullable: false),
                    PayerAccount = table.Column<string>(type: "text", nullable: false),
                    PayerBankCode = table.Column<string>(type: "text", nullable: false),
                    PayerBankName = table.Column<string>(type: "text", nullable: false),
                    PayerClearingType = table.Column<string>(type: "text", nullable: false),
                    PayeeName = table.Column<string>(type: "text", nullable: false),
                    PayeeAccount = table.Column<string>(type: "text", nullable: false),
                    PayeeBankCode = table.Column<string>(type: "text", nullable: false),
                    PayeeBankName = table.Column<string>(type: "text", nullable: false),
                    PayeeClearingType = table.Column<string>(type: "text", nullable: false),
                    RawPayloadJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillPayments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BillReversals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BillId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorityId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalSipsTransactionId = table.Column<string>(type: "text", nullable: false),
                    ReversalTransactionId = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    ReversedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillReversals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Bills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorityId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalInvoiceId = table.Column<string>(type: "text", nullable: false),
                    UPR = table.Column<string>(type: "text", nullable: false),
                    MdaCode = table.Column<string>(type: "text", nullable: false),
                    ServiceCode = table.Column<string>(type: "text", nullable: false),
                    RevenueCode = table.Column<string>(type: "text", nullable: false),
                    TaxpayerReference = table.Column<string>(type: "text", nullable: false),
                    PayerDisplayName = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevenueAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    StatusProjection = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedPayloadJson = table.Column<string>(type: "text", nullable: false),
                    PayloadHash = table.Column<string>(type: "text", nullable: false),
                    RecordHash = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bills", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BillVerificationSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BillId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorityId = table.Column<Guid>(type: "uuid", nullable: false),
                    VerificationRequestId = table.Column<string>(type: "text", nullable: false),
                    PayerBankCode = table.Column<string>(type: "text", nullable: false),
                    PayerChannel = table.Column<string>(type: "text", nullable: false),
                    Descriptor = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                    DescriptorVersion = table.Column<string>(type: "text", nullable: false),
                    DescriptorHmac = table.Column<string>(type: "text", nullable: false),
                    DescriptorKeyVersion = table.Column<int>(type: "integer", nullable: false),
                    CreditorClearingId = table.Column<string>(type: "text", nullable: false),
                    CreditorClearingType = table.Column<string>(type: "text", nullable: false),
                    CreditorName = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillVerificationSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CallbackDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorityId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    CallbackSubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    Signature = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastHttpStatus = table.Column<int>(type: "integer", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CallbackDeliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CallbackSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorityId = table.Column<Guid>(type: "uuid", nullable: false),
                    CallbackUrl = table.Column<string>(type: "text", nullable: false),
                    Secret = table.Column<string>(type: "text", nullable: false),
                    SubscribedEventsJson = table.Column<string>(type: "jsonb", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CallbackSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HmacKeyVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    KeyHash = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HmacKeyVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdempotencyRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    PayloadHash = table.Column<string>(type: "text", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdempotencyRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    AggregateId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RevenueAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorityId = table.Column<Guid>(type: "uuid", nullable: false),
                    MdaCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ServiceCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RevenueCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BankCode = table.Column<string>(type: "text", nullable: false),
                    BankName = table.Column<string>(type: "text", nullable: false),
                    AccountNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AccountName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ClearingType = table.Column<string>(type: "text", nullable: false),
                    BillerBic = table.Column<string>(type: "text", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RevenueAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RevenueAuthorities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ApiKeyHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RevenueAuthorities", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BillEvents_AuthorityId_Sequence",
                table: "BillEvents",
                columns: new[] { "AuthorityId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_BillEvents_BillId_EventTime",
                table: "BillEvents",
                columns: new[] { "BillId", "EventTime" });

            migrationBuilder.CreateIndex(
                name: "IX_BillEvents_Sequence",
                table: "BillEvents",
                column: "Sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillPaymentAttempts_TxId",
                table: "BillPaymentAttempts",
                column: "TxId");

            migrationBuilder.CreateIndex(
                name: "IX_BillPayments_BillId_Status",
                table: "BillPayments",
                columns: new[] { "BillId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_BillPayments_SipsTransactionId",
                table: "BillPayments",
                column: "SipsTransactionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillPayments_TxId",
                table: "BillPayments",
                column: "TxId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillReversals_OriginalSipsTransactionId_ReversalTransaction~",
                table: "BillReversals",
                columns: new[] { "OriginalSipsTransactionId", "ReversalTransactionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bills_AuthorityId_ExternalInvoiceId",
                table: "Bills",
                columns: new[] { "AuthorityId", "ExternalInvoiceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bills_AuthorityId_UPR",
                table: "Bills",
                columns: new[] { "AuthorityId", "UPR" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bills_StatusProjection",
                table: "Bills",
                column: "StatusProjection");

            migrationBuilder.CreateIndex(
                name: "IX_Bills_TaxpayerReference",
                table: "Bills",
                column: "TaxpayerReference");

            migrationBuilder.CreateIndex(
                name: "IX_BillVerificationSnapshots_VerificationRequestId",
                table: "BillVerificationSnapshots",
                column: "VerificationRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_HmacKeyVersions_Version",
                table: "HmacKeyVersions",
                column: "Version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyRecords_Key",
                table: "IdempotencyRecords",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RevenueAccounts_AuthorityId_MdaCode_ServiceCode_IsDefault",
                table: "RevenueAccounts",
                columns: new[] { "AuthorityId", "MdaCode", "ServiceCode", "IsDefault" });

            migrationBuilder.CreateIndex(
                name: "IX_RevenueAuthorities_Code",
                table: "RevenueAuthorities",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillEvents");

            migrationBuilder.DropTable(
                name: "BillPaymentAttempts");

            migrationBuilder.DropTable(
                name: "BillPayments");

            migrationBuilder.DropTable(
                name: "BillReversals");

            migrationBuilder.DropTable(
                name: "Bills");

            migrationBuilder.DropTable(
                name: "BillVerificationSnapshots");

            migrationBuilder.DropTable(
                name: "CallbackDeliveries");

            migrationBuilder.DropTable(
                name: "CallbackSubscriptions");

            migrationBuilder.DropTable(
                name: "HmacKeyVersions");

            migrationBuilder.DropTable(
                name: "IdempotencyRecords");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "RevenueAccounts");

            migrationBuilder.DropTable(
                name: "RevenueAuthorities");
        }
    }
}
