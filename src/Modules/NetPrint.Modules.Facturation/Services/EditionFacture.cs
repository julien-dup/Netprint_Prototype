namespace NetPrint.Modules.Facturation.Services;

// =====================================================================
// Édition d'une facture (reprise de FAC202P5, docs/facturation/fiches/FAC202P5.md) : données lues, libellés dans la langue du
// donneur d'ordre (PRT008, programme « FACTURE »), puis document PDF (Impressions/DocumentFacture). Les montants viennent du
// calcul (CalculFacture), le document ne recalcule rien.
// =====================================================================

// En-tête : adresse figée de la facture (sinon celle du client facturé), références, règlement ; Brouillon : pas encore éditée
// en définitif (FDEDIT = O) ; Origine : facture d'origine d'un avoir (FDFACO / FDFACA, que le RPG n'imprimait pas)
internal sealed record EnTeteEditionFacture(
    int Numero, string NumeroImprime, DateOnly Date, bool Avoir, bool Brouillon, bool Annulee, string Enseigne, IReadOnlyList<string> Adresse,
    string AlAttention, string RefCommande, DateOnly? DateCommande, string Interlocuteur, string RefExportSociete, string RefFournisseur,
    string TvaClient, string Representant, string ModeReglement, string Echeance, int CompteGeneral, int CompteClient, string Tiers,
    string Devise, (int Numero, int Annee)? Origine);

internal sealed record ElementEditionFacture(int Numero, int Dossier, string Cle, string Titre, IReadOnlyList<(string Texte, bool Gras)> Textes);

internal sealed record LigneEditionFacture(int Element, string Reference, decimal Quantite, string Designation, string Unite, decimal Remise,
    decimal PrixUnitaire, decimal Montant, bool Texte);

internal sealed record BanqueFacture(string Nom, string Iban, string Bic);

internal sealed record DonneesEditionFacture(
    EnTeteEditionFacture EnTete, IReadOnlyList<ElementEditionFacture> Elements, IReadOnlyList<LigneEditionFacture> Lignes,
    IReadOnlyList<string> TextesPied, IReadOnlyList<string> MentionsGauche, IReadOnlyList<string> MentionsDroite, BanqueFacture? Banque,
    LibellesFacture L);

// Libellés de la facture : français (tableau LIB de FAC202P5), sinon traduction PRT008 (TRADUCn → libellé, FAC202P5 l. 625-720)
internal sealed record LibellesFacture(
    string RefCommande, string DateCommande, string Attention, string NumeroAvoir, string DateFacture, string NumeroFacture, string Reference,
    string Quantite, string Designation, string Unite, string PrixUnitaire, string Montant, string Dossier, string TauxTva, string Tva,
    string Port, string NetAPayer, string ModeReglement, string Annulee, string Facture, string AvoirTitre, string Ht, string Ttc,
    string RefFournisseur, string VotreRefCee, string Totaux, string Acompte, string VotreCommande, string VotreRef, string BaseHt,
    string NumeroClient, string Echeance, string AuComptant)
{
    public static readonly LibellesFacture Francais = new("Réf. commande", "Date cde", "À l'attention de", "N° avoir", "Date de facture",
        "N° facture", "Référence", "Quantité", "Désignation", "Unité", "P.U. H.T.", "Total H.T.", "Dossier", "Taux T.V.A.", "T.V.A.", "Port",
        "Net à payer", "Mode de règlement", "Facture annulée", "Facture", "Avoir", "H.T.", "T.T.C.", "Réf. fournisseur", "Votre n° TVA", "Totaux",
        "Acompte", "V/cde", "V/réf.", "Base H.T.", "N° client", "Échéance", "Au comptant");

    // traductions : TRADUC1 à TRADUC50 (index 0 = TRADUC1) ; libellé vide = français
    public static LibellesFacture Pour(IReadOnlyList<string> traductions)
    {
        var f = Francais;
        string T(int n, string defaut) => n <= traductions.Count && traductions[n - 1].Trim().TrimEnd(':', ' ') is { Length: > 0 } t ? t : defaut;
        return new LibellesFacture(T(1, f.RefCommande), T(3, f.DateCommande), T(5, f.Attention), T(11, f.NumeroAvoir), T(13, f.DateFacture),
            T(15, f.NumeroFacture), T(19, f.Reference), T(21, f.Quantite), T(23, f.Designation), T(25, f.Unite), T(27, f.PrixUnitaire),
            T(29, f.Montant), T(31, f.Dossier), T(4, f.TauxTva), T(8, f.Tva), T(10, f.Port), T(12, f.NetAPayer), T(14, f.ModeReglement),
            T(16, f.Annulee), T(15, f.Facture), T(11, f.AvoirTitre), T(34, f.Ht), T(37, f.Ttc), T(38, f.RefFournisseur), T(40, f.VotreRefCee),
            T(42, f.Totaux), T(44, f.Acompte), T(46, f.VotreCommande), T(48, f.VotreRef), T(6, f.BaseHt), T(41, f.NumeroClient), f.Echeance,
            T(26, f.AuComptant));
    }
}

internal static class ReglesEditionFacture
{
    // Facture éditée en définitif : HT, port, escompte et TVA tels qu'enregistrés (FAC080) et échéance enregistrée ; ventilation
    // par taux du calcul si elle redonne la même TVA, sinon une seule ligne (facture multi-taux éditée par le RPG à un taux)
    public static TotauxFacture TotauxEnregistres(TotauxFacture calcul, Donnees.FacturePourCalcul f)
    {
        var e = f.Enregistre;
        var ttc = e.Ht - e.Escompte + e.PortTaxable + e.PortExonere + e.Tva;
        var tva = calcul.TotalTva == e.Tva ? calcul.Tva
            : [new TvaParTaux(calcul.Tva.FirstOrDefault()?.Taux ?? 0m, e.Ht - e.Escompte + e.PortTaxable, e.Tva)];
        return new TotauxFacture(e.Ht, e.PortTaxable, e.PortExonere, e.Escompte, tva, e.Tva, ttc, calcul.Acompte, ttc - calcul.Acompte,
            f.Conditions.Echeance ?? calcul.Echeance);
    }

    // N° imprimé : préfixé de l'année sur 2 chiffres et d'un point si FAC010.PDESEC (position 2) = O (« 26.050001 »)
    public static string NumeroImprime(int numero, DateOnly date, bool prefixeAnnee) =>
        prefixeAnnee ? $"{date.Year % 100:00}.{numero:000000}" : numero.ToString();

    // Échéance imprimée (FAC202P5 r. 572-580, 787-800) : rien pour un avoir ; « sur relevé » ; au comptant si échéance = date
    public static string Echeance(bool avoir, DateOnly date, DateOnly echeance, bool surReleve, LibellesFacture l) =>
        avoir ? "" : surReleve ? $"Sur relevé au {echeance:dd/MM/yyyy}" : echeance == date ? l.AuComptant : $"{l.Echeance} : {echeance:dd/MM/yyyy}";
}
