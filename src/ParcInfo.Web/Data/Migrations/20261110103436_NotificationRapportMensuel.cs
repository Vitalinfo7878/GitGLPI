using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParcInfo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class NotificationRapportMensuel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NotifRapportMensuel",
                table: "DroitsAcces",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotifRapportMensuel",
                table: "DroitsAcces");
        }
    }
}
