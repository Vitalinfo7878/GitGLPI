using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParcInfo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Demandes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Demandes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IdPublic = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Numero = table.Column<int>(type: "int", nullable: false),
                    CreeLe = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SaisiePar = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    DemandeurNom = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Objets = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Concerne = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    AdresseMailACreer = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Raison = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    AdresseLivraison = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    DestinataireLivraison = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    DemandeurEmail = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    DemandeurTelephone = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Echeance = table.Column<DateOnly>(type: "date", nullable: true),
                    StatutValidation = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    DecisionLe = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecisionPar = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    DecisionParEmail = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CommentaireDecision = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Priorite = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    StatutRealisation = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CommentaireInformatique = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    RealiseeLe = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifieLe = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Demandes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Demandes_DemandeurEmail",
                table: "Demandes",
                column: "DemandeurEmail");

            migrationBuilder.CreateIndex(
                name: "IX_Demandes_IdPublic",
                table: "Demandes",
                column: "IdPublic",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Demandes_Numero",
                table: "Demandes",
                column: "Numero",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Demandes_SaisiePar",
                table: "Demandes",
                column: "SaisiePar");

            migrationBuilder.CreateIndex(
                name: "IX_Demandes_StatutValidation",
                table: "Demandes",
                column: "StatutValidation");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Demandes");
        }
    }
}
