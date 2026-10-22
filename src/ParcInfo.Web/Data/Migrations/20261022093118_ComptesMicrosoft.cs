using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParcInfo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ComptesMicrosoft : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EntraObjectId",
                table: "Sites",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SansCompteMicrosoft",
                table: "Sites",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SansCompteMicrosoft",
                table: "Personnes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ComptesMicrosoft",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    NomAffichage = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Prenom = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Nom = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Upn = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Email = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    AutresAdresses = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Poste = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Service = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Bureau = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Societe = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Matricule = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    TelephoneMobile = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    TelephonePro = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Adresse = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Ville = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    ResponsableId = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    ResponsableNom = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Groupes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Licences = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Actif = table.Column<bool>(type: "boolean", nullable: false),
                    CreeDansEntraLe = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Supprime = table.Column<bool>(type: "boolean", nullable: false),
                    SynchroniseLe = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComptesMicrosoft", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sites_EntraObjectId",
                table: "Sites",
                column: "EntraObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Personnes_EntraObjectId",
                table: "Personnes",
                column: "EntraObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ComptesMicrosoft_Email",
                table: "ComptesMicrosoft",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_ComptesMicrosoft_Upn",
                table: "ComptesMicrosoft",
                column: "Upn");

            migrationBuilder.AddForeignKey(
                name: "FK_Personnes_ComptesMicrosoft_EntraObjectId",
                table: "Personnes",
                column: "EntraObjectId",
                principalTable: "ComptesMicrosoft",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Sites_ComptesMicrosoft_EntraObjectId",
                table: "Sites",
                column: "EntraObjectId",
                principalTable: "ComptesMicrosoft",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Personnes_ComptesMicrosoft_EntraObjectId",
                table: "Personnes");

            migrationBuilder.DropForeignKey(
                name: "FK_Sites_ComptesMicrosoft_EntraObjectId",
                table: "Sites");

            migrationBuilder.DropTable(
                name: "ComptesMicrosoft");

            migrationBuilder.DropIndex(
                name: "IX_Sites_EntraObjectId",
                table: "Sites");

            migrationBuilder.DropIndex(
                name: "IX_Personnes_EntraObjectId",
                table: "Personnes");

            migrationBuilder.DropColumn(
                name: "EntraObjectId",
                table: "Sites");

            migrationBuilder.DropColumn(
                name: "SansCompteMicrosoft",
                table: "Sites");

            migrationBuilder.DropColumn(
                name: "SansCompteMicrosoft",
                table: "Personnes");
        }
    }
}
