using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParcInfo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class RestaurationJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Instantane",
                table: "Journal",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RestaureLe",
                table: "Journal",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RestaurePar",
                table: "Journal",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Instantane",
                table: "Journal");

            migrationBuilder.DropColumn(
                name: "RestaureLe",
                table: "Journal");

            migrationBuilder.DropColumn(
                name: "RestaurePar",
                table: "Journal");
        }
    }
}
