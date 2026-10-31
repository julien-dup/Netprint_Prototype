using System.Data.Common;
using System.Text;
using Microsoft.Extensions.Configuration;
using NetPrint.Socle.Donnees;
using NetPrint.Socle.Session;

namespace NetPrint.Noyau.ListesDeChoix;

// =====================================================================
// Listes de choix (recherches F4 / bouton « … ») des référentiels partagés par tous les modules.
// Reprise des fenêtres FEN_Rec* du projet WinDev ComposantsExternes, en SQL paramétré.
// La société vient toujours de la session.
//
// Recherche par mots (comme les FEN_Rec* WinDev) : chaque mot doit figurer dans une des zones de recherche ;
// un mot terminé par « * » doit COMMENCER une zone (« DUP* »). Les grandes listes (clients, prospects,
// dossiers, papiers, matières, fournisseurs) demandent un mot ; les petites s'affichent en entier.
// =====================================================================
public enum ListeChoix
{
    Client, Prospect, Profil, Categorie, SousCategorie, Fabricant, Representant, Norme, Dossier,
    Papier, Sorte, SousSorte, Coloris, MachineTirage, Section, Operation, Matiere, Fournisseur,
    OperationSousTraitance, MotifEchec, Article,
}

public interface IListesDeChoix
{
    // Parent : catégorie (sous-catégorie), sorte (sous-sorte), section (opération), fournisseur (opération STR),
    // compte général et compte client séparés par un blanc (article : articles de ce client seulement, facultatif)
    Task<ResultatChoix> RechercherAsync(ListeChoix liste, string? recherche, string? parent = null, CancellationToken ct = default);
}

public sealed record ColonneChoix(string Libelle, bool Nombre = false);

// Codes : valeurs renvoyées au champ (client : compte général puis auxiliaire) ; Valeurs : une par colonne.
// Inactif : ligne affichée en rouge (client fermé…).
public sealed record LigneChoix(IReadOnlyList<string> Codes, IReadOnlyList<string> Valeurs, bool Inactif = false)
{
    public string Code => Codes.Count > 0 ? Codes[0] : "";
}

public sealed record ResultatChoix(
    string Titre,
    IReadOnlyList<ColonneChoix> Colonnes,
    IReadOnlyList<LigneChoix> Lignes,
    bool RechercheObligatoire,       // grande liste : rien tant qu'aucun mot n'est saisi
    bool Tronque,
    string? Message = null);

internal sealed class ListesDeChoixDb2(IConfiguration config, IUtilisateurCourant utilisateur) : IListesDeChoix
{
    private const int Maximum = 200;

    private sealed record Definition(
        string Titre, ColonneChoix[] Colonnes, bool RechercheObligatoire,
        Func<string, Requete> Sql,                // bibliothèque → requête de base (WHERE déjà ouvert)
        string[] ZonesRecherche,                  // zones texte comparées aux mots
        string? ZoneNumero,                       // zone comparée à un mot entièrement numérique
        string Tri,
        int NbCodes = 1,
        string? ZoneParent = null,
        bool ParentNumerique = false,
        int? ColonneInactif = null);              // index de la colonne SQL qui marque une ligne inactive (≠ 0)

    private sealed record Requete(string Select, bool AvecSociete = true);

