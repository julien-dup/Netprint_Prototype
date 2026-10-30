using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NetPrint.Socle.Donnees;
using NetPrint.Socle.Session;

namespace NetPrint.Noyau.Societes;

// Sociétés (AA0007)
public interface ISocietes
{
    // Nom réduit de la société (AA0007.SRS) ; null si la société n'existe pas
    Task<string?> LireNomAsync(int societe, CancellationToken ct = default);

    // Identité de la société émettrice des documents (en-tête, mentions légales, logo de l'enseigne) ; null si absente
    Task<IdentiteSociete?> LireIdentiteAsync(int societe, string enseigne = "", CancellationToken ct = default);
}

// Société émettrice (AA0007). Le logo vient du dossier configuré Documents:Logos ({société}-{enseigne}.png, puis
// {société}.png ; .jpg accepté) : les logos de l'IBM i sont des overlays d'impression, inutilisables hors du 5250.
public sealed record IdentiteSociete(
    int Societe, string RaisonSociale, string FormeJuridique, string Rue1, string Rue2, string CodePostal, string Ville,
    string Telephone, string Email, string SiteWeb, string Siren, string Siret, string Ape, string Capital, byte[]? Logo)
{
    // « SAS au capital de 160 000 € · SIRET 000000002 00024 · APE 222C »
    public string MentionsLegales => string.Join(" · ", new[]
    {
        (FormeJuridique + (Capital == "" ? "" : $" au capital de {CapitalFormate} €")).Trim(),
        Siren == "" ? "" : Siret == "" ? $"SIREN {Siren}" : $"SIRET {Siren} {Siret}",
        Ape == "" ? "" : $"APE {Ape}",
    }.Where(x => x != ""));

    private string CapitalFormate => decimal.TryParse(Capital.Replace(',', '.'), System.Globalization.NumberStyles.Number,
        System.Globalization.CultureInfo.InvariantCulture, out var c)
        ? c.ToString("#,##0.##", System.Globalization.CultureInfo.GetCultureInfo("fr-FR")) : Capital;
}

internal sealed class SocietesDb2(IConfiguration config, IHostEnvironment env) : ISocietes
{
    public async Task<string?> LireNomAsync(int societe, CancellationToken ct = default)
    {
        // Mode démo sans IBM i configuré : nom fictif
        if (ModeDemo.Actif(config, env) && string.IsNullOrWhiteSpace(config.GetConnectionString(BaseDb2.NomChaine)))
            return $"SOCIÉTÉ {societe} (démo)";

        var bib = BaseDb2.Bibliotheque(config);
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"SELECT SRS FROM {bib}.AA0007 WHERE RSOC = ?";
        cmd.Parameters.Valeur(societe);
        var res = await cmd.ExecuteScalarAsync(ct);
        return res is null ? null : res is DBNull ? "" : Convert.ToString(res)!.Trim();
    }

    public async Task<IdentiteSociete?> LireIdentiteAsync(int societe, string enseigne = "", CancellationToken ct = default)
    {
        if (ModeDemo.Actif(config, env) && string.IsNullOrWhiteSpace(config.GetConnectionString(BaseDb2.NomChaine)))
            return new IdentiteSociete(societe, $"SOCIÉTÉ {societe} (démo)", "SAS", "1 rue de l'Exemple", "", "75000", "PARIS",
                "01 00 00 00 00", "", "", "", "", "", "", Logo(societe, enseigne));

        var bib = BaseDb2.Bibliotheque(config);
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"""
            SELECT SRS, SFJ, SRUE1, SRUE2, SPOST, SVILLE, STEL, SEMAIL, SADWEB, SIREN, SIRET, SAPE, SCAPIT
            FROM {bib}.AA0007 WHERE RSOC = ?
            """;
        cmd.Parameters.Valeur(societe);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        string T(int i) => rd.IsDBNull(i) ? "" : Convert.ToString(rd.GetValue(i))!.Trim();
        return new IdentiteSociete(societe, T(0), T(1), T(2), T(3), T(4), T(5), T(6), T(7), T(8), T(9), T(10), T(11), T(12),
            Logo(societe, enseigne));
    }

    private byte[]? Logo(int societe, string enseigne)
    {
        if (config["Documents:Logos"] is not { Length: > 0 } dossier || !Directory.Exists(dossier)) return null;
        var noms = enseigne.Trim() == "" ? new[] { $"{societe}" } : [$"{societe}-{enseigne.Trim()}", $"{societe}"];
        foreach (var nom in noms)
            foreach (var ext in new[] { ".png", ".jpg" })
                if (Path.Combine(dossier, nom + ext) is var chemin && File.Exists(chemin)) return LireLogo(chemin);
        return null;
    }

    // Logos gardés en mémoire tant que le fichier ne change pas (l'aperçu d'ARC est régénéré à chaque modification)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Date, byte[] Contenu)> Logos = new();

    private static byte[] LireLogo(string chemin)
    {
        var date = File.GetLastWriteTimeUtc(chemin);
        if (Logos.TryGetValue(chemin, out var l) && l.Date == date) return l.Contenu;
        var contenu = File.ReadAllBytes(chemin);
        Logos[chemin] = (date, contenu);
        return contenu;
    }
}
