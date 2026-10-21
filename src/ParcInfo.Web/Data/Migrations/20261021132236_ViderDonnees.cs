using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParcInfo.Web.Data.Migrations
{
    /// <summary>
    /// Remise à zéro des données avant un nouvel import (demande du 21/10/2026) : matériels, mouvements, fiches, sites,
    /// personnes, régions, demandes, tickets, courriels et journal. Les clés de connexion (DataProtectionKeys) sont conservées.
    /// Exécutée une seule fois par base ; sans effet sur une base neuve. Irréversible.
    /// </summary>
    public partial class ViderDonnees : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                TRUNCATE TABLE "MessagesTickets", "Tickets", "Mouvements", "Fiches", "Materiels", "Sites", "Personnes",
                    "Regions", "Demandes", "Courriels", "Journal" RESTART IDENTITY CASCADE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Données supprimées : rien à restaurer
        }
    }
}
