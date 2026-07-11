using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sps.P2G.Gateway.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxCallbackClaiming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LockExpiresAt",
                table: "OutboxMessages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LockToken",
                table: "OutboxMessages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LockedAt",
                table: "OutboxMessages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_LockToken",
                table: "OutboxMessages",
                column: "LockToken");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Status_LockExpiresAt",
                table: "OutboxMessages",
                columns: new[] { "Status", "LockExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_LockToken",
                table: "OutboxMessages");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_Status_LockExpiresAt",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "LockExpiresAt",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "LockToken",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "LockedAt",
                table: "OutboxMessages");
        }
    }
}
