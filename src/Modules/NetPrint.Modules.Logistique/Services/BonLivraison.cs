using System.Globalization;
using NetPrint.Modules.Logistique.Donnees;

namespace NetPrint.Modules.Logistique.Services;

// =====================================================================
// Règles de l'édition du BL (EXP035P2) : données lues → modèle du document. Fonctions pures, testées sans base ni PDF.
// Présentation choisie par la fiche client (AA0003.CENVBL) : '1' (défaut) lignes par élément ; '2' « PRINTOR » lignes de
// tout le BL regroupées par article, avec la commande du client sur chaque ligne. BL revendeur (EXP009.HREV = 'O') : le
// client revendeur est l'expéditeur, sans logo ni identité de la société, ni représentant ni « notre n° CEE ».
// ÉCARTS voulus (docs/points-en-attente.md) :
//  - BL non chiffré, comme toutes les variantes RPG ;
//  - quantité en unités de conditionnement seulement si elle tombe juste (le RPG perdait le reste de la division) ;
//  - pas de traduction des libellés (PRT008/PRT009), de code-barres des lots ni de fiche sous-références (EXP035P4) ;
//  - les options (logo, adresse du client, mention) ne « collent » plus d'un BL à l'autre (indicateurs RPG non remis à zéro).
// =====================================================================
// Expediteur : lignes du bloc expéditeur d'un BL revendeur (à la place de la société), sinon null
internal sealed record ModeleBl(
    IReadOnlyList<string>? Expediteur, int Numero, bool Retour, DateOnly? Date, string Representant, string RefFournisseur, IReadOnlyList<string> Destinataire,
    IReadOnlyList<string> Observations, IReadOnlyList<(string Libelle, string Valeur)> Transport, IReadOnlyList<string> Introduction,
    IReadOnlyList<GroupeBl> Groupes, IReadOnlyList<string> TextesPied, int PoidsTotal, int ColisTotal, int PalettesTotal);

// Un élément du BL : titre (commande), lignes, textes de fin (fournisseur, textes techniques)
internal sealed record GroupeBl(string Titre, IReadOnlyList<LigneImprimeeBl> Lignes, IReadOnlyList<string> Textes);

internal sealed record LigneImprimeeBl(
    string Reference, IReadOnlyList<string> Complements, string Quantite, IReadOnlyList<string> Designations, string Code,
    IReadOnlyList<string> Colis, string Poids, IReadOnlyList<string> Details);

