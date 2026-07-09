using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sps.P2G.Gateway.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationalIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_AggregateId",
                table: "OutboxMessages",
                column: "AggregateId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Status_NextAttemptAt",
                table: "OutboxMessages",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Type_Status_NextAttemptAt",
                table: "OutboxMessages",
                columns: new[] { "Type", "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CallbackDeliveries_AuthorityId_Status_CreatedAt",
                table: "CallbackDeliveries",
                columns: new[] { "AuthorityId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CallbackDeliveries_CallbackSubscriptionId_Status",
                table: "CallbackDeliveries",
                columns: new[] { "CallbackSubscriptionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CallbackDeliveries_EventId",
                table: "CallbackDeliveries",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_Bills_AuthorityId_CreatedAt",
                table: "Bills",
                columns: new[] { "AuthorityId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Bills_AuthorityId_StatusProjection_UpdatedAt",
                table: "Bills",
                columns: new[] { "AuthorityId", "StatusProjection", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BillReversals_AuthorityId_ReversedAt",
                table: "BillReversals",
                columns: new[] { "AuthorityId", "ReversedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BillPayments_AuthorityId_PaidAt",
                table: "BillPayments",
                columns: new[] { "AuthorityId", "PaidAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BillPayments_AuthorityId_Status_PaidAt",
                table: "BillPayments",
                columns: new[] { "AuthorityId", "Status", "PaidAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BillPaymentAttempts_AuthorityId_CreatedAt",
                table: "BillPaymentAttempts",
                columns: new[] { "AuthorityId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BillPaymentAttempts_Reason_CreatedAt",
                table: "BillPaymentAttempts",
                columns: new[] { "Reason", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BillPaymentAttempts_Status_CreatedAt",
                table: "BillPaymentAttempts",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_AggregateId",
                table: "OutboxMessages");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_Status_NextAttemptAt",
                table: "OutboxMessages");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_Type_Status_NextAttemptAt",
                table: "OutboxMessages");

            migrationBuilder.DropIndex(
                name: "IX_CallbackDeliveries_AuthorityId_Status_CreatedAt",
                table: "CallbackDeliveries");

            migrationBuilder.DropIndex(
                name: "IX_CallbackDeliveries_CallbackSubscriptionId_Status",
                table: "CallbackDeliveries");

            migrationBuilder.DropIndex(
                name: "IX_CallbackDeliveries_EventId",
                table: "CallbackDeliveries");

            migrationBuilder.DropIndex(
                name: "IX_Bills_AuthorityId_CreatedAt",
                table: "Bills");

            migrationBuilder.DropIndex(
                name: "IX_Bills_AuthorityId_StatusProjection_UpdatedAt",
                table: "Bills");

            migrationBuilder.DropIndex(
                name: "IX_BillReversals_AuthorityId_ReversedAt",
                table: "BillReversals");

            migrationBuilder.DropIndex(
                name: "IX_BillPayments_AuthorityId_PaidAt",
                table: "BillPayments");

            migrationBuilder.DropIndex(
                name: "IX_BillPayments_AuthorityId_Status_PaidAt",
                table: "BillPayments");

            migrationBuilder.DropIndex(
                name: "IX_BillPaymentAttempts_AuthorityId_CreatedAt",
                table: "BillPaymentAttempts");

            migrationBuilder.DropIndex(
                name: "IX_BillPaymentAttempts_Reason_CreatedAt",
                table: "BillPaymentAttempts");

            migrationBuilder.DropIndex(
                name: "IX_BillPaymentAttempts_Status_CreatedAt",
                table: "BillPaymentAttempts");
        }
    }
}
