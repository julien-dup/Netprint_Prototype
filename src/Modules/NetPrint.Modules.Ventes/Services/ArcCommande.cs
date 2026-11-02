using System.Text;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Socle.Documents;

namespace NetPrint.Modules.Ventes.Services;

// =====================================================================
// ARC d'une commande (ARC105P) : décodage des canevas ARC010F / ARC015F, lignes, totaux. Fonctions pures : les
// données viennent de ArcCommandeDb2, la mise en page de Impressions/DocumentArc.
// Écarts volontaires avec le RPG (rapport ARC, § 6) :
//   - canevas : repli sur le français si la langue du client n'a pas de canevas (le RPG imprimait un ARC sans en-tête) ;
//   - client facturé : sans client facturé, les variables $CRSF… reprennent le client (condition inversée dans le RPG) ;
//   - lignes dans l'ordre de leur n°, regroupées par livraison (adresse, date, à la demande) ; devise de la commande
//     sans conversion (seuls euro et franc étaient gérés) ;
//   - variables remplacées par leur valeur sans blancs de fin (le § sert toujours à réaligner).
// =====================================================================
internal static partial class ArcCommande
{
    // Variable $CRS (raison sociale du client), pas $CRSF (client facturé)
    [System.Text.RegularExpressions.GeneratedRegex("[$£]CRS(?!F)")]
    private static partial System.Text.RegularExpressions.Regex AdresseClient();

    // ------------------------------------------------------------------
    // Canevas : « $CODE » (ou « £CODE ») remplacé par sa valeur ; le code lu fait 4 caractères (un code de 3 lettres
    // absorbe le caractère qui suit, comme ARC105P) ; un code inconnu est supprimé. « § » reprend l'écriture à sa
    // colonne dans le canevas (réalignement après une valeur longue). 74 colonnes au plus.
    // ------------------------------------------------------------------
    internal const int LargeurCanevas = 74;

    internal static string Decoder(string ligne, IReadOnlyDictionary<string, string> variables)
    {
        var sortie = new StringBuilder();
        for (var i = 0; i < ligne.Length; i++)
        {
            var c = ligne[i];
            if (c is '$' or '£' && i + 1 < ligne.Length)
            {
                var code = ligne.Substring(i + 1, Math.Min(4, ligne.Length - i - 1));
                if (variables.TryGetValue(code.Trim(), out var valeur)) sortie.Append(valeur);
                i += code.Length;
                continue;
            }
            if (c == '§')
            {
                if (sortie.Length > i) sortie.Length = i;
                else sortie.Append(' ', i - sortie.Length);
                continue;
            }
            sortie.Append(c);
        }
        var texte = sortie.ToString().TrimEnd();
        return texte.Length > LargeurCanevas ? texte[..LargeurCanevas] : texte;
    }

    // Lignes d'un chapitre décodées ; blancs de début et de fin retirés, suites de lignes vides réduites à une
    internal static List<LigneTexte> Chapitre(IEnumerable<LigneCanevas> canevas, string chapitre, IReadOnlyDictionary<string, string> variables)
    {
        var lignes = canevas.Where(l => l.Chapitre == chapitre).OrderBy(l => l.Numero)
            .Select(l => new LigneTexte(Decoder(l.Texte, variables), l.Gras)).ToList();
        var resultat = new List<LigneTexte>();
        foreach (var l in lignes)
            if (l.Texte != "" || resultat.Count > 0 && resultat[^1].Texte != "") resultat.Add(l);
        while (resultat.Count > 0 && resultat[^1].Texte == "") resultat.RemoveAt(resultat.Count - 1);
        return resultat;
    }

