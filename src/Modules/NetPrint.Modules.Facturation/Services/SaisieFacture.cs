using NetPrint.Modules.Facturation.Contrats;

namespace NetPrint.Modules.Facturation.Services;

// =====================================================================
// Saisie d'une facture à partir de BL (FAC150), sans base : facture préparée par le moteur commun (GenerationFactures.Regrouper)
// mise en saisie, montants recalculés à chaque modification (FAC150 R28 à R33), saisie contrôlée, puis report des corrections
// sur la facture à écrire. Erreurs bloquantes : type de ligne inconnu, unité absente de la table UV, en-tête incomplet.
// Anomalies (facture créée « à contrôler », comme en automatique) : prix à 0, vente à 0, plusieurs codes vente, acompte trop fort.
// =====================================================================
internal static class SaisieFacture
{
    // Délais d'échéance autorisés (FAC150 R10, table TJE)
    public static readonly IReadOnlyList<int> Delais = [0, 20, 30, 45, 60, 90, 120, 150, 180];

    public static FactureSaisie VersSaisie(FacturePrevue f, IReadOnlyList<ElementDeBl> bls, string nom, ParametresGeneration p, DateOnly date,
        IReadOnlyList<CodeLibelle> modes, IReadOnlyList<CodeLibelle> representants, bool peutEditer, int? ajoutA = null) => new()
    {
        // Ajout à la facture ajoutA du jour date : en-tête inchangé, acompte des nouvelles commandes non repris (FDCPT inchangé)
        Numero = ajoutA,
        DateOrigine = ajoutA is null ? null : date,
        Ajout = ajoutA is not null,
        Avoir = f.Avoir,
        CompteGeneral = f.CompteGeneral,
        CompteClient = f.CompteClient,
        NomClient = nom,
        Date = date,
        CompteGeneralFacture = f.CompteClientFacture == 0 ? null : f.CompteGeneralFacture,
        CompteClientFacture = f.CompteClientFacture == 0 ? null : f.CompteClientFacture,
        ModeReglement = f.Conditions.Mode,
        NombreJours = f.Conditions.NombreJours,
        Le = f.Conditions.Le,
        Arrete = f.Conditions.Arrete,
        Escompte = Math.Truncate(f.Conditions.Escompte * 10m) / 10m,
        Representant = f.Representant == 0 ? null : f.Representant,
        Reference = f.DernierDossier.Reference,
        AlAttention = f.DernierDossier.AlAttention,
        Acompte = ajoutA is null ? f.Acompte : 0,
        PeutEditerDefinitif = peutEditer,
        Bls = bls,
        Empreinte = Empreinte(f),
        Elements = [.. f.Elements.Select(e => new ElementSaisie
        {
            Numero = e.Numero,
            Monnaie = e.Dossier.Monnaie,
            Commande = e.Dossier.Numero,
            Cle = e.Dossier.Cle,
            Designation = e.Dossier.Designation1,
            Bls = [.. e.Bls.Select(b => b.Bl).Distinct()],
            Ventes = e.Ventes,
            Lignes = [.. e.Lignes.Select(l => VersLigne(l, p, l.PrixLibre))],
        })],
        CodesLigne = CodesLigne(p),
        Unites = p.Diviseurs,
        ModesReglement = modes,
        Representants = representants,
    };

