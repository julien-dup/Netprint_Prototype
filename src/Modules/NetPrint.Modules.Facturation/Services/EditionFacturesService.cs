using NetPrint.Modules.Facturation.Contrats;
using NetPrint.Modules.Facturation.Donnees;
using NetPrint.Modules.Facturation.Impressions;
using NetPrint.Noyau.Societes;
using NetPrint.Socle;
using NetPrint.Socle.Documents;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Facturation.Services;

// =====================================================================
// Édition d'une facture : même périmètre que la consultation (représentant limité), calcul natif (CalculFacture), données de
// l'édition, PDF. N'écrit rien (le RPG posait FDBROU à chaque consultation : anomalie de ConsultationFactures). Facture déjà
// éditée en définitif : totaux de FAC080 (ceux de la comptabilité), le calcul ne sert qu'aux factures pas encore éditées.
// =====================================================================
internal sealed class EditionFacturesService(IConsultationFactures consultation, CalculFacturesDb2 calcul, EditionFacturesDb2 donnees,
    ISocietes societes, IUtilisateurCourant utilisateur) : IEditionFactures
{
    public async Task<DocumentGenere> EditerAsync(int numero, DateOnly date, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        _ = await consultation.LireAsync(numero, date, ct) ?? throw new RegleMetierException($"La facture {numero} du {date:dd/MM/yyyy} n'existe pas ou ne vous est pas accessible.");
        var aCalculer = await calcul.LireAsync(u.Societe, numero, date, ct)
                        ?? throw new RegleMetierException($"La facture {numero} n'existe plus.");
        var totaux = CalculFacture.Calculer(aCalculer.Lignes, aCalculer.Conditions);
        if (aCalculer.Editee) totaux = ReglesEditionFacture.TotauxEnregistres(totaux, aCalculer);
        var lues = await donnees.LireAsync(u.Societe, numero, date, totaux.Echeance, ct)
                   ?? throw new RegleMetierException($"La facture {numero} n'existe plus.");
        var societe = await societes.LireIdentiteAsync(u.Societe, lues.EnTete.Enseigne, DocumentFacture.TypeLogo, ct);
        var nom = $"{(lues.EnTete.Avoir ? "Avoir" : "Facture")} {lues.EnTete.NumeroImprime}.pdf";
        return await Task.Run(() => DocumentsPdf.Generer(nom, new DocumentFacture(lues, totaux, societe)), ct);
    }
}
