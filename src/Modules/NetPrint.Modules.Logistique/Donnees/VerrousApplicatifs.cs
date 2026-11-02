using System.Data.Odbc;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Logistique.Donnees;

// Verrous applicatifs PRTLCK (fichier « BL », « DOSSIER », « OPR »…), vus par les écrans AS/400 et WinDev. Clé unique
// (PLFIC, PLCLE) : l'insertion échoue si le verrou est déjà posé, même par une validation simultanée.
internal static class VerrousApplicatifs
{
    public static async Task PoserAsync(OdbcConnection cnx, string bib, string fichier, string cle, string profil, string message, CancellationToken ct)
    {
        try
        {
            await using var cmd = cnx.CreateCommand();
            cmd.CommandText = $"INSERT INTO {bib}.PRTLCK (PLFIC, PLCLE, PLPROF) VALUES (?, ?, ?)";
            cmd.Parameters.Valeur(fichier);
            cmd.Parameters.Valeur(cle);
            cmd.Parameters.Valeur(profil);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        // Clé en double (SQL0803) : le pilote IBM i renvoie 23000 ou 23505 selon les cas
        catch (OdbcException ex) when (ex.Errors.Count > 0 && ex.Errors[0].SQLState is "23505" or "23000")
        {
            var detenteur = "un autre poste";
            await using (var rd = await Lecteur.ExecuterAsync(cnx, $"SELECT PLPROF FROM {bib}.PRTLCK WHERE PLFIC = ? AND PLCLE = ?", ct, fichier, cle))
                if (await rd.ReadAsync(ct)) detenteur = rd.Texte(0);
            throw new RegleMetierException($"{message} {detenteur}.");
        }
    }

    public static async Task LeverAsync(OdbcConnection cnx, string bib, string fichier, string cle, string profil)
    {
        try
        {
            await using var cmd = cnx.CreateCommand();
            cmd.CommandText = $"DELETE FROM {bib}.PRTLCK WHERE PLFIC = ? AND PLCLE = ? AND PLPROF = ?";
            cmd.Parameters.Valeur(fichier);
            cmd.Parameters.Valeur(cle);
            cmd.Parameters.Valeur(profil);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (OdbcException)
        {
            // verrou orphelin : se supprime comme ceux des écrans AS/400
        }
    }
}
