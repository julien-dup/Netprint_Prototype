using NetPrint.Modules.Facturation.Contrats;
using NetPrint.Modules.Facturation.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Facturation.Services;

// =====================================================================
// Création d'un compteur de factures manquant (reprise de la création de l'écran FAC040), proposée par la facturation quand
// le compteur de l'année ou du mois n'existe pas (David, 01/11). Droit : interdiction PRT000P1 sur FAC040 (total ou création).
// Le mois doit correspondre à la numérotation de la société : 1 à 12 en mensuelle, 0 en annuelle.
// =====================================================================
internal sealed class CompteursFacturesService(FacturationAutoDb2 donnees, DroitsFacturationDb2 droits, IUtilisateurCourant utilisateur)
    : ICompteursFactures
{
    public const string ProgrammeCompteurs = "FAC040";

    public async Task CreerAsync(int annee, int mois, int premier, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        if (await droits.OptionsProgrammeAsync(ProgrammeCompteurs, u.Profil, ct) is [var total, var creation, ..] && (total == "**" || creation == "**"))
            throw new RegleMetierException("Vous n'avez pas le droit de créer les compteurs de factures (FAC040).");
        if (premier is < 1 or > 999999) throw new RegleMetierException("Le premier n° de facture va de 1 à 999 999.");
        if (annee is < 2000 or > 2099) throw new RegleMetierException("Année du compteur invalide.");
        var type = (await donnees.ContexteAsync(u.Societe, "", ct)).TypeNumerotation
                   ?? throw new RegleMetierException("Les paramètres de facturation de la société (FAC010) n'existent pas.");
        if (type == 2 ? mois is < 1 or > 12 : type == 1 ? mois != 0 : true)
            throw new RegleMetierException("Ce compteur ne correspond pas à la numérotation des factures de la société.");
        await donnees.CreerCompteurAsync(u.Societe, annee, mois, premier, ct);
    }
}
