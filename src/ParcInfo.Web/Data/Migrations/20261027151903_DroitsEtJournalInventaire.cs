using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ParcInfo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class DroitsEtJournalInventaire : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Details",
                table: "Journal",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "DroitsAcces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Nom = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Gestionnaire = table.Column<bool>(type: "boolean", nullable: false),
                    Valideur = table.Column<bool>(type: "boolean", nullable: false),
                    AdminTickets = table.Column<bool>(type: "boolean", nullable: false),
                    ConsultationInventaire = table.Column<bool>(type: "boolean", nullable: false),
                    ModifiePar = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    ModifieLe = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DroitsAcces", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Journal_Action",
                table: "Journal",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_DroitsAcces_Email",
                table: "DroitsAcces",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DroitsAcces");

            migrationBuilder.DropIndex(
                name: "IX_Journal_Action",
                table: "Journal");

            migrationBuilder.AlterColumn<string>(
                name: "Details",
                table: "Journal",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(4000)",
                oldMaxLength: 4000,
                oldNullable: true);
        }
    }
}