    // Variables connues d'ARC105P pour une commande de négoce ou de produit fini (les zones de fabrication restent vides)
    internal static Dictionary<string, string> Variables(EnteteArc e, Libelles l, DateOnly aujourdhui)
    {
        string D(DateOnly? d) => d?.ToString("dd/MM/yy") ?? "";
        var facture = e.ClientFacture ?? e.Client;
        var v = new Dictionary<string, string>
        {
            ["GDT1"] = D(aujourdhui),
            ["GDT2"] = $"{aujourdhui.Day} {l.Mois[aujourdhui.Month - 1]} {aujourdhui.Year}",
            ["CFJ"] = e.Client.FormeJuridique, ["CRS"] = e.Client.Nom, ["CRU1"] = e.Client.Rue1, ["CRU2"] = e.Client.Rue2,
            ["CCP"] = e.Client.CodePostal, ["CVIL"] = e.Client.Ville, ["CPAY"] = e.Client.Pays,
            ["CTEL"] = e.Telephone, ["CFAX"] = e.Fax, ["CLMR"] = e.ModeReglement,
            ["CECH"] = e.JoursEcheance == 0 ? "" : e.JoursEcheance.ToString(),
            ["CLE"] = e.JourEcheance == 0 ? "" : e.JourEcheance.ToString(),
            ["CARR"] = e.Arrete >= 30 ? l.FinDeMois : e.Arrete == 0 ? "" : e.Arrete.ToString(),
            ["LREG"] = Reglement(e, l), ["CRFF"] = e.ReferenceFournisseur,
            ["ECL"] = e.CompteClient.ToString(), ["ECLF"] = (e.CompteClientFacture == 0 ? e.CompteClient : e.CompteClientFacture).ToString(),
            ["CFJF"] = facture.FormeJuridique, ["CRSF"] = facture.Nom, ["CR1F"] = facture.Rue1, ["CR2F"] = facture.Rue2,
            ["CCPF"] = facture.CodePostal, ["CVLF"] = facture.Ville,
            ["EDD"] = D(e.DateDossier), ["EDCC"] = D(e.DateCommandeClient),
            ["EIA"] = e.InitialesFabricant, ["ELIA"] = e.NomFabricant, ["EPRF"] = e.Profil, ["ELPR"] = e.NomProfil,
            ["EDS1"] = e.Designation1, ["EDS2"] = e.Designation2,
            ["ECRP"] = e.Representant == 0 ? "" : e.Representant.ToString(), ["ELRP"] = e.NomRepresentant,
            ["ELXR"] = e.NomRepresentantEtendu != "" ? e.NomRepresentantEtendu : e.NomRepresentant,
            ["ERFI"] = e.ReferenceImprime, ["ERFC"] = e.ReferenceCommande, ["EINT"] = e.Interlocuteur,
            ["EDOS"] = e.Cle.Libelle,
            ["ETYL"] = e.Enlevement == "E" ? "Enlèvement Client" : "A Livrer",
            ["ETYP"] = e.Port switch { "F" => "Franco", "D" => "Dû", "A" => "Avancé", _ => "" },
            ["EDLL"] = e.SemaineLivraison != "" ? e.SemaineLivraison : D(e.DateLivraison),
            ["ENF"] = e.TypeFacturation == "2" ? "1 Facture" : "",
            ["ECUV"] = e.Unite,
        };
        return v;
    }

    // $LREG : « Traite 60 jours fin de mois le 10 », « Virement au comptant »
    internal static string Reglement(EnteteArc e, Libelles l)
    {
        var texte = e.ModeReglement.Trim();
        texte += e.JoursEcheance != 0 ? $" {e.JoursEcheance} {l.Jours}" : $" {l.AuComptant}";
        if (e.Arrete >= 30) texte += $" {l.FinDeMois}";
        if (e.JourEcheance != 0) texte += $" {l.Le} {e.JourEcheance}";
        return texte.Trim();
    }

