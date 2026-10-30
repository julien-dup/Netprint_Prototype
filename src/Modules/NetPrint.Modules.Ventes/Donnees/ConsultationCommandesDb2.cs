using System.Data.Common;
using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Ventes.Donnees;

// Accès aux données de la consultation des commandes : interface pour tester le service sans base
internal interface ISourceCommandes
{
    Task<int?> RepresentantImposeAsync(int societe, string profil, CancellationToken ct);

    // Les 8 options PRT000P1 d'un programme pour le profil (le profil prime sur son groupe) ; null = aucun paramétrage
    Task<IReadOnlyList<string>?> OptionsProgrammeAsync(string programme, string profil, CancellationToken ct);

    Task<IReadOnlyList<LigneCommandeBrute>> RechercherAsync(int societe, CriteresCommandes criteres, CancellationToken ct);
}

// Ligne lue telle quelle (une ligne NEG100 et son en-tête AB001 ; zones de ligne à vide si la commande n'a pas de ligne)
internal sealed record LigneCommandeBrute(
    int Rdoss, string Ccdcle, string Cctypc, int Rgen, int Rcli, string Cnom, string NomFacture, string Ccrefc,
    string Ccdes1, string Ccdes2, DateOnly? DateOuverture, DateOnly? DateCommandeClient, DateOnly? DateLivraisonEntete,
    DateOnly? DateSolde, string Cctags, int Ccrep, string NomRepresentant, string Ccinat, string Ccense,
    int Dnnlig, string Dnart, string Dnind, string Arfimp, string Dndesa, string Dnstat, int Dnlori, string Dnappe,
    int Dndliv, int Dnqtec, int Dnqtel, int Dnqtef, int Dnqter, decimal Dnpus, string Dnuv, string Dnopt2, string Dnopt5,
    long Tnum, string Dnnorm, int Amodel, string Afam, string Asfam, string Dnsold, int Dnccom, string Texte, int NbDocuments);

internal sealed class ConsultationCommandesDb2(IConfiguration config) : ISourceCommandes
{
    private string Bib => BaseDb2.Bibliotheque(config);

    // Restriction représentant (B000F, comme InitVarGlobales de HistoriqueDossiers et ConsultationDevis)
    public async Task<int?> RepresentantImposeAsync(int societe, string profil, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var cmd = Commande(cnx,
            $"SELECT RREPR, ILIMIT FROM {Bib}.B000F WHERE RSOC = ? AND IPROF = ? AND ITAG <> '*' FETCH FIRST 1 ROW ONLY",
            societe, profil.ToUpperInvariant());
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return rd.Texte(1) == "O" ? rd.Entier(0) : null;
    }

    public async Task<IReadOnlyList<string>?> OptionsProgrammeAsync(string programme, string profil, CancellationToken ct)
    {
        var b = Bib;
        profil = profil.ToUpperInvariant();
        const string options = "AOPT01, AOPT02, AOPT03, AOPT04, AOPT05, AOPT06, AOPT07, AOPT08";
        await using var cnx = await OuvrirAsync(ct);
        await using (var cmd = Commande(cnx, $"SELECT {options} FROM {b}.PRT000P1 WHERE APGM = ? AND AUSR = ? FETCH FIRST 1 ROW ONLY",
                         programme, profil))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            if (await rd.ReadAsync(ct)) return Options(rd);

        var groupe = "";
        await using (var cmd = Commande(cnx, $"SELECT AGROUP FROM {b}.AI00F WHERE RPRF = ? FETCH FIRST 1 ROW ONLY", profil))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            if (await rd.ReadAsync(ct)) groupe = rd.Texte(0);
        if (groupe == "") return null;

        await using (var cmd = Commande(cnx, $"SELECT {options} FROM {b}.PRT000P1 WHERE APGM = ? AND AGROUP = ? FETCH FIRST 1 ROW ONLY",
                         programme, groupe))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            if (await rd.ReadAsync(ct)) return Options(rd);
        return null;

        static List<string> Options(DbDataReader rd) => Enumerable.Range(0, 8).Select(rd.Texte).ToList();
    }

    public async Task<IReadOnlyList<LigneCommandeBrute>> RechercherAsync(int societe, CriteresCommandes criteres, CancellationToken ct)
    {
        var requete = RequeteListeCommandes.Construire(Bib, societe, criteres);
        await using var cnx = await OuvrirAsync(ct);
        await using var cmd = Commande(cnx, requete.Sql, [.. requete.Parametres]);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var lignes = new List<LigneCommandeBrute>();
        while (await rd.ReadAsync(ct)) lignes.Add(Lire(rd));
        return lignes;
    }

    // Ordre des colonnes : RequeteListeCommandes.Colonnes
    private static LigneCommandeBrute Lire(DbDataReader rd)
    {
        var textes = Enumerable.Range(51, RequeteListeCommandes.NbTextes).Select(rd.Texte).Where(t => t != "");
        return new(
            Rdoss: rd.Entier(0), Ccdcle: rd.Texte(1), Cctypc: rd.Texte(2), Rgen: rd.Entier(3), Rcli: rd.Entier(4),
            Cnom: rd.Texte(5), NomFacture: rd.Texte(6), Ccrefc: rd.Texte(7), Ccdes1: rd.Texte(8), Ccdes2: rd.Texte(9),
            DateOuverture: BaseDb2.Date(rd.Entier(10), rd.Entier(11), rd.Entier(12)),
            DateCommandeClient: BaseDb2.Date(rd.Entier(13), rd.Entier(14), rd.Entier(15)),
            DateLivraisonEntete: BaseDb2.Date(rd.Entier(16), rd.Entier(17), rd.Entier(18)),
            DateSolde: BaseDb2.Date(rd.Entier(19), rd.Entier(20), rd.Entier(21)),
            Cctags: rd.Texte(22), Ccrep: rd.Entier(23), NomRepresentant: rd.Texte(24), Ccinat: rd.Texte(25), Ccense: rd.Texte(26),
            Dnnlig: rd.Entier(27), Dnart: rd.Texte(28), Dnind: rd.Texte(29), Arfimp: rd.Texte(30), Dndesa: rd.Texte(31),
            Dnstat: rd.Texte(32), Dnlori: rd.Entier(33), Dnappe: rd.Texte(34), Dndliv: rd.Entier(35),
            Dnqtec: rd.Entier(36), Dnqtel: rd.Entier(37), Dnqtef: rd.Entier(38), Dnqter: rd.Entier(39),
            Dnpus: rd.Nombre(40), Dnuv: rd.Texte(41), Dnopt2: rd.Texte(42), Dnopt5: rd.Texte(43),
            Tnum: rd.IsDBNull(44) ? 0 : Convert.ToInt64(rd.GetValue(44)),
            Dnnorm: rd.Texte(45), Amodel: rd.Entier(46), Afam: rd.Texte(47), Asfam: rd.Texte(48), Dnsold: rd.Texte(49),
            Dnccom: rd.Entier(50), Texte: string.Join("\n", textes), NbDocuments: rd.Entier(51 + RequeteListeCommandes.NbTextes));
    }

    private async Task<OdbcConnection> OuvrirAsync(CancellationToken ct)
    {
        var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        return cnx;
    }

    private static OdbcCommand Commande(OdbcConnection cnx, string sql, params object?[] valeurs)
    {
        var cmd = cnx.CreateCommand();
        cmd.CommandText = sql;
        foreach (var v in valeurs) cmd.Parameters.Valeur(v);
        return cmd;
    }
}
