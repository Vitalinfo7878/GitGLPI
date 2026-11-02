using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParcInfo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class FicheAttributionASigner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FicheAttributionAlerteLe",
                table: "Demandes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FicheAttributionEnvoyeeLe",
                table: "Demandes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "FicheAttributionPrevueLe",
                table: "Demandes",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PiecesJointes",
                table: "Courriels",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FicheAttributionAlerteLe",
                table: "Demandes");

            migrationBuilder.DropColumn(
                name: "FicheAttributionEnvoyeeLe",
                table: "Demandes");

            migrationBuilder.DropColumn(
                name: "FicheAttributionPrevueLe",
                table: "Demandes");

            migrationBuilder.DropColumn(
                name: "PiecesJointes",
                table: "Courriels");
        }
    }
}
