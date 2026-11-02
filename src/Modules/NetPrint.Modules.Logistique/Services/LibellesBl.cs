using System.Globalization;
using NetPrint.Socle.Documents;

namespace NetPrint.Modules.Logistique.Services;

// =====================================================================
// Libellés du BL : français, ou traductions de la langue du client (AA0003.CDOP10) comme EXP035P2 :
// PRT008 (programme « BL », zones TRADUC1 à 50) pour les libellés fixes, PRT009 pour les listes fixes (28 unités de
// conditionnement, 30 modes d'expédition, 31 types de port, 32 états de livraison). Une traduction absente garde le français.
// =====================================================================
internal sealed record LibellesBl(
    bool Francais, string Titre, string TitreRetour, string Expediteur, string Destinataire, string ExpedieLe, string Page,
    string Port, string Livraison, string Transporteur, string Telephone, string Fax, string NotreCee, string VotreCee,
    string RefFournisseur, string Representant, string Reference, string Quantite, string Designation, string Etat, string Colis,
    string Poids, string Commande, string CommandeClient, string Colisage, string PoidsTotal, string NombreColis, string Palettes,
    string Kg, string Observations, string RecuPar, string Date, string Signature, string Appel, string Lot, string VotreRef,
    string NotreRef, string De, string Du, string VotreCommande, string CommandeDu, string NotreCommande, string RefCommande,
    IReadOnlyDictionary<(int Liste, string Code), string> Listes)
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static readonly LibellesBl EnFrancais = new(
        true, "Bon de livraison", "Retour de marchandises", "Expéditeur", "Livré à", "Expédié le", "Page", "Port", "Livraison",
        "Transporteur", "Tél.", "Fax", "Notre n° CEE", "Votre n° CEE", "Réf. fournisseur", "Représentant", "Référence", "Quantité",
        "Désignation", "État", "Colis", "Poids", "Commande", "Commande du client", "Colisage", "Poids total", "Nombre de colis",
        "Palettes", "kg", "Observations à la réception", "Reçu par", "Date", "Signature", "N° d'appel", "N° lot", "V/Réf.",
        "N/Réf.", "de", "du", "V/cde", "Cde du", "N/cde", "Réf. cde",
        new Dictionary<(int, string), string>());

    // traductions : TRADUC1 à TRADUC50 de PRT008 (vide = français) ; listes : PRT009 de la langue
    public static LibellesBl Pour(IReadOnlyList<string> traductions, IReadOnlyDictionary<(int, string), string> listes)
    {
        if (traductions.All(x => x == "") && listes.Count == 0) return EnFrancais;
        var f = EnFrancais;
        // Libellé n (1 à 50), sans les « : » des zones RPG ; à défaut le français
        string T(int n, string defaut) =>
            n <= traductions.Count && traductions[n - 1].Trim(' ', ':', '.') is { Length: > 0 } t ? t : defaut;
        return new LibellesBl(false, T(1, f.Titre), T(37, f.TitreRetour), T(3, f.Expediteur), T(5, f.Destinataire), T(26, f.ExpedieLe),
            T(15, f.Page), T(13, f.Port), T(17, f.Livraison), T(11, f.Transporteur), T(7, f.Telephone), T(9, f.Fax), T(28, f.NotreCee),
            T(30, f.VotreCee), T(43, f.RefFournisseur), f.Representant, T(19, f.Reference), T(21, f.Quantite), T(23, f.Designation),
            T(25, f.Etat), T(27, f.Colis), T(29, f.Poids), T(31, f.Commande), T(41, f.CommandeClient), T(2, f.Colisage),
            T(8, f.PoidsTotal), T(4, f.NombreColis), T(22, f.Palettes), T(10, f.Kg), T(42, f.Observations), T(14, f.RecuPar),
            T(39, f.Date), T(16, f.Signature), T(45, f.Appel), T(47, f.Lot), T(34, f.VotreRef), T(36, f.NotreRef), T(49, f.De),
            T(49, f.Du), T(44, f.VotreCommande), T(46, f.CommandeDu), T(48, f.NotreCommande), T(50, f.RefCommande), listes);
    }

    // Valeur d'une liste fixe traduite, à défaut le libellé français
    public string Liste(int liste, string code, string francais) => Listes.GetValueOrDefault((liste, code)) is { Length: > 0 } t ? t : francais;

    // Dates : longue en français (« 10 novembre 2023 »), sinon jj/mm/aaaa
    public string DateLongue(DateOnly d) => Francais ? DocumentsPdf.DateLongue(d) : d.ToString("dd/MM/yyyy", Fr);
}
