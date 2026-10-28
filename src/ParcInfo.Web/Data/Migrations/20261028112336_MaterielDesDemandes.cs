using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParcInfo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class MaterielDesDemandes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DemandesMateriels",
                columns: table => new
                {
                    DemandeId = table.Column<int>(type: "integer", nullable: false),
                    MaterielId = table.Column<int>(type: "integer", nullable: false),
                    Le = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Par = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DemandesMateriels", x => new { x.DemandeId, x.MaterielId });
                    table.ForeignKey(
                        name: "FK_DemandesMateriels_Demandes_DemandeId",
                        column: x => x.DemandeId,
                        principalTable: "Demandes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DemandesMateriels_Materiels_MaterielId",
                        column: x => x.MaterielId,
                        principalTable: "Materiels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DemandesMateriels_MaterielId",
                table: "DemandesMateriels",
                column: "MaterielId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DemandesMateriels");
        }
    }
}
