using NetPrint.Socle;

namespace NetPrint.Modules.Facturation.Services;

// =====================================================================
// Génération des factures automatiques (FAC402P, docs/facturation/fiches/FAC402P-ecritures.md) : les éléments de BL retenus
// (FAC401) sont regroupés en factures et éléments selon le mode de facturation du donneur d'ordre, puis leurs lignes sont
// construites (négoce : lignes du BL et de la commande ; fabrication : ligne du dossier), avec les compléments du dossier
// (AB004), les éléments « à facturer en plus » (EXP011) et la remise de la commande. Calcul pur, sans base : la simulation
// et la génération définitive partagent ce moteur (le RPG avait deux copies divergentes, FAC412 et FAC402P).
//
// Corrigé par rapport au RPG (§ 7 de la fiche) : mode 5 traité (le RPG l'ignorait) ; avoir en montants positifs, signe porté
// par le type A (convention de toutes les factures de la base) ; conditions de règlement du groupe ; EXP011 de chaque BL ;
// quantité de la ligne dossier par BL en mode 6 et remise une fois par élément ; anomalies de toutes les lignes ; avoir non
// marqué « à contrôler » d'office ; « Le tout » et unité vide ou LT : montant = prix ; ligne de texte seulement si non vide ;
// lignes négoce lues pour tous les types de commande. Gardé : mode 7 regroupé par commande (TART7 jamais alimenté dans le RPG).
// =====================================================================

// Conditions de règlement de l'élément (FAC401 : client des conditions, remplacées par celles de la commande si AB001.CCMOD)
internal sealed record ConditionsReglement(string Mode, int NombreJours, int Le, decimal Escompte, int Arrete);

internal sealed record AdresseEnvoi(string FormeJuridique, string Nom, string Rue1, string Rue2, string CodePostal, string Ville, string Pays)
{
    public static readonly AdresseEnvoi Aucune = new("", "", "", "", "", "", "");
    public bool Vide => Nom == "" && Rue1 == "" && Rue2 == "" && CodePostal == "" && Ville == "";
}

// Ligne de commande négoce (NEG100) d'une ligne de BL ; Gratuit : DNOPT2 = O ; PrixADefinir : DNOPT5 = O
internal sealed record LigneCommandeNegoce(int Ligne, string CodeLigne, string Article, string Indice, decimal Pu, string Unite,
    decimal TauxCommission, bool Gratuit, bool PrixADefinir);

// Ligne de BL (EXP015) et sa ligne de commande (null : absente)
internal sealed record LigneBl(int Ligne, string Article, string CodeLigne, decimal Quantite, string Designation, string Designation2,
    string Designation3, LigneCommandeNegoce? Commande);

// Élément « à facturer en plus » du BL (EXP011, pas encore facturé)
internal sealed record LigneEnPlus(int Ligne, string CodeLigne, string Article, string Indice, decimal Quantite, string Designation,
    string Unite, decimal Pu, decimal TauxCommission);

// Complément de facturation du dossier (AB004, pas encore facturé) ; Remise : taux F4REM
internal sealed record ComplementDossier(int Ligne, string CodeLigne, decimal Quantite, string Designation, string Unite, int Remise, decimal Pu);

// Commande (AB001, AB001FA, client de la commande) ; Fabrication : CCTYPC = 1 ; Adresse : AB001FA, sinon A500F n° FANAL ;
// PiedDuDonneurOrdre : textes de pied du donneur d'ordre (FATPC blanc ou 1), sinon du client facturé
internal sealed record DossierAFacturer(
    int Numero, string Cle, string TypeCommande, string CodeVente, string Unite, string Designation1, string Designation2, decimal Pu,
    decimal TauxRemise, string Reference, DateOnly? DateCommande, string Categorie, string SousCategorie, int Devis, int Modele,
    decimal TauxCommission, string Monnaie, decimal Acompte, string AlAttention, string OrdreTri, string EditionBl, AdresseEnvoi Adresse,
    bool PiedDuDonneurOrdre, string Releve, string Presentation, IReadOnlyList<ComplementDossier> Complements)
{
    public bool Fabrication => TypeCommande == "1";

    // FAC090.FETYP : fabrication 1, négoce 2 (CCTYPC 4) ou 3 (CCTYPC 3), autre 0
    public int TypeElement => TypeCommande switch { "1" => 1, "4" => 2, "3" => 3, _ => 0 };
}

