using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ParcInfo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhotosTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PhotosTickets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TicketId = table.Column<int>(type: "integer", nullable: false),
                    MessageId = table.Column<int>(type: "integer", nullable: true),
                    Nom = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TypeContenu = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Taille = table.Column<int>(type: "integer", nullable: false),
                    Contenu = table.Column<byte[]>(type: "bytea", nullable: false),
                    AjouteeLe = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AjouteePar = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotosTickets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhotosTickets_MessagesTickets_MessageId",
                        column: x => x.MessageId,
                        principalTable: "MessagesTickets",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PhotosTickets_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PhotosTickets_MessageId",
                table: "PhotosTickets",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_PhotosTickets_TicketId",
                table: "PhotosTickets",
                column: "TicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PhotosTickets");
        }
    }
}
