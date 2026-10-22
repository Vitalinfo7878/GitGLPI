using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParcInfo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class DoublonsIgnores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DoublonsIgnores",
                columns: table => new
                {
                    Cle = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Par = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Le = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DoublonsIgnores", x => x.Cle);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DoublonsIgnores");
        }
    }
}
