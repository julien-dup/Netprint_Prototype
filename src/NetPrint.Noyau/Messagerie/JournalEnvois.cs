using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Socle.Donnees;

namespace NetPrint.Noyau.Messagerie;

// =====================================================================
// Journal des envois de documents (table ENV100, script db/db2/20261030-2230_ENV100_journal_envois.sql) : une ligne
// par document envoyé (ou préparé pour Outlook), avec le PDF tel que le client l'a reçu.
// Tant que la table n'existe pas dans la bibliothèque, l'écriture est ignorée et la lecture renvoie une liste vide :
// l'envoi ne dépend jamais du journal.
// =====================================================================
public interface IJournalEnvois
{
    Task EcrireAsync(IReadOnlyList<EnvoiDocument> envois, CancellationToken ct = default);
    Task<IReadOnlyList<EnvoiJournalise>> LireAsync(int societe, string type, int dossier, string cle, CancellationToken ct = default);
    Task<(string Nom, byte[] Contenu)?> LirePdfAsync(int societe, long id, string type, int dossier, string cle, CancellationToken ct = default);

    // Documents sans dossier (BL…) : repérés par leur numéro (ENNDOC), RDOSS reste à 0
    Task<IReadOnlyList<EnvoiJournalise>> LireDocumentAsync(int societe, string type, int document, CancellationToken ct = default);
    Task<(string Nom, byte[] Contenu)?> LirePdfDocumentAsync(int societe, long id, string type, int document, CancellationToken ct = default);
}

public static class TypesEnvoi
{
    public const string Arc = "ARC";
    public const string Bl = "BL";
}

public static class CanauxEnvoi
{
    public const string Smtp = "S";
    public const string Outlook = "O";
}

public static class StatutsEnvoi
{
    public const string Envoye = "E";
    public const string Test = "T";
    public const string Echec = "X";
    public const string Prepare = "P";
}

public sealed record EnvoiDocument(
    int Societe, string Message, string Profil, string Type, int Dossier, string Cle, int CompteGeneral, int CompteClient, string Canal,
    string Statut, string Expediteur, IReadOnlyList<string> Destinataires, IReadOnlyList<string> Copies, string Redirection, string Objet,
    string Erreur, string NomPdf, byte[]? Pdf, int Document = 0);

public sealed record EnvoiJournalise(long Id, DateTime Date, string Profil, string Canal, string Statut, string Expediteur,
    string Destinataires, string Copies, string Redirection, string Objet, string Erreur, string NomPdf, bool AvecPdf);

internal sealed class JournalEnvoisDb2(IConfiguration config) : IJournalEnvois
{
    private string Bib => BaseDb2.Bibliotheque(config);

