using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Sps.P2G.Gateway.Infrastructure.Database;

#nullable disable

namespace Sps.P2G.Gateway.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(GatewayDbContext))]
    [Migration("20260719090000_AddUniqueActiveRevenueAccount")]
    public partial class AddUniqueActiveRevenueAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "ClearingType",
                table: "RevenueAccounts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT
                        "Id",
                        row_number() OVER (
                            PARTITION BY "AuthorityId", "AccountNumber", "ClearingType"
                            ORDER BY "IsDefault" DESC, "CreatedAt" ASC, "Id" ASC
                        ) AS rn
                    FROM "RevenueAccounts"
                    WHERE "Status" = 'Active'
                )
                UPDATE "RevenueAccounts" AS account
                SET
                    "Status" = 'Inactive',
                    "IsDefault" = false,
                    "UpdatedAt" = NOW()
                FROM ranked
                WHERE account."Id" = ranked."Id"
                    AND ranked.rn > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_RevenueAccounts_AuthorityId_AccountNumber_ClearingType",
                table: "RevenueAccounts",
                columns: new[] { "AuthorityId", "AccountNumber", "ClearingType" },
                unique: true,
                filter: "\"Status\" = 'Active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RevenueAccounts_AuthorityId_AccountNumber_ClearingType",
                table: "RevenueAccounts");

            migrationBuilder.AlterColumn<string>(
                name: "ClearingType",
                table: "RevenueAccounts",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);
        }
    }
}
