using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParcInfo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class TicketRapatriement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DemandeId",
                table: "Tickets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Interne",
                table: "Tickets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MaterielRemplace",
                table: "Demandes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaterielRemplaceId",
                table: "Demandes",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_DemandeId",
                table: "Tickets",
                column: "DemandeId");

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_Demandes_DemandeId",
                table: "Tickets",
                column: "DemandeId",
                principalTable: "Demandes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_Demandes_DemandeId",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_DemandeId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "DemandeId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "Interne",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "MaterielRemplace",
                table: "Demandes");

            migrationBuilder.DropColumn(
                name: "MaterielRemplaceId",
                table: "Demandes");
        }
    }
}
