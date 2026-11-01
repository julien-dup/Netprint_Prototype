using NetPrint.Modules.Facturation.Contrats;

namespace NetPrint.Modules.Facturation.Services;

// Contexte du contrôle des critères : société (AA0007 : enseignes SOP03, périodes ouvertes SPED1-SPEF1 / SPED2-SPEF2 en AAAAMM),
// paramètres de facturation FAC010 (null = absents) et existence de l'enseigne saisie (PRT320F)
internal sealed record ContexteLancement(bool GereEnseignes, bool EnseigneExiste, IReadOnlyList<(int Debut, int Fin)> Periodes,
    int? TypeNumerotation)
{
    public bool PeriodeOuverte(DateOnly d) => Periodes.Any(p => d.Year * 100 + d.Month >= p.Debut && d.Year * 100 + d.Month <= p.Fin);
}

// =====================================================================
// Contrôle des critères de la facturation automatique (FAC400, R3 à R11). Les bornes absentes sont complétées comme le RPG :
// BL de fin 999999, date d'expédition de fin = aujourd'hui, comptes auxiliaires 0 à 9999999.
// =====================================================================
internal static class ReglesFacturationAuto
{
    public const int BlMaxi = 999999, CompteMaxi = 9999999, CompteGeneralMaxi = 999999;

    public static IReadOnlyList<ErreurLancement> Controler(CriteresFacturationAuto c, ContexteLancement x, DateOnly aujourdhui)
    {
        var erreurs = new List<ErreurLancement>();
        void Erreur(string champ, string message) => erreurs.Add(new(champ, message));

        c.Enseigne = c.Enseigne.Trim().ToUpperInvariant();
        if (x.GereEnseignes && c.Enseigne == "") Erreur(nameof(c.Enseigne), "L'enseigne est obligatoire.");
        else if (x.GereEnseignes && !x.EnseigneExiste) Erreur(nameof(c.Enseigne), $"L'enseigne {c.Enseigne} n'existe pas.");
        else if (!x.GereEnseignes) c.Enseigne = "";

        if (c.BlDu is null && c.BlAu is null && c.ExpedieDu is null && c.ExpedieAu is null)
            Erreur(nameof(c.BlDu), "Indiquez une plage de BL : des n° ou des dates d'expédition.");
        else
        {
            if (c.BlDu is not null || c.BlAu is not null)
            {
                c.BlDu ??= 0;
                c.BlAu ??= BlMaxi;
                if (c.BlDu > c.BlAu) Erreur(nameof(c.BlAu), "Le n° de BL de fin est avant celui de début.");
            }
            if (c.ExpedieDu is not null || c.ExpedieAu is not null)
            {
                c.ExpedieAu ??= aujourdhui;
                if (c.ExpedieDu > c.ExpedieAu) Erreur(nameof(c.ExpedieAu), "La date d'expédition de fin est avant celle de début.");
            }
        }

        c.CompteGeneralDu ??= 0;
        c.CompteGeneralAu ??= CompteGeneralMaxi;
        if (c.CompteGeneralDu > c.CompteGeneralAu) Erreur(nameof(c.CompteGeneralAu), "Le compte général de fin est avant celui de début.");
        c.CompteClientDu ??= 0;
        c.CompteClientAu ??= CompteMaxi;
        if (c.CompteClientDu > c.CompteClientAu) Erreur(nameof(c.CompteClientAu), "Le compte client de fin est avant celui de début.");

        if (c.DateFacture is not { } d) Erreur(nameof(c.DateFacture), "La date des factures est obligatoire.");
        else if (!x.PeriodeOuverte(d))
            Erreur(nameof(c.DateFacture), "La date des factures n'est pas dans une période comptable ouverte de la société.");

        if (x.TypeNumerotation is null) Erreur("", "Les paramètres de facturation de la société (FAC010) n'existent pas.");
        else if (x.TypeNumerotation == 3 && !x.GereEnseignes)
            Erreur("", "La numérotation des factures par enseigne demande une société gérée par enseigne (FAC010 / AA0007).");

        c.Frequence = (c.Frequence ?? "").Trim().ToUpperInvariant();
        if (FrequencesFacturation.Toutes.All(f => f.Code != c.Frequence)) Erreur(nameof(c.Frequence), "Fréquence inconnue.");
        return erreurs;
    }
}
