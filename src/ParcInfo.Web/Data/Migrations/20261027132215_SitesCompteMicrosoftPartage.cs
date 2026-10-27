using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParcInfo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class SitesCompteMicrosoftPartage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sites_EntraObjectId",
                table: "Sites");

            migrationBuilder.CreateIndex(
                name: "IX_Sites_EntraObjectId",
                table: "Sites",
                column: "EntraObjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sites_EntraObjectId",
                table: "Sites");

            migrationBuilder.CreateIndex(
                name: "IX_Sites_EntraObjectId",
                table: "Sites",
                column: "EntraObjectId",
                unique: true);
        }
    }
}
