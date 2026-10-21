using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParcInfo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class SansConfirmationReception : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DerniereRelanceLe",
                table: "Fiches");

            migrationBuilder.DropColumn(
                name: "ReceptionConfirmeeLe",
                table: "Fiches");

            migrationBuilder.DropColumn(
                name: "ReceptionConfirmeePar",
                table: "Fiches");

            migrationBuilder.DropColumn(
                name: "ReceptionConfirmeeParEmail",
                table: "Fiches");

            migrationBuilder.DropColumn(
                name: "StatutReception",
                table: "Fiches");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DerniereRelanceLe",
                table: "Fiches",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReceptionConfirmeeLe",
                table: "Fiches",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceptionConfirmeePar",
                table: "Fiches",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceptionConfirmeeParEmail",
                table: "Fiches",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatutReception",
                table: "Fiches",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");
        }
    }
}
