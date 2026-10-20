using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ParcInfo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Tickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Tickets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Numero = table.Column<int>(type: "integer", nullable: false),
                    CreeLe = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ModifieLe = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SaisiePar = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    DemandeurNom = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    DemandeurEmail = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    DemandeurTelephone = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Lieu = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Categorie = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Impact = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Portee = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Titre = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    DejaEssaye = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Disponibilites = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    MaterielReference = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    MaterielId = table.Column<int>(type: "integer", nullable: true),
                    Priorite = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Statut = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AssigneA = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    PrisEnChargeLe = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResoluLe = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosLe = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NbReouvertures = table.Column<int>(type: "integer", nullable: false),
                    Diagnostic = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Cause = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Solution = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ModeResolution = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    TempsPasseMinutes = table.Column<int>(type: "integer", nullable: true),
                    SatisfactionNote = table.Column<int>(type: "integer", nullable: true),
                    SatisfactionCommentaire = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tickets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tickets_Materiels_MaterielId",
                        column: x => x.MaterielId,
                        principalTable: "Materiels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MessagesTickets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TicketId = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Auteur = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    AuteurEmail = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Texte = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Interne = table.Column<bool>(type: "boolean", nullable: false),
                    Systeme = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessagesTickets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessagesTickets_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MessagesTickets_TicketId",
                table: "MessagesTickets",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_CreeLe",
                table: "Tickets",
                column: "CreeLe");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_DemandeurEmail",
                table: "Tickets",
                column: "DemandeurEmail");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_MaterielId",
                table: "Tickets",
                column: "MaterielId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_Numero",
                table: "Tickets",
                column: "Numero",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_SaisiePar",
                table: "Tickets",
                column: "SaisiePar");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_Statut",
                table: "Tickets",
                column: "Statut");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessagesTickets");

            migrationBuilder.DropTable(
                name: "Tickets");
        }
    }
}