    public async Task EcrireAsync(IReadOnlyList<EnvoiDocument> envois, CancellationToken ct = default)
    {
        if (envois.Count == 0) return;
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        try
        {
            foreach (var e in envois)
            {
                await using var cmd = cnx.CreateCommand();
                // ENNDOC seulement pour un document sans dossier : l'ARC s'écrit même sans le script 20261031-1000
                var document = e.Document > 0 ? ", ENNDOC" : "";
                var marque = e.Document > 0 ? ", ?" : "";
                cmd.CommandText = $"""
                    INSERT INTO {Bib}.ENV100 (RSOC, ENMSG, ENPROF, ENTYPE, RDOSS, ENDCLE, RGEN, RCLI, ENCANAL, ENSTAT, ENEXP, ENDEST, ENCOPI,
                        ENREDI, ENOBJ, ENERR, ENNOM, ENPDF{document})
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?{marque})
                    """;
                cmd.Parameters.Valeur(e.Societe);
                cmd.Parameters.Valeur(e.Message);
                cmd.Parameters.Valeur(Coupe(e.Profil.ToUpperInvariant(), 10, " "));
                cmd.Parameters.Valeur(e.Type);
                cmd.Parameters.Valeur(e.Dossier);
                cmd.Parameters.Valeur(e.Cle == "" ? " " : e.Cle);
                cmd.Parameters.Valeur(e.CompteGeneral);
                cmd.Parameters.Valeur(e.CompteClient);
                cmd.Parameters.Valeur(e.Canal);
                cmd.Parameters.Valeur(e.Statut);
                cmd.Parameters.Valeur(Coupe(e.Expediteur, 100));
                cmd.Parameters.Valeur(Coupe(string.Join("; ", e.Destinataires), 1000));
                cmd.Parameters.Valeur(Coupe(string.Join("; ", e.Copies), 1000));
                cmd.Parameters.Valeur(Coupe(e.Redirection, 100));
                cmd.Parameters.Valeur(Coupe(e.Objet, 300));
                cmd.Parameters.Valeur(Coupe(e.Erreur, 500));
                cmd.Parameters.Valeur(Coupe(e.NomPdf, 100));
                if (e.Pdf is { } pdf) cmd.Parameters.Binaire(pdf);
                else cmd.Parameters.Add(new OdbcParameter { OdbcType = OdbcType.VarBinary, Value = DBNull.Value });
                if (e.Document > 0) cmd.Parameters.Valeur(e.Document);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        catch (OdbcException ex) when (TableAbsente(ex))
        {
            // Journal pas encore créé dans cette bibliothèque : envoi sans trace
        }
    }

    public Task<IReadOnlyList<EnvoiJournalise>> LireAsync(int societe, string type, int dossier, string cle, CancellationToken ct = default) =>
        LireAsync(societe, type, "RDOSS = ? AND ENDCLE = ?", [dossier, cle == "" ? " " : cle], ct);

    public Task<IReadOnlyList<EnvoiJournalise>> LireDocumentAsync(int societe, string type, int document, CancellationToken ct = default) =>
        LireAsync(societe, type, "ENNDOC = ?", [document], ct);

    public Task<(string Nom, byte[] Contenu)?> LirePdfAsync(int societe, long id, string type, int dossier, string cle,
        CancellationToken ct = default) =>
        LirePdfAsync(societe, id, type, "RDOSS = ? AND ENDCLE = ?", [dossier, cle == "" ? " " : cle], ct);

    public Task<(string Nom, byte[] Contenu)?> LirePdfDocumentAsync(int societe, long id, string type, int document, CancellationToken ct = default) =>
        LirePdfAsync(societe, id, type, "ENNDOC = ?", [document], ct);

    // filtre : texte fixe de ce fichier (jamais une saisie), valeurs en paramètres
    private async Task<IReadOnlyList<EnvoiJournalise>> LireAsync(int societe, string type, string filtre, object[] valeurs, CancellationToken ct)
    {
        var liste = new List<EnvoiJournalise>();
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        try
        {
            await using var cmd = cnx.CreateCommand();
            cmd.CommandText = $"""
                SELECT ENID, ENDATE, ENPROF, ENCANAL, ENSTAT, ENEXP, ENDEST, ENCOPI, ENREDI, ENOBJ, ENERR, ENNOM,
                       CASE WHEN ENPDF IS NULL THEN 0 ELSE 1 END
                FROM {Bib}.ENV100 WHERE RSOC = ? AND ENTYPE = ? AND {filtre}
                ORDER BY ENDATE DESC FETCH FIRST {NetPrint.Socle.LimitesRecherche.Maximum} ROWS ONLY
                """;
            cmd.Parameters.Valeur(societe);
            cmd.Parameters.Valeur(type);
            foreach (var v in valeurs) cmd.Parameters.Valeur(v);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                liste.Add(new EnvoiJournalise(Convert.ToInt64(rd.GetValue(0)), rd.GetDateTime(1), rd.Texte(2), rd.Texte(3), rd.Texte(4),
                    rd.Texte(5), rd.Texte(6), rd.Texte(7), rd.Texte(8), rd.Texte(9), rd.Texte(10), rd.Texte(11), rd.Entier(12) == 1));
        }
        catch (OdbcException ex) when (Absente(ex, filtre))
        {
        }
        return liste;
    }

    private async Task<(string Nom, byte[] Contenu)?> LirePdfAsync(int societe, long id, string type, string filtre, object[] valeurs,
        CancellationToken ct)
    {
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        try
        {
            await using var cmd = cnx.CreateCommand();
            cmd.CommandText = $"""
                SELECT ENNOM, ENPDF FROM {Bib}.ENV100
                WHERE RSOC = ? AND ENID = ? AND ENTYPE = ? AND {filtre} AND ENPDF IS NOT NULL
                """;
            cmd.Parameters.Valeur(societe);
            cmd.Parameters.Valeur(id);
            cmd.Parameters.Valeur(type);
            foreach (var v in valeurs) cmd.Parameters.Valeur(v);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct)) return (rd.Texte(0), (byte[])rd.GetValue(1));
        }
        catch (OdbcException ex) when (Absente(ex, filtre))
        {
        }
        return null;
    }

    private static bool TableAbsente(OdbcException ex) => ex.Errors.Count > 0 && ex.Errors[0].SQLState is "42704" or "42S02";

    // Lecture par n° de document avant le script 20261031-1000 (colonne ENNDOC absente) : historique vide
    private static bool Absente(OdbcException ex, string filtre) =>
        TableAbsente(ex) || (filtre.StartsWith("ENNDOC") && ex.Errors.Count > 0 && ex.Errors[0].SQLState is "42703" or "42S22");

    private static string Coupe(string v, int n, string vide = "")
    {
        v = (v ?? "").Trim();
        if (v == "") return vide;
        return v.Length > n ? v[..n] : v;
    }
}