    private static readonly Dictionary<ListeChoix, Definition> Definitions = new()
    {
        [ListeChoix.Client] = new("Choix d'un client",
            [new("Compte", true), new("Aux.", true), new("Mot directeur"), new("Nom"), new("CP"), new("Ville"), new("Téléphone")],
            true, b => new($"SELECT RGEN, RCLI, CMODIR, CNOM, CPOSTA, CVILLE, CTELEP, CDFERM FROM {b}.AA0003 WHERE RSOC = ? AND CTAG <> '*'"),
            ["CNOM", "CMODIR", "CTELEP", "CVILLE", "CPOSTA"], "RCLI", "CNOM", NbCodes: 2, ColonneInactif: 7),
        [ListeChoix.Prospect] = new("Choix d'un prospect",
            [new("N°", true), new("Mot directeur"), new("Raison sociale"), new("CP"), new("Ville")],
            true, b => new($"SELECT RPROSP, DMODIR, DRS, DPOSTA, DVILLE FROM {b}.AX00F WHERE RSOC = ? AND DTAG <> '*'"),
            ["DRS", "DMODIR", "DTELEP", "DVILLE", "DPOSTA"], "RPROSP", "DRS"),
        [ListeChoix.Profil] = new("Choix d'un profil",
            [new("Profil"), new("Nom")],
            false, b => new($"SELECT RPRF, ACNOM FROM {b}.AI00F WHERE 1 = 1", AvecSociete: false),
            ["RPRF", "ACNOM"], null, "RPRF"),
        [ListeChoix.Categorie] = new("Choix d'une catégorie",
            [new("Catégorie"), new("Libellé")],
            false, b => new($"SELECT RCAT, TLIB1 FROM {b}.AA0009 WHERE RSOC = ? AND TTAG <> '*' AND RCAT <> '$1' AND RCAT <> '$2'"),
            ["RCAT", "TLIB1"], null, "RCAT"),
        [ListeChoix.SousCategorie] = new("Choix d'une sous-catégorie",
            [new("Sous-catégorie"), new("Catégorie"), new("Libellé"), new("Libellé 2")],
            false, b => new($"SELECT RSCAT, RCAT, TLIB1, TLIB2 FROM {b}.AE0003 WHERE RSOC = ? AND TTAG <> '*'"),
            ["RSCAT", "TLIB1", "TLIB2"], null, "RCAT, RSCAT", ZoneParent: "RCAT"),
        [ListeChoix.Fabricant] = new("Choix d'un fabricant",
            [new("Initiales"), new("Nom")],
            false, b => new($"SELECT ACINI, ACNOM FROM {b}.AI00F WHERE ACFAB = 'O'", AvecSociete: false),
            ["ACINI", "ACNOM"], null, "ACINI"),
        [ListeChoix.Representant] = new("Choix d'un représentant",
            [new("N°", true), new("Nom")],
            false, b => new($"SELECT RREPR, INOM FROM {b}.B000F WHERE RSOC = ? AND ITAG <> '*'"),
            ["INOM"], "RREPR", "RREPR"),
        [ListeChoix.Norme] = new("Choix d'une norme",
            [new("Norme"), new("Désignation")],
            false, b => new($"SELECT DISTINCT NOCOD, NODES FROM {b}.NOR010F WHERE RSOC = ?"),
            ["NOCOD", "NODES"], null, "NOCOD"),
        [ListeChoix.Dossier] = new("Choix d'un dossier (OF)",
            [new("Dossier", true), new("Clé"), new("Désignation"), new("Client"), new("Date"), new("Réf. commande")],
            true, b => new($"""
                SELECT a.RDOSS, a.CCDCLE, a.CCDES1, b.CMODIR,
                       DIGITS(a.CCDATJ) || '/' || DIGITS(a.CCDATM) || '/' || DIGITS(a.CCDATA), a.CCREFC
                FROM {b}.AB001 a LEFT OUTER JOIN {b}.AA0003 b ON a.RSOC = b.RSOC AND a.RGEN = b.RGEN AND a.RCLI = b.RCLI
                WHERE a.RSOC = ?
                """),
            ["a.CCDES1", "b.CMODIR", "a.CCREFC"], "a.RDOSS", "a.RDOSS DESC"),
        [ListeChoix.Papier] = new("Choix d'un papier",
            [new("Papier", true), new("Sorte"), new("Sous-sorte"), new("Libellé"), new("Grammage", true), new("Coloris"),
             new("Laize", true), new("Hauteur", true)],
            true, b => new($"""
                SELECT a.RPAPIE, a.FSORTE, a.FSSORT, c.TLIB1, a.FGRAMM, a.FCOLOR, a.FLAIZE, a.FHAUT
                FROM {b}.B7000F a
                LEFT OUTER JOIN {b}.AE0004 c ON a.RSOC = c.RSOC AND a.FSORTE = c.RSORTE AND a.FSSORT = c.RSSORT
                WHERE a.RSOC = ? AND a.FTAG = ' '
                """),
            ["a.FSORTE", "a.FSSORT", "c.TLIB1", "a.FCOLOR"], "a.RPAPIE", "a.RPAPIE"),
        [ListeChoix.Sorte] = new("Choix d'une sorte", [new("Sorte"), new("Libellé")], false,
            b => new($"SELECT RARG, TLIB1 FROM {b}.AA00081 WHERE RSOC = ? AND TTAG <> '*' AND RTABLE = 'SP'"),
            ["RARG", "TLIB1", "TLIBR"], null, "RARG"),
        [ListeChoix.Coloris] = new("Choix d'un coloris", [new("Coloris"), new("Libellé")], false,
            b => new($"SELECT RARG, TLIB1 FROM {b}.AA00081 WHERE RSOC = ? AND TTAG <> '*' AND RTABLE = 'KL'"),
            ["RARG", "TLIB1", "TLIBR"], null, "RARG"),
        [ListeChoix.MotifEchec] = new("Choix d'un motif d'échec", [new("Motif"), new("Libellé")], false,
            b => new($"SELECT RARG, TLIB1 FROM {b}.AA00081 WHERE RSOC = ? AND TTAG <> '*' AND RTABLE = 'ME'"),
            ["RARG", "TLIB1", "TLIBR"], null, "RARG"),
        [ListeChoix.SousSorte] = new("Choix d'une sous-sorte",
            [new("Sous-sorte"), new("Sorte"), new("Libellé"), new("Libellé 2")],
            false, b => new($"SELECT RSSORT, RSORTE, TLIB1, TLIB2 FROM {b}.AE0004 WHERE RSOC = ? AND TTAG <> '*'"),
            ["RSSORT", "TLIB1", "TLIB2"], null, "RSORTE, RSSORT", ZoneParent: "RSORTE"),
        [ListeChoix.MachineTirage] = new("Choix d'une machine de tirage", [new("Section"), new("Libellé")], false,
            b => new($"SELECT RSECT, RSLIB FROM {b}.C800F WHERE RSOC = ? AND RSTAG <> '*' AND RSMCHT = 'O'"),
            ["RSECT", "RSLIB"], null, "RSECT"),
        [ListeChoix.Section] = new("Choix d'une machine (section)", [new("Section"), new("Libellé")], false,
            b => new($"SELECT RSECT, RSLIB FROM {b}.C800F WHERE RSOC = ? AND RSTAG <> '*' AND RREEL = 'O'"),
            ["RSECT", "RSLIB"], null, "RSECT"),
        [ListeChoix.Operation] = new("Choix d'une opération", [new("Opération", true), new("Libellé")], false,
            b => new($"SELECT b.ROPE, b.OLIB FROM {b}.D000F b WHERE b.RSOC = ?"),
            ["b.OLIB"], "b.ROPE", "b.ROPE"),
        [ListeChoix.Matiere] = new("Choix d'une matière",
            [new("Matière", true), new("Libellé"), new("Famille"), new("Sous-famille")],
            true, b => new($"SELECT RMATIE, GNOM, GFAMIL, GSSFAM FROM {b}.C000F WHERE RSOC = ? AND GTAG <> '*'"),
            ["GNOM", "GDES2"], "RMATIE", "GNOM"),
        [ListeChoix.Fournisseur] = new("Choix d'un fournisseur",
            [new("N°", true), new("Mot directeur"), new("Raison sociale"), new("Ville")],
            true, b => new($"SELECT RFOURN, PMOT, PRS, PVILLE FROM {b}.B500F WHERE RSOC = ? AND PTAG <> '*'"),
            ["PRS", "PMOT", "PVILLE"], "RFOURN", "PRS"),
        [ListeChoix.OperationSousTraitance] = new("Choix d'une opération de sous-traitance",
            [new("Opération", true), new("Libellé"), new("Prix fixe", true), new("Prix prop.", true), new("Qté prop.", true)],
            false, b => new($"SELECT ROPE, HLIB, HPRFIX, HPRPRO, HQTEPR FROM {b}.AZ00F WHERE RSOC = ? AND HTAG <> '*'"),
            ["HLIB"], "ROPE", "ROPE", ZoneParent: "RFOURN", ParentNumerique: true),
        // Articles (FEN_RecArticle) : code, désignation ou référence imprimée ; remplacés ou clôturés (AREMPL C / O) en rouge
        [ListeChoix.Article] = new("Choix d'un article",
            [new("Article"), new("Indice"), new("Désignation"), new("Réf. imprimée"), new("Client")],
            true, b => new($"""
                SELECT a.AARTIC, a.AINDIC, a.ADES, a.ARFIMP, c.CMODIR, CASE WHEN a.AREMPL IN ('C', 'O') THEN 1 ELSE 0 END
                FROM {b}.SPF010 a LEFT OUTER JOIN {b}.AA0003 c ON c.RSOC = a.RSOC AND c.RGEN = a.AGEN AND c.RCLI = a.ACLI
                WHERE a.RSOC = ?
                """),
            ["a.AARTIC", "a.ADES", "a.ARFIMP"], null, "a.AARTIC, a.AINDIC", NbCodes: 2, ColonneInactif: 5),
    };