    // ------------------------------------------------------------------
    // Lignes imprimées (SPSEL / SPDET) : vente avec quantité, port, texte ; remises exclues. Quantité : commandée si
    // les lignes soldées sont demandées, sinon reste à livrer (lignes soldées exclues).
    // ------------------------------------------------------------------
    internal static List<GroupeArc> Groupes(IReadOnlyList<LigneArc> lignes, bool avecSoldees, bool dateButoir, Libelles l)
    {
        var groupes = new List<GroupeArc>();
        var retenues = lignes
            .Where(x => x.TypeLigne is "V" or "P" or "E" or "T")
            .Where(x => avecSoldees || !x.Soldee)
            .Select(x => (Ligne: x, Quantite: QuantiteImprimee(x, avecSoldees)))
            .Where(x => x.Ligne.TypeLigne != "V" || x.Quantite > 0)
            .OrderBy(x => x.Ligne.Numero);
        // Une ligne texte suit la livraison de la ligne qui la précède (elle la commente)
        (string, DateOnly?, bool)? precedente = null;
        var avecCle = new List<((string, DateOnly?, bool) Cle, LigneArc Ligne, int Quantite)>();
        foreach (var (x, q) in retenues)
        {
            var cle = x.TypeLigne == "T" && precedente is { } p ? p
                : (x.AdresseLivraison?.UneLigne ?? "", x.ALaDemande ? null : x.DateLivraison, x.ALaDemande);
            avecCle.Add((cle, x, q));
            precedente = cle;
        }
        foreach (var grp in avecCle.GroupBy(x => x.Cle))
        {
            var premiere = grp.First(x => x.Ligne.TypeLigne != "T" || grp.All(y => y.Ligne.TypeLigne == "T")).Ligne;
            var titre = premiere.ALaDemande
                ? l.ALaDemande + (dateButoir && premiere.DateLivraison is { } db ? $" · date butoir {db:dd/MM/yyyy}" : "")
                : premiere.DateLivraison is { } d ? $"{l.PourLe} {d:dd/MM/yyyy}" : "";
            groupes.Add(new GroupeArc(titre, premiere.AdresseLivraison?.UneLigne ?? "", grp.Select(x => Ligne(x.Ligne, x.Quantite, l)).ToList()));
        }
        return groupes;
    }

    internal static int QuantiteImprimee(LigneArc x, bool avecSoldees)
    {
        var q = avecSoldees ? x.QuantiteCommandee : x.QuantiteCommandee - x.QuantiteLivree;
        return x.TypeLigne == "P" ? Math.Max(0, q) : q;
    }

    internal static LigneImprimee Ligne(LigneArc x, int quantite, Libelles l)
    {
        if (x.TypeLigne == "T")
            return new LigneImprimee("", null, x.Designation, null, "", null, null, x.Textes.Where(t => t != "").ToList());

        // Montant : quantité × prix / diviseur de l'unité ; « Le tout » (999999999) ou sans diviseur : montant de la ligne
        var montant = x.Tnum is 0 or MoteurPrixCommande.TnumLeTout ? x.Montant : MoteurPrixCommande.Montant(x.PrixNet, quantite, x.Tnum);
        decimal qte = quantite;
        var prix = x.PrixNet;
        var unite = x.Gratuite ? "" : x.LibelleUnite;
        var designation = x.Designation;
        var complements = new List<string>();
        // Vente par conditionnement : quantité en conditionnements, prix du conditionnement
        // ÉCART : le prix du conditionnement tient compte du diviseur de l'unité (ARC105P multipliait le prix au mille
        // par l'emballage)
        var parConditionnement = x.ParConditionnement && x.Emballage > 0;
        decimal PrixConditionnement(decimal p) => x.Tnum is 0 or MoteurPrixCommande.TnumLeTout ? p * x.Emballage : p * x.Emballage / x.Tnum;
        if (parConditionnement)
        {
            qte = (decimal)quantite / x.Emballage;
            prix = PrixConditionnement(prix);
            unite = x.Gratuite ? "" : l.UniteConditionnement;
            designation = $"{(x.TypeEmballage == "" ? "Conditionnement" : x.TypeEmballage)} de {x.Emballage:N0} ex.";
            complements.Add(x.Designation);
        }
        string? avantRemise = x.Remise != 0 && x.PrixBrut != 0
            ? $"({DocumentsPdf.Prix(parConditionnement ? PrixConditionnement(x.PrixBrut) : x.PrixBrut)}  - {x.Remise:0.##} %)"
            : null;

        complements.AddRange(x.Textes.Where(t => t != ""));
        complements.AddRange(x.MentionsNorme.Where(t => t != ""));
        if (x.NumeroAppel != "")
            complements.Add($"{l.NumeroAppel} {x.NumeroAppel}" + (x.DateAppel is { } da ? $" {l.Du} {da:dd/MM/yyyy}" : ""));
        if (x.ReferenceClient != "")
            complements.Add($"{l.VotreReference} {x.ReferenceClient}" + (x.Marche != "" ? $" - {x.Marche}" : ""));
        if (x.ClientFactureLigne != "") complements.Add($"{l.Facturation} {x.ClientFactureLigne}");
        var article = x.Indice == "" ? x.Article : $"{x.Article} {x.Indice}";
        return new LigneImprimee(article, qte, designation, prix, unite, montant, avantRemise, complements);
    }

