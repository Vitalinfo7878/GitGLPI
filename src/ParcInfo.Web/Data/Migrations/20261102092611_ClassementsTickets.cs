using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ParcInfo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClassementsTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClassementId",
                table: "Tickets",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ClassementsTickets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Nom = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreeLe = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreePar = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassementsTickets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ClassementId",
                table: "Tickets",
                column: "ClassementId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassementsTickets_Nom",
                table: "ClassementsTickets",
                column: "Nom",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_ClassementsTickets_ClassementId",
                table: "Tickets",
                column: "ClassementId",
                principalTable: "ClassementsTickets",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_ClassementsTickets_ClassementId",
                table: "Tickets");

            migrationBuilder.DropTable(
                name: "ClassementsTickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_ClassementId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ClassementId",
                table: "Tickets");
        }
    }
}
