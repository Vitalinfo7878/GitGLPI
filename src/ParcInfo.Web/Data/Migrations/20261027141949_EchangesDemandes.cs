using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ParcInfo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class EchangesDemandes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MessagesDemandes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DemandeId = table.Column<int>(type: "integer", nullable: false),
                    Le = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Auteur = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AuteurEmail = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Texte = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessagesDemandes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessagesDemandes_Demandes_DemandeId",
                        column: x => x.DemandeId,
                        principalTable: "Demandes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MessagesDemandes_DemandeId",
                table: "MessagesDemandes",
                column: "DemandeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessagesDemandes");
        }
    }
}
