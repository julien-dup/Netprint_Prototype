using Microsoft.Extensions.Configuration;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Facturation.Donnees;

// =====================================================================
// Droits PRT000P1 d'un programme pour le profil (le profil prime sur son groupe AI00F) ; null = aucun paramétrage.
// Même lecture que Ventes et Logistique (OprDb2) : à regrouper dans le Noyau (docs/points-en-attente.md).
// =====================================================================
internal sealed class DroitsFacturationDb2(IConfiguration config)
{
    private string Bib => BaseDb2.Bibliotheque(config);

    public async Task<IReadOnlyList<string>?> OptionsProgrammeAsync(string programme, string profil, CancellationToken ct)
    {
        profil = profil.ToUpperInvariant();
        var options = $"SELECT AOPT01, AOPT02, AOPT03, AOPT04, AOPT05, AOPT06, AOPT07, AOPT08 FROM {Bib}.PRT000P1 WHERE APGM = ?";
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        await using (var rd = await Lecteur.ExecuterAsync(cnx, $"{options} AND AUSR = ? FETCH FIRST 1 ROW ONLY", ct, programme, profil))
            if (await rd.ReadAsync(ct)) return Options(rd);
        var groupe = "";
        await using (var rd = await Lecteur.ExecuterAsync(cnx, $"SELECT AGROUP FROM {Bib}.AI00F WHERE RPRF = ? FETCH FIRST 1 ROW ONLY", ct, profil))
            if (await rd.ReadAsync(ct)) groupe = rd.Texte(0);
        if (groupe == "") return null;
        await using (var rd = await Lecteur.ExecuterAsync(cnx, $"{options} AND AGROUP = ? FETCH FIRST 1 ROW ONLY", ct, programme, groupe))
            if (await rd.ReadAsync(ct)) return Options(rd);
        return null;

        static List<string> Options(Lecteur rd) => Enumerable.Range(0, 8).Select(rd.Texte).ToList();
    }
}