// Élément de BL à facturer (TFAC401) : donneur d'ordre (HGENE / HCLIE), client facturé du BL (HGENF / HCLIF, 0 possible),
// mode du donneur d'ordre (saisie manuelle : 6, un élément par commande), conditions, adresse de livraison, représentant de la
// commande, retour (HETABL = R) ⇒ avoir ; date d'expédition et enseigne du BL (EXP009)
internal sealed record ElementBlAFacturer(
    int Bl, int Element, int CompteGeneral, int CompteClient, int CompteGeneralFacture, int CompteClientFacture, int Mode,
    ConditionsReglement Conditions, AdresseEnvoi Livraison, int Representant, bool Retour, decimal QuantiteLivree,
    DossierAFacturer Dossier, IReadOnlyList<LigneBl> Lignes, IReadOnlyList<LigneEnPlus> EnPlus, string NomClient = "",
    DateOnly? Expedition = null, string Enseigne = "");

// Paramètres de la société : types de ligne FAC020 (code → type, libellé) et diviseurs des unités de vente (AA0008 « UV »)
internal sealed record ParametresGeneration(IReadOnlyDictionary<string, (string Type, string Libelle)> TypesLigne,
    IReadOnlyDictionary<string, decimal> Diviseurs)
{
    public const decimal LeTout = 999999999m;

    // Code et libellé de la remise : premier type de ligne « R » dans l'ordre des codes (FAC402P l. 775-787)
    public (string Code, string Libelle) Remise => TypesLigne.Where(t => t.Value.Type == "R").OrderBy(t => t.Key, StringComparer.Ordinal)
        .Select(t => (t.Key, t.Value.Libelle)).FirstOrDefault(("", ""));
}

// Ligne de facture (FAC100) ; LigneBl : FGLBL (O / N / blanc) ; Bl / ElementBl / LigneDuBl : FGBL, FGBLEL, FGBLLG ;
// PrixLibre : prix et unité non contrôlés (article gratuit de la commande, complément du dossier, élément en plus du BL) ;
// Origine : complément AB004 (C + n° de ligne) ou élément en plus EXP011 (X + BL.élément.ligne) dont la ligne vient
internal sealed record LignePrevue(
    int Numero, string CodeLigne, decimal Quantite, string Designation, decimal Pu, int Remise, string Unite, string Article, string Indice,
    decimal Montant, decimal TauxCommission, string LigneBl, string Monnaie, int LigneCommande, int Bl, int ElementBl, int LigneDuBl,
    bool PrixLibre = false, string Origine = "")
{
    public static string DeComplement(int ligne) => $"C{ligne}";
    public static string DEnPlus(int bl, int element, int ligne) => $"X{bl}.{element}.{ligne}";
    public bool Texte => CodeLigne == "9";
}

// Élément de facture (FAC090) et ce qu'il marque comme facturé
internal sealed record ElementPrevu(
    int Numero, DossierAFacturer Dossier, decimal Quantite, decimal ChiffreAffaires, decimal Ventes, IReadOnlyList<LignePrevue> Lignes,
    IReadOnlyList<(int Bl, int Element)> Bls, IReadOnlyList<(int Bl, int Element, int Ligne)> EnPlus, IReadOnlyList<int> Complements,
    IReadOnlyList<string> Anomalies);

// Facture ou avoir à créer (FAC080) ; AControler : FDEDIT = A
internal sealed record FacturePrevue(
    bool Avoir, int CompteGeneral, int CompteClient, int CompteGeneralFacture, int CompteClientFacture, int Mode,
    ConditionsReglement Conditions, int Representant, AdresseEnvoi Adresse, DossierAFacturer DernierDossier, string CodeVente,
    IReadOnlyList<ElementPrevu> Elements, IReadOnlyList<string> Anomalies, decimal Acompte)
{
    public bool AControler => Anomalies.Count > 0;
    public decimal Ventes => Elements.Sum(e => e.Ventes);

    // Textes de pied : ceux du donneur d'ordre ou du client facturé, selon la dernière commande (FAC402P SPAB001)
    public (int General, int Client) Pied => DernierDossier.PiedDuDonneurOrdre || CompteClientFacture == 0
        ? (CompteGeneral, CompteClient) : (CompteGeneralFacture, CompteClientFacture);
}

internal static class GenerationFactures
{
    public const int ElementsMaxi = 99;   // FAC090.FENUM sur 2 chiffres