    public async Task<ResultatChoix> RechercherAsync(ListeChoix liste, string? recherche, string? parent = null, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var d = Definitions[liste];
        var mots = (recherche ?? "").ToUpperInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => m.Replace("%", "").Replace("_", ""))
            .Where(m => m.Length > 0 && m != "*")
            .ToList();

        if (d.RechercheObligatoire && mots.Count == 0)
            return new(d.Titre, d.Colonnes, [], true, false, "Saisissez un ou plusieurs mots puis cliquez sur Rechercher.");
        if (d.ZoneParent is not null && d.ParentNumerique && !int.TryParse(parent, out _))
            return new(d.Titre, d.Colonnes, [], d.RechercheObligatoire, false,
                "Renseignez d'abord le fournisseur : les opérations de sous-traitance dépendent du fournisseur.");

        var b = BaseDb2.Bibliotheque(config);
        var requete = d.Sql(b);
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        await using var cmd = cnx.CreateCommand();
        var sql = new StringBuilder(requete.Select);
        if (requete.AvecSociete) cmd.Parameters.Valeur(u.Societe);

        // Liste dépendante : sous-catégories d'une catégorie, sous-sortes d'une sorte, opérations d'une section…
        if (d.ZoneParent is not null && !string.IsNullOrWhiteSpace(parent))
        {
            sql.Append($" AND {d.ZoneParent} = ?");
            cmd.Parameters.Valeur(d.ParentNumerique ? int.Parse(parent) : parent.Trim().ToUpperInvariant());
        }
        // Articles d'un client : compte général et compte client (« 411100 376 ») ; sans client, tous les articles
        if (liste == ListeChoix.Article && (parent ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var gen, var cli]
            && int.TryParse(gen, out var compteGeneral) && int.TryParse(cli, out var compteClient))
        {
            sql.Append(" AND a.AGEN = ? AND a.ACLI = ?");
            cmd.Parameters.Valeur(compteGeneral);
            cmd.Parameters.Valeur(compteClient);
        }
        // Opérations d'une section : D100F (comme FEN_RechercheOperations)
        if (liste == ListeChoix.Operation && !string.IsNullOrWhiteSpace(parent))
        {
            sql.Append($" AND b.ROPE IN (SELECT ROPE FROM {b}.D100F WHERE RSOC = b.RSOC AND RSECT = ?)");
            cmd.Parameters.Valeur(parent.Trim().ToUpperInvariant());
        }