internal static class BonLivraison
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static ModeleBl Construire(DonneesEditionBl d)
    {
        var e = d.Entete;
        var premier = d.Elements.FirstOrDefault();

        var destinataire = new[] { e.RaisonSociale, e.Rue1, e.Rue2, $"{e.CodePostal} {e.Ville}".Trim(), e.Pays }.Where(x => x != "").ToList();

        var transport = new List<(string, string)>();
        if (e.TypePort != "" || e.Incoterm != "") transport.Add(("Port", $"{TypePort(e.TypePort)} {e.Incoterm}".Trim()));
        if (e.TypeLivraison != "") transport.Add(("Livraison", TypeLivraison(e.TypeLivraison)));
        if (e.Transporteur != "")
            transport.Add(("Transporteur", string.Join(" · ", new[] { e.Transporteur, e.TransporteurVille.Trim(),
                e.TransporteurTelephone == "" ? "" : $"Tél. {e.TransporteurTelephone}" }.Where(x => x != ""))));
        if (e.RefExportSociete != "" && !e.Revendeur) transport.Add(("Notre n° CEE", e.RefExportSociete));
        if (e.RegimeClient == "C" && e.RefExportClient != "") transport.Add(("Votre n° CEE", e.RefExportClient));

        // Adresse du client donneur d'ordre (EXP005.FEOPT2) pour un BL client ou vierge, puis la mention du BL
        var introduction = new List<string>();
        if (e.AdresseClientEnTete && !e.Revendeur && premier is { Etat: "C" or "V" } && e.ClientNom != "")
        {
            introduction.Add($"Commande du client : {e.ClientNom}");
            introduction.AddRange(new[] { e.ClientRue1, e.ClientRue2, $"{e.ClientCodePostal} {e.ClientVille}".Trim() }.Where(x => x != ""));
        }
        if (e.Mention != "") introduction.Add(e.Mention);

        // PRINTOR : les produits fabriqués gardent leur ligne, le reste du BL est regroupé par article
        var groupes = e.Presentation == "2"
            ? [.. d.Elements.Where(el => el.TypeBl == 1).Select(el => Groupe(d, el)), .. GroupesParArticle(d)]
            : d.Elements.Select(el => Groupe(d, el)).ToList();

        // Revendeur : son adresse comme expéditeur, sauf pour un BL de sous-traitance (EXP035P2 garde alors le logo)
        IReadOnlyList<string>? expediteur = e.Revendeur && premier?.Etat != "S"
            ? new[] { e.ClientNom, e.ClientRue1, e.ClientRue2, $"{e.ClientCodePostal} {e.ClientVille}".Trim(),
                    e.ClientTelephone == "" ? "" : $"Tél. {e.ClientTelephone}", e.ClientFax == "" ? "" : $"Fax {e.ClientFax}", e.ClientEmail }
                .Where(x => x != "").ToList()
            : null;

        // Retour et représentant : ceux du premier élément, comme EXP035P2. Totaux d'EXP009 (non recalculés), sauf sur un BL
        // partiel (représentant limité) où ils ne correspondraient pas aux lignes imprimées.
        return new ModeleBl(expediteur, e.NumeroBl, premier?.Etat == "R", e.DateExpedition,
            premier is { Representant: not 0 } r && !e.Revendeur ? $"{r.Representant} {r.NomRepresentant}".Trim() : "",
            e.RefFournisseur, destinataire, e.Observations.Where(x => x != "").ToList(), transport, introduction, groupes,
            d.TextesPied, d.Partiel ? 0 : e.PoidsTotal, d.Partiel ? 0 : e.ColisTotal, d.Partiel ? 0 : e.PalettesTotal);
    }

    private static GroupeBl Groupe(DonneesEditionBl d, ElementEditionBl el)
    {
        var lignes = d.Lignes.Where(l => l.Element == el.Numero).ToList();
        var normes = d.Entete.Revendeur ? [] : Normes(d, el.EnseigneCommande, el.NormeCommande);
        var dossier = el.Dossier == 0 ? "" : $"{el.Dossier:00000}/{el.Cle}";
        var textes = new List<string>();
        if (el.Etat == "S") textes.Add($"Fournisseur n° {d.Entete.CompteClient}");
        textes.AddRange(el.TextesTechniques);

        // BL de produit fabriqué (HTYPBL = 1) : une ligne pour l'élément, désignations des lignes EXP015
        if (el.TypeBl == 1)
        {
            var (quantite, uc) = Quantite(el.QuantiteLivree, el.QuantiteEnUc, el.EmballageCommande, "UC");
            var designations = new List<string>();
            if (uc != "") designations.Add(uc);
            if (el.RefImprime != "") designations.Add($"Réf. imp. : {el.RefImprime}");
            if (el.RefCommande != "")
                designations.Add($"Réf. cde : {el.RefCommande}" + (el.DateCommande is { } dc ? $" du {dc.ToString("dd/MM/yyyy", Fr)}" : ""));
            designations.AddRange(lignes.SelectMany(l => l.Designations));
            var ligne = new LigneImprimeeBl(dossier, [], quantite, designations, EtatLivraison(el.EtatLivraison),
                ColisElement(el), el.Poids == 0 ? "" : el.Poids.ToString(Fr), normes);
            return new GroupeBl("", [ligne], textes);
        }

        var titre = dossier == "" ? "" : $"Commande {dossier}" + (el.QuantiteLivree != 0 ? $" · {el.QuantiteLivree.ToString("#,##0", Fr)} ex." : "");
        return new GroupeBl(titre, [.. lignes.Where(l => l.Article == "" || l.Imprimee).Select(l => Ligne(d, el, l))], textes);
    }

    // Présentation PRINTOR (Sppres2) : toutes les lignes du BL triées par article, indice, appel, commande ; un titre par
    // article (référence et référence client), mentions de norme en fin d'article. Les lignes de texte ne sont imprimées
    // que sur un retour, comme le RPG.
    private static IEnumerable<GroupeBl> GroupesParArticle(DonneesEditionBl d)
    {
        var elements = d.Elements.Where(el => el.TypeBl != 1).ToDictionary(el => el.Numero);
        var lignes = d.Lignes.Where(l => elements.ContainsKey(l.Element))
            .Where(l => l.Article == "" ? elements[l.Element].Etat == "R" : l.Imprimee)
            .OrderBy(l => l.Article, StringComparer.Ordinal).ThenBy(l => l.Indice, StringComparer.Ordinal)
            .ThenBy(l => l.Appel, StringComparer.Ordinal).ThenBy(l => elements[l.Element].Dossier).ThenBy(l => elements[l.Element].Cle);
        foreach (var article in lignes.GroupBy(l => (l.Article, l.Indice)))
        {
            var premiere = article.First();
            var titre = "";
            if (premiere.Article != "")
            {
                var (reference, complement) = References(d, premiere);
                titre = complement == "" ? reference : $"{reference} · {complement}";
            }
            var imprimees = article.Select(l =>
            {
                var el = elements[l.Element];
                if (l.Article == "") return new LigneImprimeeBl("", [], "", l.Designations, "", [], "", []);
                var commande = (el.RefCommandeClient, el.DateCommandeClient) switch
                {
                    ("", null) => "",
                    ("", { } dc) => $"Cde du {dc.ToString("dd/MM/yyyy", Fr)}",
                    (var rc, null) => $"V/cde : {rc}",
                    (var rc, { } dc) => $"V/cde : {rc} du {dc.ToString("dd/MM/yyyy", Fr)}",
                };
                commande = (commande == "" ? "" : commande + " - ") + $"N/cde {el.Dossier}/{el.Cle}";
                var details = new List<string> { commande };
                details.AddRange(Appel(l));
                details.AddRange(el.TextesTechniques);
                if (d.Entete.ImprimerLots) details.AddRange(Lots(d.Lots.Where(x => x.Element == l.Element && x.Ligne == l.Ligne), l.UniteLots));
                var (quantite, uc) = Quantite(l.Quantite, l.QuantiteEnUc, l.EmballageArticle, Emballage(l.TypeEmballage, "UC"));
                return new LigneImprimeeBl("", [], quantite, [.. uc == "" ? Array.Empty<string>() : [uc], .. l.Designations],
                    EtatLivraison(l.EtatLivraison), ColisLigne(l), Poids(l), details);
            }).ToList();
            var normes = article.Where(l => l.Norme != "")
                .SelectMany(l => Normes(d, elements[l.Element].EnseigneCommande, l.Norme)).Distinct().ToList();
            yield return new GroupeBl(titre, imprimees, normes);
        }
    }

    // Référence imprimée d'un article et son complément : code article et référence du client (EXP005.FEDART), n° de marché
    private static (string Reference, string Complement) References(DonneesEditionBl d, LigneEditionBl l)
    {
        var article = l.Indice == "" ? l.Article : $"{l.Article}/{l.Indice}";
        var refClient = l.RefClient != "" ? l.RefClient : l.RefImprime;
        var marche = l.NumeroMarche == "" ? "" : $" - {l.NumeroMarche}";
        return d.Entete.ReferenceClientDabord && refClient != ""
            ? (refClient, $"N/Réf. : {article}{marche}")
            : (article, refClient == "" ? marche.TrimStart(' ', '-') : $"V/Réf. : {refClient}{marche}");
    }

    private static IEnumerable<string> Appel(LigneEditionBl l)
    {
        if (l.Appel != "")
            yield return $"N° d'appel : {l.Appel}" + (l.DateAppel is { } da ? $" du {da.ToString("dd/MM/yyyy", Fr)}" : "");
    }

    private static LigneImprimeeBl Ligne(DonneesEditionBl d, ElementEditionBl el, LigneEditionBl l)
    {
        // Ligne de texte (sans article) : désignations seules
        if (l.Article == "") return new LigneImprimeeBl("", [], "", l.Designations, "", [], "", []);

        var (reference, complement) = References(d, l);

        var (quantite, uc) = Quantite(l.Quantite, l.QuantiteEnUc, l.EmballageArticle, Emballage(l.TypeEmballage, "UC"));
        var designations = new List<string>();
        if (uc != "") designations.Add(uc);
        designations.AddRange(l.Designations);

        var details = new List<string>(Appel(l));
        if (d.Entete.ImprimerLots) details.AddRange(Lots(d.Lots.Where(x => x.Element == l.Element && x.Ligne == l.Ligne), l.UniteLots));
        details.AddRange(Normes(d, el.EnseigneCommande, l.Norme));

        return new LigneImprimeeBl(reference, complement == "" ? [] : [complement], quantite, designations, EtatLivraison(l.EtatLivraison),
            ColisLigne(l), Poids(l), details);
    }

    // Quantité, ou nombre d'unités de conditionnement quand l'option est demandée et que la division tombe juste
    internal static (string Quantite, string Uc) Quantite(int quantite, bool enUc, int emballage, string libelleUc) =>
        enUc && emballage > 0 && quantite % emballage == 0 && quantite != 0
            ? ((quantite / emballage).ToString("#,##0", Fr), $"{libelleUc} de {emballage.ToString("#,##0", Fr)} ex.")
            : (quantite.ToString("#,##0", Fr), "");

    // Poids d'une ligne (EXP035P2) : poids d'un colis × colis + poids d'un colis × exemplaires du mécompte / emballage
    internal static string Poids(LigneEditionBl l)
    {
        var poids = l.PoidsColis * l.Colis + (l.EmballagePar > 0 ? l.PoidsColis * l.ExemplairesMecompte / l.EmballagePar : 0);
        return poids == 0 ? "" : poids.ToString("#,##0.##", Fr);
    }

    // Colis d'une ligne : « 12 Carton(s) / de 500 ex. », mécompte « et 120 ex. / en 1 Carton(s) »
    internal static IReadOnlyList<string> ColisLigne(LigneEditionBl l)
    {
        var uc = Emballage(l.TypeEmballage, "colis");
        var r = new List<string>();
        if (l.Colis != 0)
        {
            r.Add($"{l.Colis} {uc}");
            if (l.EmballagePar > 0) r.Add($"de {l.EmballagePar.ToString("#,##0", Fr)} ex.");
        }
        if (l.ColisMecompte != 0)
        {
            r.Add($"{(l.Colis != 0 ? "et " : "")}{l.ExemplairesMecompte.ToString("#,##0", Fr)} ex.");
            r.Add($"en {l.ColisMecompte} {uc}");
        }
        return r;
    }

    // Colis d'un élément fabriqué : deux conditionnements possibles (HNUC1/HQTUC1/HUCDT1, HNUC2/HQTUC2), « S » = sans
    internal static IReadOnlyList<string> ColisElement(ElementEditionBl el)
    {
        var r = new List<string>();
        void Uc(int nombre, int parUc, string code)
        {
            var libelle = Emballage(code, "colis");
            if (nombre != 0) r.Add($"{nombre} {libelle}");
            else if (parUc != 0 && el.Etat == "V") r.Add(libelle);
            if (parUc != 0 && code != "S") r.Add($"de {parUc.ToString("#,##0", Fr)} ex.");
        }
        Uc(el.NbUc1, el.QteUc1, el.Uc1);
        if (el.NbUc2 != 0 || el.QteUc2 != 0) Uc(el.NbUc2, el.QteUc2, el.Uc2);
        return r;
    }

    // Lots consécutifs identiques regroupés : « N° lot 12345A  2 500 ex. » (lot fournisseur s'il est connu)
    internal static IEnumerable<string> Lots(IEnumerable<LotEditionBl> lots, string unite)
    {
        var groupes = new List<(string Lot, int Quantite)>();
        foreach (var l in lots)
        {
            var lot = l.LotFournisseur != "" ? l.LotFournisseur : l.Lot;
            if (groupes.Count > 0 && groupes[^1].Lot == lot) groupes[^1] = (lot, groupes[^1].Quantite + l.Quantite);
            else groupes.Add((lot, l.Quantite));
        }
        return groupes.Select(g => $"N° lot {g.Lot}  {g.Quantite.ToString("#,##0", Fr)} {(unite == "" ? "ex." : unite)}");
    }

    private static IReadOnlyList<string> Normes(DonneesEditionBl d, string enseigne, string code) =>
        code == "" ? [] : d.Normes.GetValueOrDefault((enseigne, code)) ?? [];

    // Listes fixes de DDVLLSL0 : 28 unités de conditionnement, 30 modes d'expédition, 31 types de port, 32 états de livraison
    internal static string Emballage(string code, string defaut) => code switch
    {
        "B" => "Bobine(s)", "C" => "Carton(s)", "F" => "Filtainer", "P" => "Paquet(s)", "Q" => "Palette(s)", "R" => "Rouleau(x)",
        "S" => "Sans conditionnement", "T" => "Barquettes", _ => defaut,
    };

    internal static string TypeLivraison(string code) => code switch
    {
        "A" => "Affrètement", "B" => "Maritime", "C" => "Aérien", "E" => "Express", "I" => "International", "M" => "Messagerie",
        "P" => "Multi-pal", _ => code,
    };

    // Codes d'une lettre traduits, incoterms (CIF, DAP…) tels quels
    internal static string TypePort(string code) => code switch
    {
        "A" => "Avancé", "C&F" => "Coût & fret", "D" => "Dû", "E" => "Enlèvement", "F" => "Franco", _ => code,
    };

    internal static string EtatLivraison(string code) => code switch
    {
        "A" => "Acompte", "E" => "Suite", "S" => "Solde", "T" => "Totalité", _ => code,
    };
}
