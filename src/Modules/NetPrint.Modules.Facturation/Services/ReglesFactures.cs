using NetPrint.Modules.Facturation.Contrats;

namespace NetPrint.Modules.Facturation.Services;

// =====================================================================
// Règles de lecture des factures, sans base (docs/facturation/fiches/ConsultationFactures.md § 6). Montants stockés positifs ;
// avoir (FDTYPE = A) affiché en négatif ; ligne de remise (FAC020.LTYPE = R) aussi (les deux se cumulent, comme le WinDev).
// =====================================================================
internal static class ReglesFactures
{
    // TTC = HT + port taxable + port exonéré - escompte + TVA (l'acompte n'est pas déduit, comme ConsultationFactures)
    public static decimal Ttc(decimal ht, decimal portTaxable, decimal portExonere, decimal escompte, decimal tva) =>
        ht + portTaxable + portExonere - escompte + tva;

    // FDEDIT : O à éditer, E éditée (FDCOMP autre que N : et comptabilisée, comme ConsultationFactures), A en attente
    public static string Statut(string edition, string comptabilisee) => edition switch
    {
        "O" => StatutsFacture.AEditer,
        "E" => comptabilisee == "N" ? StatutsFacture.Editee : StatutsFacture.Comptabilisee,
        "A" => StatutsFacture.Attente,
        _ => edition,
    };

    // Envoi (EnvoideFactures) : FDTAGE = O envoyée (date, manuel ou automatique, canal et format), FDNONE = O ignorée
    public static string Envoi(string envoyee, string ignoree, int date, string mode, string methode, string format)
    {
        if (ignoree == "O") return "Ignorée";
        if (envoyee != "O") return "";
        var jour = date > 0 && Socle.Donnees.BaseDb2.DateAAAAMMJJ(date) is { } d ? $" le {d:dd/MM/yyyy}" : "";
        var canal = methode switch { "M" => "e-mail", "F" => "FTP", "W" => "plateforme", _ => "" };
        var details = string.Join(", ", new[] { mode switch { "M" => "manuel", "A" => "automatique", _ => "" }, canal, format.Trim() }.Where(x => x != ""));
        return $"Envoyée{jour}" + (details == "" ? "" : $" ({details})");
    }

    // Lignes : lignes de texte (code 9 ou type T) rattachées à la ligne qui les précède ; montants signés
    public static List<LigneFacture> Lignes(bool avoir, IReadOnlyList<LigneFacture> brutes)
    {
        var resultat = new List<LigneFacture>();
        foreach (var l in brutes)
        {
            var texte = l.CodeLigne == "9" || l.TypeLigne == "T";
            if (texte && resultat.Count > 0 && resultat[^1].Element == l.Element)
            {
                resultat[^1] = resultat[^1] with { Textes = [.. resultat[^1].Textes, l.Designation] };
                continue;
            }
            var signeQuantite = avoir ? -1 : 1;
            var signeMontant = signeQuantite * (l.TypeLigne == "R" ? -1 : 1);
            resultat.Add(l with { Quantite = l.Quantite * signeQuantite, PrixUnitaire = l.PrixUnitaire * signeMontant, Montant = l.Montant * signeMontant });
        }
        return resultat;
    }
}