    // Totaux : somme des lignes imprimées, remise de pied AB001.CTXRM
    internal static (decimal Brut, decimal Net) Totaux(IEnumerable<GroupeArc> groupes, decimal tauxRemise)
    {
        var brut = groupes.SelectMany(g => g.Lignes).Sum(x => x.Montant ?? 0);
        var net = tauxRemise == 0 ? brut : Math.Round(brut * (100 - tauxRemise) / 100, 2, MidpointRounding.AwayFromZero);
        return (brut, net);
    }

    // Canevas d'un chapitre : recherche élargie d'ARC105P (type, catégorie, sous-catégorie → … → tout à blanc), dans la
    // langue du client puis en français
    internal static List<LigneCanevas> Canevas(IReadOnlyList<LigneCanevas> tous, string typeProduit, string categorie, string sousCategorie,
        string langue, string chapitre)
    {
        (string, string, string)[] cles =
            [(typeProduit, categorie, sousCategorie), (typeProduit, categorie, ""), (typeProduit, "", ""), ("", categorie, ""), ("", "", "")];
        foreach (var lang in langue == "" ? [""] : new[] { langue, "" })
            foreach (var (t, c, s) in cles)
            {
                var lignes = tous.Where(x => x.Chapitre == chapitre && x.Langue == lang && x.TypeProduit == t && x.Categorie == c && x.SousCategorie == s).ToList();
                if (lignes.Count > 0) return lignes;
            }
        return [];
    }

    // ------------------------------------------------------------------
    // Message d'envoi : objet d'ARC105CLP (« Accusé de réception de votre commande <réf.> du <date> ») pour une commande,
    // d'EnvoiARC (« ARC du client <nom> ») pour plusieurs ; texte = chapitre A du canevas (variables de la 1re commande)
    // ------------------------------------------------------------------
    internal const string TexteParDefaut = "Veuillez trouver ci-joint notre accusé de réception de commande.\n\n" +
        "Nous restons à votre entière disposition pour tout renseignement complémentaire.\n\nLe service commercial";

    internal static (string Objet, string Texte) Message(IReadOnlyList<DonneesArc> commandes, DateOnly aujourdhui)
    {
        var e = commandes[0].Entete;
        string objet;
        if (commandes.Count > 1) objet = $"ARC du client {e.Client.RaisonSociale}";
        else if (e.ReferenceCommande != "")
            objet = $"Accusé de réception de votre commande {e.ReferenceCommande}" +
                    (e.DateCommandeClient is { } dc ? $" du {dc:dd/MM/yyyy}" : "");
        else objet = $"Accusé de réception de la commande {e.Cle.Libelle}";

        var d = commandes[0];
        var lignes = Chapitre(Canevas(d.Canevas, e.TypeProduit, e.Categorie, e.SousCategorie, e.Langue, "A"), "A",
            Variables(e, Libelles.Pour(d.Traductions), aujourdhui));
        var texte = lignes.Count > 0 ? string.Join("\n", lignes.Select(l => l.Texte.TrimEnd())) : TexteParDefaut;
        return (objet, texte);
    }