        foreach (var mot in mots)
        {
            var debut = mot.EndsWith('*');
            var texte = mot.TrimEnd('*');
            var conditions = d.ZonesRecherche.Select(z => $"UPPER({z}) LIKE ?").ToList();
            var valeurs = d.ZonesRecherche.Select(_ => (object)(debut ? $"{texte}%" : $"%{texte}%")).ToList();
            if (d.ZoneNumero is not null && long.TryParse(texte, out var n))
            {
                conditions.Add($"{d.ZoneNumero} = ?");
                valeurs.Add(n);
            }
            sql.Append(" AND (").Append(string.Join(" OR ", conditions)).Append(')');
            foreach (var v in valeurs) cmd.Parameters.Valeur(v);
        }
        sql.Append($" ORDER BY {d.Tri} FETCH FIRST {Maximum + 1} ROWS ONLY");
        cmd.CommandText = sql.ToString();

        var lignes = new List<LigneChoix>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var nbColonnes = d.Colonnes.Length;
        while (await rd.ReadAsync(ct))
        {
            var valeurs = new string[nbColonnes];
            for (var i = 0; i < nbColonnes; i++) valeurs[i] = Formater(rd, i);
            var inactif = d.ColonneInactif is { } ci && rd.Entier(ci) != 0;
            lignes.Add(new LigneChoix(valeurs.Take(d.NbCodes).ToArray(), valeurs, inactif));
        }
        var tronque = lignes.Count > Maximum;
        if (tronque) lignes.RemoveAt(Maximum);
        return new(d.Titre, d.Colonnes, lignes, d.RechercheObligatoire, tronque,
            lignes.Count == 0 ? "Aucune ligne ne correspond." : null);
    }

    // Nombres sans zéros inutiles (« 80 » et non « 80.000 »), textes sans les blancs AS400
    private static string Formater(DbDataReader rd, int i)
    {
        if (rd.IsDBNull(i)) return "";
        var v = rd.GetValue(i);
        return v is decimal d ? d.ToString("0.###", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"))
            : Convert.ToString(v)?.Trim() ?? "";
    }
}
