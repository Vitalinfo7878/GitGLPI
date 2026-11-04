using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParcInfo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class DemandeDepuisTicket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TicketOrigineId",
                table: "Demandes",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Demandes_TicketOrigineId",
                table: "Demandes",
                column: "TicketOrigineId");

            migrationBuilder.AddForeignKey(
                name: "FK_Demandes_Tickets_TicketOrigineId",
                table: "Demandes",
                column: "TicketOrigineId",
                principalTable: "Tickets",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Demandes_Tickets_TicketOrigineId",
                table: "Demandes");

            migrationBuilder.DropIndex(
                name: "IX_Demandes_TicketOrigineId",
                table: "Demandes");

            migrationBuilder.DropColumn(
                name: "TicketOrigineId",
                table: "Demandes");
        }
    }
}
