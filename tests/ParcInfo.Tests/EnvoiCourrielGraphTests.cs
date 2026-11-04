using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Abstractions.Store;
using Microsoft.Kiota.Serialization.Json;
using ParcInfo.Web.Services;

namespace ParcInfo.Tests;

/// <summary>
/// Requête envoyée à Microsoft Graph, sérialisée comme le fait le client Graph (backing store : une propriété affectée à null
/// est envoyée « null », ce que Graph refuse pour attachments).
/// </summary>
public sealed class EnvoiCourrielGraphTests
{
    private static async Task<string> Json(IParsable requete)
    {
        var w = new BackingStoreSerializationWriterProxyFactory(new JsonSerializationWriterFactory()).GetSerializationWriter("application/json");
        w.WriteObjectValue(null, requete);
        using var r = new StreamReader(w.GetSerializedContent());
        return await r.ReadToEndAsync();
    }

    [Fact]
    public async Task Sans_piece_jointe_aucune_propriete_attachments()
    {
        var json = await Json(EnvoiCourrielGraph.Requete("a@vitalrest.com", "Sujet", "<p>Corps</p>"));
        Assert.DoesNotContain("attachments", json);
        Assert.Contains("\"toRecipients\"", json);
        Assert.DoesNotContain("attachments", await Json(EnvoiCourrielGraph.Requete("a@vitalrest.com", "Sujet", "<p>Corps</p>", [])));
    }

    [Fact]
    public async Task Avec_piece_jointe_un_tableau_de_fichiers()
    {
        var json = await Json(EnvoiCourrielGraph.Requete("a@vitalrest.com", "Sujet", "<p>Corps</p>", [new PieceJointe("FIA2026 0002.pdf", "application/pdf", [1, 2, 3])]));
        Assert.Contains("\"attachments\":[{", json);
        Assert.Contains("\"@odata.type\":\"#microsoft.graph.fileAttachment\"", json);
        Assert.Contains("\"name\":\"FIA2026 0002.pdf\"", json);
        Assert.Contains("\"contentBytes\":\"AQID\"", json);
    }
}