    public static IReadOnlyList<FacturePrevue> Generer(IEnumerable<ElementBlAFacturer> elements, ParametresGeneration p)
    {
        var factures = new List<FacturePrevue>();
        // Compléments AB004 et acompte d'une commande : sur la première facture qui la contient seulement (le RPG reprenait
        // l'acompte sur chaque facture : double déduction)
        var deja = new Facturees();
        // Ordre et regroupements des logiques TFAC401L1 à L7 : donneur d'ordre, client facturé, puis selon le mode
        foreach (var groupe in elements.OrderBy(e => e.CompteGeneral).ThenBy(e => e.CompteClient)
                     .ThenBy(e => e.CompteGeneralFacture).ThenBy(e => e.CompteClientFacture)
                     .ThenBy(e => CleTri(e), StringComparer.Ordinal).ThenBy(e => e.Bl).ThenBy(e => e.Element)
                     .GroupBy(e => (e.CompteGeneral, e.CompteClient, e.CompteGeneralFacture, e.CompteClientFacture, CleFacture(e))))
        {
            var parElement = groupe.GroupBy(CleElement).ToList();
            // Au-delà de 99 éléments, la suite va sur une autre facture
            foreach (var morceau in parElement.Chunk(ElementsMaxi))
                factures.Add(Facture(morceau, p, deja));
        }
        return factures;
    }

    // Saisie manuelle (FAC085 / FAC150) : une seule facture pour les éléments de BL choisis, un élément par commande
    public static FacturePrevue Regrouper(IReadOnlyList<ElementBlAFacturer> elements, ParametresGeneration p)
    {
        if (elements.Count == 0) throw new RegleMetierException("Choisissez au moins un BL à facturer.");
        if (elements.Select(e => e.Retour).Distinct().Count() > 1)
            throw new RegleMetierException("Les BL de retour (avoir) et les BL livrés (facture) se facturent séparément.");
        if (elements.Select(e => (e.CompteGeneral, e.CompteClient)).Distinct().Count() > 1)
            throw new RegleMetierException("Les BL choisis n'ont pas tous le même donneur d'ordre.");
        // Client facturé du BL (0 : le donneur d'ordre) : une facture n'en a qu'un ; les conditions, elles, se corrigent à l'écran
        if (elements.Select(e => e.CompteClientFacture == 0 ? (e.CompteGeneral, e.CompteClient) : (e.CompteGeneralFacture, e.CompteClientFacture))
                .Distinct().Count() > 1)
            throw new RegleMetierException("Les BL choisis ne sont pas tous facturés au même client : faites une facture par client facturé.");
        var groupes = elements.Select(e => e with { Mode = 6 }).OrderBy(Dossier, StringComparer.Ordinal).ThenBy(e => e.Bl).ThenBy(e => e.Element)
            .GroupBy(Dossier).ToList();
        if (groupes.Count > ElementsMaxi) throw new RegleMetierException($"Une facture ne peut pas porter plus de {ElementsMaxi} commandes.");
        return Facture(groupes, p, new Facturees());
    }

    // ---------- Regroupements par mode (fiche § 2) ----------
    private static string Adresse(AdresseEnvoi a) => string.Join("|", a.FormeJuridique, a.Nom, a.Rue1, a.Rue2, a.CodePostal, a.Ville);

    private static string Conditions(ElementBlAFacturer e) =>
        $"{e.Representant:000}|{e.Conditions.Mode}|{e.Conditions.NombreJours:000}|{e.Conditions.Le:00}|{e.Conditions.Arrete:00}|{e.Conditions.Escompte:00.00}";

    private static string Dossier(ElementBlAFacturer e) => $"{e.Dossier.Numero:00000}|{e.Dossier.Cle}";

    private static string Etat(ElementBlAFacturer e) => e.Retour ? "R" : "C";

    private static string CleFacture(ElementBlAFacturer e) => e.Mode switch
    {
        1 => $"{Dossier(e)}|{Etat(e)}|{e.Bl:000000}|{e.Element:000}",
        2 => $"{Dossier(e)}|{Adresse(e.Livraison)}|{Etat(e)}",
        3 or 7 => $"{Dossier(e)}|{Etat(e)}",
        4 => $"{Conditions(e)}|{Etat(e)}|{e.Bl:000000}",
        5 or 8 => $"{Conditions(e)}|{Adresse(e.Livraison)}|{Etat(e)}",
        _ => $"{Conditions(e)}|{Etat(e)}",   // 6
    };

    // Élément de facture : un par élément de BL (1, 4, 5, 8), un par commande (6), un seul (2, 3, 7)
    private static string CleElement(ElementBlAFacturer e) => e.Mode switch
    {
        6 => Dossier(e),
        2 or 3 or 7 => "",
        _ => $"{Dossier(e)}|{e.Bl:000000}|{e.Element:000}",
    };

    private static string CleTri(ElementBlAFacturer e) => $"{CleFacture(e)}#{CleElement(e)}";