    public static IReadOnlyList<CodeLigneFacture> CodesLigne(ParametresGeneration p) =>
        [.. p.TypesLigne.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => new CodeLigneFacture(t.Key, t.Value.Type, t.Value.Libelle))];

    // Ligne préparée ou relue → ligne modifiable ; remise en montant (complément AB004) : le montant se saisit dans le prix
    public static LigneSaisie VersLigne(LignePrevue l, ParametresGeneration p, bool prixLibre) => new()
    {
        CodeLigne = l.CodeLigne, Designation = l.Designation, Quantite = l.Quantite, Unite = l.Unite, Remise = l.Remise,
        Pu = Type(p, l.CodeLigne) == "R" && l.Remise == 0 ? l.Montant : l.Pu,
        Montant = l.Montant, PrixLibre = prixLibre, Origine = l.Origine, Conserve = l.Conserve, Article = l.Article, Indice = l.Indice,
        TauxCommission = l.TauxCommission, LigneBl = l.LigneBl, LigneCommande = l.LigneCommande, Bl = l.Bl, ElementBl = l.ElementBl,
        LigneDuBl = l.LigneDuBl,
    };

    // Ce que la préparation a lu : la création refuse si la relecture des BL donne autre chose (BL, commande ou prix changés)
    public static string Empreinte(FacturePrevue f) => string.Join(";", f.Elements.Select(e =>
        $"{e.Dossier.Numero}/{e.Dossier.Cle}:{string.Join(",", e.Bls)}:{string.Join(",", e.Lignes.Select(l => $"{l.CodeLigne}|{l.Quantite}|{l.Pu}|{l.Montant}"))}"))
        + $"#{f.Avoir}|{f.CompteGeneral}|{f.CompteClient}|{f.Acompte}";

    public static ParametresGeneration Parametres(FactureSaisie s) =>
        new(s.CodesLigne.ToDictionary(c => c.Code, c => (c.Type, c.Libelle)), s.Unites);

    private static string Type(ParametresGeneration p, string code) => p.TypesLigne.TryGetValue(code, out var t) ? t.Type : "";

    // En-tête : date, client facturé complet, mode de règlement (RG), délai, jours, escompte, représentant (B000F)
    private static void ControlerEnTete(FactureSaisie s, Action<string, string> Erreur)
    {
        if (s.Date is null) Erreur(nameof(s.Date), "La date de la facture est obligatoire.");
        if ((s.CompteGeneralFacture is null) != (s.CompteClientFacture is null))
            Erreur(nameof(s.CompteGeneralFacture), "Client facturé : indiquez le compte général et le compte client, ou aucun des deux.");
        s.ModeReglement = s.ModeReglement.Trim();
        if (s.ModeReglement == "") Erreur(nameof(s.ModeReglement), "Le mode de règlement est obligatoire.");
        else if (s.ModesReglement.Count > 0 && s.ModesReglement.All(m => m.Code != s.ModeReglement))
            Erreur(nameof(s.ModeReglement), $"Le mode de règlement {s.ModeReglement} n'existe pas.");
        if (!Delais.Contains(s.NombreJours))
            Erreur(nameof(s.NombreJours), $"Délai d'échéance autorisé : {string.Join(", ", Delais)} jours.");
        if (s.Le is < 0 or > 31) Erreur(nameof(s.Le), "Le jour d'échéance « le » va de 0 à 31.");
        if (s.Arrete is < 0 or > 31) Erreur(nameof(s.Arrete), "Le jour d'arrêté va de 0 à 31.");
        if (s.Escompte is < 0 or > 99.9m) Erreur(nameof(s.Escompte), "Le taux d'escompte va de 0 à 99,9 %.");
        if (s.Representant is not { } r || r <= 0) Erreur(nameof(s.Representant), "Le représentant est obligatoire.");
        else if (s.Representants.Count > 0 && s.Representants.All(x => x.Code != r.ToString()))
            Erreur(nameof(s.Representant), $"Le représentant {r} n'existe pas.");
    }

    public static ControleFactureSaisie Recalculer(FactureSaisie s)
    {
        var p = Parametres(s);
        var erreurs = new List<ErreurLancement>();
        var anomalies = new List<string>();
        void Erreur(string champ, string message) => erreurs.Add(new(champ, message));

        // Ajout à une facture existante : son en-tête ne change pas (ni saisi ni écrit), il n'est pas contrôlé
        if (!s.Ajout) ControlerEnTete(s, Erreur);

        foreach (var e in s.Elements)
        {
            var (_, ventes) = Calculer(e, p, (rang, message) => Erreur("", $"Commande {e.Commande}/{e.Cle}, ligne {rang} : {message}"),
                anomalies.Add);
            e.Ventes = ventes;
        }
        var total = s.Elements.Sum(e => e.Ventes);
        if (s.Elements.SelectMany(e => e.Lignes).Where(l => Type(p, l.CodeLigne.Trim()) == "V").Select(l => l.CodeLigne.Trim()).Distinct().Count() > 1)
            anomalies.Add("Code vente différent");
        if (total == 0) anomalies.Add("Vente à 0");
        if (s.Acompte > total) anomalies.Add("Acompte supérieur au montant");
        return new ControleFactureSaisie(erreurs, [.. anomalies.Distinct()], total);
    }

    // Montants des lignes d'un élément (FAC150 R30 à R33) : vente = quantité ramenée à l'unité × prix ; port = prix ; remise =
    // pourcentage du chiffre d'affaires qui précède (tronqué au centime, comme FAC402P) ou montant saisi ; texte = 0. Port : même
    // calcul que la vente (prix seul sans unité), hors chiffre d'affaires
    public static (decimal ChiffreAffaires, decimal Ventes) Calculer(ElementSaisie e, ParametresGeneration p, Action<int, string>? erreur = null,
        Action<string>? anomalie = null)
    {
        decimal ca = 0, ventes = 0;
        for (var i = 0; i < e.Lignes.Count; i++)
        {
            var l = e.Lignes[i];
            l.CodeLigne = l.CodeLigne.Trim().ToUpperInvariant();
            l.Unite = l.Unite.Trim().ToUpperInvariant();
            if (!p.TypesLigne.TryGetValue(l.CodeLigne, out var t))
            {
                l.Montant = 0;
                erreur?.Invoke(i + 1, l.CodeLigne == "" ? "type de ligne obligatoire." : $"type de ligne « {l.CodeLigne} » inconnu.");
                continue;
            }
            switch (t.Type)
            {
                case "T":
                    l.Montant = 0;
                    break;
                case "R":
                    l.Montant = l.Remise != 0 ? Math.Truncate(ca * l.Remise / 100m * 100m) / 100m : l.Pu;
                    ca -= l.Montant;
                    ventes -= l.Montant;
                    break;
                default:
                    l.Montant = GenerationFactures.Montant(l.Quantite, l.Pu, l.Unite, p, out var inconnue);
                    if (inconnue) erreur?.Invoke(i + 1, $"unité « {l.Unite} » absente de la table des unités de vente.");
                    ventes += l.Montant;
                    if (t.Type is "P" or "E") break;   // port : hors chiffre d'affaires
                    if (l.PrixLibre) { ca += l.Montant; break; }
                    if (l.Pu == 0) anomalie?.Invoke("P.U.");
                    if (l.Unite == "") anomalie?.Invoke("Unité");   // sans unité, le prix vaut pour le tout : à vérifier
                    ca += l.Montant;
                    break;
            }
        }
        return (ca, ventes);
    }

    // Lignes FAC100 d'un élément saisi, montants recalculés ; remise : montant seul, prix à 0, comme la facturation automatique
    public static (decimal ChiffreAffaires, decimal Ventes, List<LignePrevue> Lignes) Lignes(ElementSaisie e, ParametresGeneration p)
    {
        var (ca, ventes) = Calculer(e, p);
        var lignes = e.Lignes.Select((l, i) =>
        {
            var type = Type(p, l.CodeLigne);
            var texte = type == "T";
            return new LignePrevue(i + 1, l.CodeLigne, texte || type == "R" ? 0 : l.Quantite, l.Designation.Trim(), texte || type == "R" ? 0 : l.Pu,
                type == "R" ? l.Remise : 0, texte ? "" : l.Unite, l.Article, l.Indice, l.Montant, l.TauxCommission,
                texte ? "" : l.LigneBl, texte ? "" : e.Monnaie, l.LigneCommande, l.Bl, l.ElementBl, l.LigneDuBl, l.PrixLibre, l.Origine,
                l.Conserve as ZonesConservees);
        }).ToList();
        return (ca, ventes, lignes);
    }

    // Code vente de la facture (FAC080.FDVEN) : premier code de type vente, sinon premier code qui n'est pas un texte
    public static string CodeVente(IEnumerable<LignePrevue> lignes, ParametresGeneration p)
    {
        var toutes = lignes.ToList();
        return toutes.FirstOrDefault(l => Type(p, l.CodeLigne) == "V")?.CodeLigne
               ?? toutes.FirstOrDefault(l => Type(p, l.CodeLigne) != "T")?.CodeLigne ?? "";
    }

    // Nouvelle date d'une facture modifiée (FAC150 R4) : même année en numérotation annuelle, même mois en mensuelle (le n° en dépend)
    public static string? ControlerNouvelleDate(int typeNumerotation, DateOnly origine, DateOnly nouvelle) => typeNumerotation switch
    {
        1 when nouvelle.Year != origine.Year => $"La numérotation est annuelle : la facture doit rester en {origine.Year}.",
        2 when nouvelle.Year != origine.Year || nouvelle.Month != origine.Month =>
            $"La numérotation est mensuelle : la facture doit rester en {origine:MM/yyyy}.",
        _ => null,
    };

    // Facture à écrire : celle relue à la création, avec l'en-tête et les lignes saisis
    public static FacturePrevue Appliquer(FacturePrevue f, FactureSaisie s, ParametresGeneration p, IReadOnlyList<string> anomalies)
    {
        var elements = f.Elements.Select(e =>
        {
            var saisi = s.Elements.Single(x => x.Numero == e.Numero);
            var (ca, ventes, lignes) = Lignes(saisi, p);
            // Complément ou élément en plus dont toutes les lignes ont été supprimées : pas facturé, il reste à facturer
            var supprimees = e.Lignes.Select(l => l.Origine).Where(o => o != "").ToHashSet();
            supprimees.ExceptWith(lignes.Select(l => l.Origine));
            return e with
            {
                ChiffreAffaires = ca, Ventes = ventes, Lignes = lignes,
                EnPlus = [.. e.EnPlus.Where(x => !supprimees.Contains(LignePrevue.DEnPlus(x.Bl, x.Element, x.Ligne)))],
                Complements = [.. e.Complements.Where(c => !supprimees.Contains(LignePrevue.DeComplement(c)))],
            };
        }).ToList();
        var codeVente = CodeVente(elements.SelectMany(e => e.Lignes), p);
        int gf = s.CompteGeneralFacture ?? 0, cf = s.CompteClientFacture ?? 0;
        // Adresse de la commande seulement si le client facturé est le donneur d'ordre (FAC402P)
        var memeClient = cf == 0 || (gf == f.CompteGeneral && cf == f.CompteClient);
        return f with
        {
            CompteGeneralFacture = gf,
            CompteClientFacture = cf,
            Conditions = new ConditionsReglement(s.ModeReglement, s.NombreJours, s.Le, s.Escompte, s.Arrete),
            Representant = s.Representant ?? 0,
            Adresse = memeClient ? f.DernierDossier.Adresse : AdresseEnvoi.Aucune,
            DernierDossier = f.DernierDossier with { Reference = s.Reference.Trim(), AlAttention = s.AlAttention.Trim() },
            CodeVente = codeVente,
            Elements = elements,
            Anomalies = anomalies,
        };
    }
}
