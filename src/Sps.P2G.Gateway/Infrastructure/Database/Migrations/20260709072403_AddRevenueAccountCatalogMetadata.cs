using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sps.P2G.Gateway.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddRevenueAccountCatalogMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MdaName",
                table: "RevenueAccounts",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ServiceName",
                table: "RevenueAccounts",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MdaName",
                table: "RevenueAccounts");

            migrationBuilder.DropColumn(
                name: "ServiceName",
                table: "RevenueAccounts");
        }
    }
}
