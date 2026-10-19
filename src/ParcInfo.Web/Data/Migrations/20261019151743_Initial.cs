using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParcInfo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Courriels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Destinataire = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Sujet = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CorpsHtml = table.Column<string>(type: "nvarchar(max)", maxLength: 2147483647, nullable: false),
                    EnvoyeLe = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Simule = table.Column<bool>(type: "bit", nullable: false),
                    Erreur = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Courriels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Fiches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IdPublic = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Annee = table.Column<int>(type: "int", nullable: false),
                    Numero = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    DestinataireNom = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    DestinataireEmail = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    StatutReception = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ReceptionConfirmeeLe = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReceptionConfirmeePar = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ReceptionConfirmeeParEmail = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    DerniereRelanceLe = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Pdf = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    PdfGenereLe = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreePar = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreeLe = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Fiches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Journal",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Date = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Utilisateur = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Details = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    MaterielId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Journal", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Regions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Nom = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Regions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Personnes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NomComplet = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    EntraObjectId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RegionId = table.Column<int>(type: "int", nullable: true),
                    Actif = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Personnes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Personnes_Regions_RegionId",
                        column: x => x.RegionId,
                        principalTable: "Regions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Sites",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Nom = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RegionId = table.Column<int>(type: "int", nullable: true),
                    Responsable = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Telephone = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Adresse = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Actif = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sites_Regions_RegionId",
                        column: x => x.RegionId,
                        principalTable: "Regions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Materiels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NumInterne = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Modele = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    NumSerie = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Statut = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SiteId = table.Column<int>(type: "int", nullable: true),
                    PersonneId = table.Column<int>(type: "int", nullable: true),
                    RegionId = table.Column<int>(type: "int", nullable: true),
                    Configuration = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    AnneeAcquisition = table.Column<int>(type: "int", nullable: true),
                    DateMiseEnPlace = table.Column<DateOnly>(type: "date", nullable: true),
                    ProfilMicrosoft = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    LicenceMicrosoft = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    OfficeEnDur = table.Column<bool>(type: "bit", nullable: true),
                    Note = table.Column<int>(type: "int", nullable: true),
                    MotDePasseSession = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ConnexionAdmin = table.Column<bool>(type: "bit", nullable: true),
                    VersionWindows = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    EditionWindows = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    AChanger = table.Column<bool>(type: "bit", nullable: false),
                    Labtech = table.Column<bool>(type: "bit", nullable: true),
                    Commentaire = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CleOffice = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    NumImmo = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    NumFacture = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreeLe = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifieLe = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Materiels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Materiels_Personnes_PersonneId",
                        column: x => x.PersonneId,
                        principalTable: "Personnes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Materiels_Regions_RegionId",
                        column: x => x.RegionId,
                        principalTable: "Regions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Materiels_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Mouvements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Numero = table.Column<int>(type: "int", nullable: false),
                    NumeroOrigine = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    MaterielId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: true),
                    Expediteur = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    DestinationSiteId = table.Column<int>(type: "int", nullable: true),
                    DestinationPersonneId = table.Column<int>(type: "int", nullable: true),
                    DestinationLibre = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Region = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    DocumentReference = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    DateReception = table.Column<DateOnly>(type: "date", nullable: true),
                    Commentaire = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    FicheId = table.Column<int>(type: "int", nullable: true),
                    CreePar = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreeLe = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Mouvements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Mouvements_Fiches_FicheId",
                        column: x => x.FicheId,
                        principalTable: "Fiches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Mouvements_Materiels_MaterielId",
                        column: x => x.MaterielId,
                        principalTable: "Materiels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Mouvements_Personnes_DestinationPersonneId",
                        column: x => x.DestinationPersonneId,
                        principalTable: "Personnes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Mouvements_Sites_DestinationSiteId",
                        column: x => x.DestinationSiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Fiches_Code",
                table: "Fiches",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Fiches_DestinataireEmail",
                table: "Fiches",
                column: "DestinataireEmail");

            migrationBuilder.CreateIndex(
                name: "IX_Fiches_IdPublic",
                table: "Fiches",
                column: "IdPublic",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Journal_Date",
                table: "Journal",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_Materiels_NumInterne",
                table: "Materiels",
                column: "NumInterne",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Materiels_NumSerie",
                table: "Materiels",
                column: "NumSerie");

            migrationBuilder.CreateIndex(
                name: "IX_Materiels_PersonneId",
                table: "Materiels",
                column: "PersonneId");

            migrationBuilder.CreateIndex(
                name: "IX_Materiels_RegionId",
                table: "Materiels",
                column: "RegionId");

            migrationBuilder.CreateIndex(
                name: "IX_Materiels_SiteId",
                table: "Materiels",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_Materiels_Statut",
                table: "Materiels",
                column: "Statut");

            migrationBuilder.CreateIndex(
                name: "IX_Mouvements_DestinationPersonneId",
                table: "Mouvements",
                column: "DestinationPersonneId");

            migrationBuilder.CreateIndex(
                name: "IX_Mouvements_DestinationSiteId",
                table: "Mouvements",
                column: "DestinationSiteId");

            migrationBuilder.CreateIndex(
                name: "IX_Mouvements_FicheId",
                table: "Mouvements",
                column: "FicheId");

            migrationBuilder.CreateIndex(
                name: "IX_Mouvements_MaterielId",
                table: "Mouvements",
                column: "MaterielId");

            migrationBuilder.CreateIndex(
                name: "IX_Mouvements_Numero",
                table: "Mouvements",
                column: "Numero",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Personnes_Email",
                table: "Personnes",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_Personnes_NomComplet",
                table: "Personnes",
                column: "NomComplet");

            migrationBuilder.CreateIndex(
                name: "IX_Personnes_RegionId",
                table: "Personnes",
                column: "RegionId");

            migrationBuilder.CreateIndex(
                name: "IX_Regions_Nom",
                table: "Regions",
                column: "Nom",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sites_Code",
                table: "Sites",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sites_RegionId",
                table: "Sites",
                column: "RegionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Courriels");

            migrationBuilder.DropTable(
                name: "Journal");

            migrationBuilder.DropTable(
                name: "Mouvements");

            migrationBuilder.DropTable(
                name: "Fiches");

            migrationBuilder.DropTable(
                name: "Materiels");

            migrationBuilder.DropTable(
                name: "Personnes");

            migrationBuilder.DropTable(
                name: "Sites");

            migrationBuilder.DropTable(
                name: "Regions");
        }
    }
}