    // ---------- Facture ----------
    private sealed class Facturees
    {
        public readonly HashSet<(int, string)> Complements = [];
        public readonly HashSet<(int, string)> Acomptes = [];
    }

    private static FacturePrevue Facture(IReadOnlyList<IGrouping<string, ElementBlAFacturer>> groupes, ParametresGeneration p, Facturees deja)
    {
        var premier = groupes[0].First();
        var avoir = premier.Retour;
        var elements = new List<ElementPrevu>();
        foreach (var g in groupes)
            elements.Add(Element(elements.Count + 1, [.. g], avoir, p, deja.Complements));
        var dernier = groupes[^1].Last().Dossier;
        var acompte = avoir ? 0m : elements.Select(e => e.Dossier).DistinctBy(d => (d.Numero, d.Cle))
            .Where(d => d.Acompte != 0 && deja.Acomptes.Add((d.Numero, d.Cle))).Sum(d => d.Acompte);

        var anomalies = elements.SelectMany(e => e.Anomalies).Distinct().ToList();
        // Code vente de la facture : premier type de ligne « vente » ; un autre code « vente » est une anomalie
        var ventes = elements.SelectMany(e => e.Lignes).Where(l => !l.Texte && Type(p, l.CodeLigne) == "V").Select(l => l.CodeLigne).Distinct().ToList();
        var codeVente = ventes.FirstOrDefault() ?? elements.SelectMany(e => e.Lignes).FirstOrDefault(l => !l.Texte)?.CodeLigne ?? "";
        if (ventes.Count > 1) anomalies.Add("Code vente différent");
        if (elements.Sum(e => e.Ventes) == 0) anomalies.Add("Vente à 0");
        if (acompte > elements.Sum(e => e.Ventes)) anomalies.Add("Acompte supérieur au montant");

        // Adresse d'envoi : mode 8 = lieu de livraison ; sinon celle de la commande si le client facturé est le donneur d'ordre
        var memeClient = premier.CompteClientFacture == 0
                         || (premier.CompteGeneralFacture == premier.CompteGeneral && premier.CompteClientFacture == premier.CompteClient);
        var adresse = premier.Mode == 8 ? premier.Livraison with { Pays = "" } : memeClient ? dernier.Adresse : AdresseEnvoi.Aucune;
        return new FacturePrevue(avoir, premier.CompteGeneral, premier.CompteClient, premier.CompteGeneralFacture, premier.CompteClientFacture,
            premier.Mode, premier.Conditions, premier.Representant, adresse, dernier, codeVente, elements, anomalies, acompte);
    }

    private static string Type(ParametresGeneration p, string code) => p.TypesLigne.TryGetValue(code, out var t) ? t.Type : "";