    public static ModeleArc Construire(DonneesArc d, bool avecSoldees, DateOnly aujourdhui)
    {
        var e = d.Entete;
        var l = Libelles.Pour(d.Traductions);
        var variables = Variables(e, l, aujourdhui);
        List<LigneCanevas> Brut(string chapitre) => Canevas(d.Canevas, e.TypeProduit, e.Categorie, e.SousCategorie, e.Langue, chapitre);
        List<LigneTexte> Chap(string chapitre) => Chapitre(Brut(chapitre), chapitre, variables);
        // Canevas qui place lui-même l'adresse du client ($CRS) : c'est l'en-tête complet de la lettre, il remplace les blocs
        // « références » et « destinataire » (sinon ils seraient imprimés deux fois)
        var enteteParCanevas = Brut("1").Concat(Brut("2")).Any(x => AdresseClient().IsMatch(x.Texte));
        var groupes = Groupes(d.Lignes, avecSoldees, d.DateButoir, l);
        var (brut, net) = Totaux(groupes, e.TauxRemisePied);

        var references = new List<(string, string)>
        {
            (l.VotreCommande, e.ReferenceCommande + (e.DateCommandeClient is { } dc ? $" {l.Du} {dc:dd/MM/yyyy}" : "")),
            ("Interlocuteur", e.Interlocuteur),
            ("N° client", $"{e.CompteGeneral} {e.CompteClient}"),
            ("Représentant", e.NomRepresentantEtendu != "" ? e.NomRepresentantEtendu : e.NomRepresentant),
            ("Livraison", (e.Enlevement == "E" ? "Enlèvement client" : "À livrer") + (e.Port switch { "F" => " · franco", "D" => " · port dû", "A" => " · port avancé", _ => "" })),
            ("Règlement", Reglement(e, l)),
        };
        if (e.ClientFacture is { } f && e.CompteClientFacture != 0 && e.CompteClientFacture != e.CompteClient)
            references.Add((l.Facturation.TrimEnd(':', ' '), f.UneLigne));

        return new ModeleArc(
            Numero: e.Cle.Libelle, Date: aujourdhui, Destinataire: e.Client, References: references.Where(r => r.Item2.Trim() != "").ToList(),
            Introduction: [.. Chap("1"), .. Chap("2")],
            TexteHaut: d.PositionTexte == "H" ? d.TexteComplementaire : [], Groupes: groupes,
            TexteBas: d.PositionTexte != "H" ? d.TexteComplementaire : [],
            TotalBrut: brut, TauxRemise: e.TauxRemisePied, NetHt: net, Devise: e.Monnaie == "F" ? "francs" : "euros",
            Pied: Chap("5"), Cadre: d.Cadre, EnteteParCanevas: enteteParCanevas, L: l);
    }
}

// ----------------------------------------------------------------------
// Données lues (ArcCommandeDb2) et modèle mis en page (DocumentArc)
// ----------------------------------------------------------------------
internal sealed record AdresseArc(string FormeJuridique, string Nom, string Rue1, string Rue2, string CodePostal, string Ville, string Pays = "")
{
    public string RaisonSociale => $"{FormeJuridique} {Nom}".Trim();
    public string UneLigne => string.Join(", ", new[] { RaisonSociale, Rue1, Rue2, $"{CodePostal} {Ville}".Trim() }.Where(x => x != ""));
    public IEnumerable<string> Lignes => new[] { RaisonSociale, Rue1, Rue2, $"{CodePostal} {Ville}".Trim(), Pays }.Where(x => x != "");
}

internal sealed record EnteteArc(
    CleCommande Cle, int CompteGeneral, int CompteClient, string Langue, string Enseigne, string TypeProduit, string Categorie,
    string SousCategorie, AdresseArc Client, AdresseArc? ClientFacture, int CompteClientFacture, string Telephone, string Fax,
    string ReferenceFournisseur, string ModeReglement, int JoursEcheance, int JourEcheance, int Arrete, string ReferenceCommande,
    DateOnly? DateCommandeClient, DateOnly? DateDossier, string Interlocuteur, int Representant, string NomRepresentant,
    string NomRepresentantEtendu, string InitialesFabricant, string NomFabricant, string Profil, string NomProfil, string Designation1,
    string Designation2, string ReferenceImprime, string Enlevement, string Port, DateOnly? DateLivraison, string SemaineLivraison,
    string TypeFacturation, string Unite, string Monnaie, decimal TauxRemisePied, string Email);

internal sealed record LigneArc(
    int Numero, string TypeLigne, bool ALaDemande, string Article, string Indice, string Designation, int QuantiteCommandee,
    int QuantiteLivree, bool Soldee, decimal PrixNet, decimal PrixBrut, decimal Remise, decimal Montant, long Tnum, string LibelleUnite,
    bool Gratuite, bool ParConditionnement, int Emballage, string TypeEmballage, DateOnly? DateLivraison, AdresseArc? AdresseLivraison,
    string NumeroAppel, DateOnly? DateAppel, IReadOnlyList<string> Textes, IReadOnlyList<string> MentionsNorme, string ReferenceClient,
    string Marche, string ClientFactureLigne);

internal sealed record LigneCanevas(string TypeProduit, string Categorie, string SousCategorie, string Chapitre, string Langue, int Numero,
    string Texte, bool Gras);

