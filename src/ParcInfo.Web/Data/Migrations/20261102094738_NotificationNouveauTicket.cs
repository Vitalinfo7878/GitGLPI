using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParcInfo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class NotificationNouveauTicket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NotifNouveauTicket",
                table: "DroitsAcces",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotifNouveauTicket",
                table: "DroitsAcces");
        }
    }
}