    // ---------- Élément et ses lignes (fiche § 4.4 et § 5) ----------
    private static ElementPrevu Element(int numero, IReadOnlyList<ElementBlAFacturer> bls, bool avoir, ParametresGeneration p,
        HashSet<(int, string)> complementsPris)
    {
        var d = bls[0].Dossier;
        var cumul = bls[0].Mode is 2 or 3 or 7;   // une seule ligne dossier pour tous les BL
        var lignes = new List<LignePrevue>();
        var anomalies = new List<string>();
        var enPlus = new List<(int, int, int)>();
        var complements = new List<int>();
        decimal ca = 0, ventes = 0, quantite = 0;
        var origine = "";   // complément ou élément en plus en cours (saisie : sa suppression le laisse à facturer)

        void Ajouter(string code, decimal qte, string des, decimal pu, string unite, string article, string indice, decimal taux,
            string ligneBl, int ligneCommande, int bl, int elementBl, int ligneDuBl, bool controler, bool prixADefinir = false)
        {
            var montant = Montant(qte, pu, unite, p, out var uniteInconnue);
            var type = Type(p, code);
            lignes.Add(new LignePrevue(lignes.Count + 1, code, qte, des, pu, 0, unite, article, indice, montant, taux, ligneBl, d.Monnaie,
                ligneCommande, bl, elementBl, ligneDuBl, !controler, origine));
            ventes += montant;
            if (type is not ("E" or "P") && type != "") ca += montant;
            if (code.Trim() == "") anomalies.Add("Code vente");
            if (!controler) return;
            if (unite == "" || uniteInconnue) anomalies.Add("Unité");
            if (pu == 0) anomalies.Add(prixADefinir ? "Prix à définir" : "P.U.");
        }
        void Texte(string texte)
        {
            if (texte.Trim() == "") return;
            lignes.Add(new LignePrevue(lignes.Count + 1, "9", 0, texte, 0, 0, "", "", "", 0, 0, "", "", 0, 0, 0, 0, Origine: origine));
        }
        void LigneDossier(decimal qte, int bl, int elementBl)
        {
            Ajouter(d.CodeVente, qte, d.Designation1, d.Pu, d.Unite, "", "", 0, "N", 0, bl, elementBl, 0, controler: true);
            quantite += qte;
            Texte(d.Designation2);
        }
        void Complements()
        {
            if (!d.Fabrication || avoir || !complementsPris.Add((d.Numero, d.Cle))) return;
            foreach (var c in d.Complements)
            {
                complements.Add(c.Ligne);
                origine = LignePrevue.DeComplement(c.Ligne);
                if (c.CodeLigne == "9") { Texte(c.Designation); continue; }
                if (Type(p, c.CodeLigne) == "R") Remise(c.CodeLigne, c.Designation, c.Remise, c.Pu);
                else Ajouter(c.CodeLigne, c.Quantite, c.Designation, c.Pu, c.Unite, "", "", 0, "N", 0, 0, 0, 0, controler: false);
            }
            origine = "";
        }
        void Remise(string code, string libelle, int taux, decimal montantFixe)
        {
            var montant = taux != 0 ? Math.Truncate(ca * taux / 100m * 100m) / 100m : montantFixe;
            lignes.Add(new LignePrevue(lignes.Count + 1, code, 0, libelle, 0, taux, "", "", "", montant, 0, "", "", 0, 0, 0, 0, Origine: origine));
            ca -= montant;
            ventes -= montant;
        }

        foreach (var e in bls)
        {
            if (d.Fabrication)
            {
                if (!cumul) LigneDossier(e.QuantiteLivree, e.Bl, e.Element);
            }
            else
                foreach (var l in e.Lignes)
                {
                    if (l.Commande is { } c && c.CodeLigne != "9" && (l.Article != "" || l.CodeLigne != ""))
                    {
                        var controler = (l.Article == "" && Type(p, c.CodeLigne) is not ("E" or "P")) || (l.Article != "" && !c.Gratuit);
                        Ajouter(c.CodeLigne, l.Quantite, l.Designation, c.Pu, c.Unite, c.Article, c.Indice, c.TauxCommission, "O", c.Ligne,
                            e.Bl, e.Element, l.Ligne, controler, c.PrixADefinir);
                    }
                    else Texte(l.Designation);
                    Texte(l.Designation2);
                    Texte(l.Designation3);
                }
            if (!cumul) Complements();
            foreach (var x in e.EnPlus)
            {
                enPlus.Add((e.Bl, e.Element, x.Ligne));
                origine = LignePrevue.DEnPlus(e.Bl, e.Element, x.Ligne);
                if (x.CodeLigne == "9") Texte(x.Designation);
                else Ajouter(x.CodeLigne, x.Quantite, x.Designation, x.Pu, x.Unite, x.Article, x.Indice, x.TauxCommission,
                    d.Fabrication ? "N" : "O", 0, 0, 0, 0, controler: false);
                origine = "";
            }
        }
        if (cumul)
        {
            if (d.Fabrication) LigneDossier(bls.Sum(e => e.QuantiteLivree), bls[0].Bl, bls[0].Element);
            Complements();
        }
        if (!d.Fabrication && d.TauxRemise > 0)
        {
            var (code, libelle) = p.Remise;
            Remise(code, libelle, (int)d.TauxRemise, 0);
        }
        return new ElementPrevu(numero, d, quantite, ca, ventes, lignes, [.. bls.Select(e => (e.Bl, e.Element))], enPlus, complements,
            [.. anomalies.Distinct()]);
    }

    // Montant d'une ligne (FAC402P SPFAC100) : quantité ÷ diviseur de l'unité (tronqué à 5 décimales) × prix, arrondi au centime ;
    // unité vide, LT ou « Le tout » : prix forfaitaire ; unité absente de la table UV : 0 (et anomalie)
    public static decimal Montant(decimal quantite, decimal pu, string unite, ParametresGeneration p, out bool uniteInconnue)
    {
        uniteInconnue = false;
        if (unite.Trim() is "" or "LT") return pu;
        if (!p.Diviseurs.TryGetValue(unite, out var diviseur) || diviseur == 0) { uniteInconnue = true; return 0; }
        if (diviseur == ParametresGeneration.LeTout) return pu;
        var z = Math.Truncate(quantite / diviseur * 100000m) / 100000m;
        return Math.Round(z * pu, 2, MidpointRounding.AwayFromZero);
    }
}