internal sealed record DonneesArc(
    EnteteArc Entete, IReadOnlyList<LigneArc> Lignes, IReadOnlyList<LigneCanevas> Canevas, IReadOnlyList<string> TexteComplementaire,
    string PositionTexte, bool AvecSoldeesParDefaut, bool DateButoir, bool Cadre, IReadOnlyList<string> Traductions);

internal sealed record LigneTexte(string Texte, bool Gras);

internal sealed record LigneImprimee(string Article, decimal? Quantite, string Designation, decimal? Prix, string Unite, decimal? Montant,
    string? PrixAvantRemise, IReadOnlyList<string> Complements);

internal sealed record GroupeArc(string Titre, string Livraison, IReadOnlyList<LigneImprimee> Lignes);

internal sealed record ModeleArc(
    string Numero, DateOnly Date, AdresseArc Destinataire, IReadOnlyList<(string Libelle, string Valeur)> References,
    IReadOnlyList<LigneTexte> Introduction, IReadOnlyList<string> TexteHaut, IReadOnlyList<GroupeArc> Groupes, IReadOnlyList<string> TexteBas,
    decimal TotalBrut, decimal TauxRemise, decimal NetHt, string Devise, IReadOnlyList<LigneTexte> Pied, bool Cadre,
    bool EnteteParCanevas, Libelles L);

// Libellés de l'ARC : français, ou traductions PRT008 (programme « ARC ») de la langue du client
internal sealed record Libelles(
    string Article, string Quantite, string Designation, string PrixUnitaire, string Montant, string NetAPayer, string Unite,
    string Livraison, string VotreReference, string NumeroAppel, string ASuivre, string Facturation, string Du, string ALaDemande,
    string Suite, string Commande, string Jours, string AuComptant, string Le, string FinDeMois, IReadOnlyList<string> Mois,
    string PourLe, string VotreCommande, string UniteConditionnement, string Titre)
{
    public static readonly Libelles Francais = new(
        "Article", "Quantité", "Désignation", "P.U.", "Montant", "Net HT", "Unité", "Livraison :", "V/Réf :", "N° appel", "à suivre…",
        "Facturation :", "du", "À la demande", "Suite", "Commande n°", "jours", "au comptant", "le", "fin de mois",
        ["janvier", "février", "mars", "avril", "mai", "juin", "juillet", "août", "septembre", "octobre", "novembre", "décembre"],
        "Pour le", "Votre commande", "Unité", "Accusé de réception de commande");

    // PRT008 : 1 Quantité, 2 Désignation, 3 Prix, 4 Montant, 5 Net à payer, 6 Unité, 7 Livraison, 8 Notre réf. (article),
    // 9 Date, 10 V/Réf, 11 N° appel, 12 à suivre, 13 Facturation, 14 du, 15 À la demande, 16 Suite, 17 Commande,
    // 18 jours, 19 au comptant, 20 le, 21 fin de mois, 22-33 mois
    public static Libelles Pour(IReadOnlyList<string> t)
    {
        if (t.Count < 33 || t.All(x => x == "")) return Francais;
        string T(int i, string defaut) => t[i - 1] != "" ? t[i - 1] : defaut;
        var f = Francais;
        return new Libelles(T(8, f.Article), T(1, f.Quantite), T(2, f.Designation), T(3, f.PrixUnitaire), T(4, f.Montant),
            T(5, f.NetAPayer), T(6, f.Unite), T(7, f.Livraison), T(10, f.VotreReference), T(11, f.NumeroAppel), T(12, f.ASuivre),
            T(13, f.Facturation), T(14, f.Du), T(15, f.ALaDemande), T(16, f.Suite), T(17, f.Commande), T(18, f.Jours),
            T(19, f.AuComptant), T(20, f.Le), T(21, f.FinDeMois), [.. Enumerable.Range(22, 12).Select((i, m) => T(i, f.Mois[m]))],
            T(9, f.PourLe), T(10, f.VotreCommande).TrimEnd(':', ' '), f.UniteConditionnement,
            // PRT008 n'a pas de titre : titre anglais pour des traductions anglaises, sinon français
            t[16].Equals("Order", StringComparison.OrdinalIgnoreCase) ? "Order acknowledgement" : f.Titre);
    }
}
