using System.Security.Cryptography;
using System.Text;

namespace ParcInfo.Web.Services;

/// <summary>
/// Chiffrement réversible des secrets stockés en base (mots de passe de session) : AES-256-GCM, avec une clé
/// conservée hors de la base (secret de l'application Container Apps, réglage « Chiffrement:Cle »).
/// Une copie de la base seule ne permet donc pas de lire les mots de passe.
/// Format stocké : « v1: » + base64(nonce 12 octets | texte chiffré | étiquette 16 octets).
/// Sans clé configurée (poste de développement), les valeurs restent en clair ; les valeurs en clair déjà en base
/// sont chiffrées au démarrage dès que la clé est configurée (voir InitialisationBase).
/// </summary>
public static class ChiffrementSecrets
{
    public const string Prefixe = "v1:";
    private const int TailleNonce = 12, TailleEtiquette = 16;

    // Statique : le convertisseur EF Core fait partie du modèle, qui est construit une seule fois pour toute l'application.
    private static byte[]? _cle;

    public static bool Actif => _cle is not null;

    /// <summary>Clé AES de 256 bits encodée en base64 (ex. « openssl rand -base64 32 »). Vide : pas de chiffrement.</summary>
    public static void Configurer(string? cleBase64)
    {
        if (string.IsNullOrWhiteSpace(cleBase64)) return;
        byte[] cle;
        try { cle = Convert.FromBase64String(cleBase64.Trim()); }
        catch (FormatException) { throw new InvalidOperationException("Chiffrement:Cle n'est pas du base64 valide."); }
        if (cle.Length != 32) throw new InvalidOperationException("Chiffrement:Cle doit faire 32 octets (256 bits) : générez-la avec « openssl rand -base64 32 ».");
        _cle = cle;
    }

    public static bool EstChiffre(string? valeur) => valeur?.StartsWith(Prefixe, StringComparison.Ordinal) == true;

    public static string? Chiffrer(string? clair)
    {
        // Déjà chiffré (ou illisible avec la clé actuelle, voir Dechiffrer) : conservé tel quel
        if (clair is null || _cle is null || EstChiffre(clair)) return clair;
        var donnees = Encoding.UTF8.GetBytes(clair);
        var resultat = new byte[TailleNonce + donnees.Length + TailleEtiquette];
        var nonce = resultat.AsSpan(0, TailleNonce);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_cle, TailleEtiquette);
        aes.Encrypt(nonce, donnees, resultat.AsSpan(TailleNonce, donnees.Length), resultat.AsSpan(TailleNonce + donnees.Length));
        return Prefixe + Convert.ToBase64String(resultat);
    }

    /// <summary>
    /// Texte en clair. Une valeur non chiffrée est rendue telle quelle ; une valeur indéchiffrable (clé absente ou différente)
    /// aussi, pour ne jamais l'écraser lors d'un enregistrement.
    /// </summary>
    public static string? Dechiffrer(string? stocke)
    {
        if (stocke is null || _cle is null || !EstChiffre(stocke)) return stocke;
        try
        {
            var octets = Convert.FromBase64String(stocke[Prefixe.Length..]);
            if (octets.Length < TailleNonce + TailleEtiquette) return stocke;
            var longueur = octets.Length - TailleNonce - TailleEtiquette;
            var clair = new byte[longueur];
            using var aes = new AesGcm(_cle, TailleEtiquette);
            aes.Decrypt(octets.AsSpan(0, TailleNonce), octets.AsSpan(TailleNonce, longueur), octets.AsSpan(TailleNonce + longueur), clair);
            return Encoding.UTF8.GetString(clair);
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return stocke;
        }
    }
}
