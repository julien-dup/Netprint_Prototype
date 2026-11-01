using NetPrint.Modules.Facturation.Contrats;
using NetPrint.Modules.Facturation.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Facturation.Services;

// =====================================================================
// Saisie d'une facture à partir de BL (FAC085 → FAC150) : clé contrôlée, BL facturables du donneur d'ordre (même lecture que la
// facturation automatique, sans l'exigence « à prendre en automatique »), facture préparée par le moteur commun (une commande =
// un élément), corrigée à l'écran, puis créée par la même écriture que la facturation automatique (une transaction, n° compris).
// Modification d'une facture pas encore éditée (FAC150 mode M) : en-tête et lignes, changement de date reporté sur tous ses fichiers.
// Suppression d'une facture pas encore éditée ou d'un de ses éléments (FAC160 / FAC165) ; facture éditée non comptabilisée remise
// « à éditer » (FAC166) pour la modifier. Droits : interdiction PRT000P1 sur FAC150 pour saisir (total ou création), modifier ou
// remettre à éditer (total ou modification), supprimer (total ou suppression), sur FAC200 pour éditer en définitif ; représentant
// limité : ses factures seulement. Ajout de BL à une facture pas encore éditée (FAC085 / FAC150 mode A) : droit de création (comme
// l'option AE de FAC080S), nouveaux éléments écrits comme à la création. Copie (FAC150 mode D) : droits de création et de
// modification (la copie s'ouvre en modification ; le RPG ne contrôlait que la modification). Pas encore repris : facture sans BL.
// =====================================================================
internal sealed class SaisieFacturesService(FacturationAutoDb2 bls, SaisieFacturesDb2 donnees, EditionDefinitiveDb2 definitive,
    DroitsFacturationDb2 droits, ConsultationFacturesDb2 consultation, IUtilisateurCourant utilisateur) : ISaisieFactures
{
    public const string ProgrammeSaisie = "FAC150";
    public const int Maxi = 10000;
    public const int BlsMaxi = 500;   // BL d'une même facture (paramètres de la relecture)

    private enum ActionFacture { Creation = 2, Modification = 3, Suppression = 4 }

    // PRT000P1 : « ** » en option 1 (total) ou en option de l'action (2 création, 3 modification, 4 suppression) l'interdit
    private async Task<UtilisateurConnecte> ExigerDroitAsync(CancellationToken ct, ActionFacture action = ActionFacture.Creation)
    {
        var u = await utilisateur.ExigerAsync();
        if (await droits.OptionsProgrammeAsync(ProgrammeSaisie, u.Profil, ct) is { } o
            && (o.ElementAtOrDefault(0) == "**" || o.ElementAtOrDefault((int)action - 1) == "**"))
            throw new RegleMetierException($"Vous n'avez pas le droit de {action switch
            {
                ActionFacture.Modification => "modifier",
                ActionFacture.Suppression => "supprimer",
                _ => "créer",
            }} des factures (FAC150).");
        return u;
    }

    // Facture existante : droit de l'action, puis accès (représentant limité : ses factures seulement)
    private async Task<UtilisateurConnecte> ExigerFactureAsync(int numero, DateOnly date, ActionFacture action, CancellationToken ct)
    {
        var u = await ExigerDroitAsync(ct, action);
        if (!await consultation.AccessibleAsync(u.Societe, u.Profil, numero, date, ct))
            throw new RegleMetierException($"Vous n'avez pas accès à la facture {numero} (factures de votre représentant seulement).");
        return u;
    }

    public async Task<bool> SupprimerAsync(int numero, DateOnly date, int? element = null, CancellationToken ct = default)
    {
        var u = await ExigerFactureAsync(numero, date, ActionFacture.Suppression, ct);
        return await bls.SupprimerFactureAsync(u.Societe, numero, date, element, ct);
    }

    public async Task RemettreAEditerAsync(int numero, DateOnly date, CancellationToken ct = default)
    {
        var u = await ExigerFactureAsync(numero, date, ActionFacture.Modification, ct);
        if (!await PeutEditerAsync(u, ct))   // annuler une édition définitive = droit d'éditer en définitif
            throw new RegleMetierException("Vous n'avez pas le droit d'éditer les factures en définitif (FAC200) : vous ne pouvez pas annuler une édition.");
        await bls.RemettreAEditerAsync(u.Societe, numero, date, ct);
    }

    private async Task<bool> PeutEditerAsync(UtilisateurConnecte u, CancellationToken ct) => !EditionFacturesService.EditionInterdite(
        await droits.OptionsProgrammeAsync(EditionFacturesService.ProgrammeEdition, u.Profil, ct));

    // Édition définitive enchaînée (si demandée et sans anomalie) ; un échec n'annule pas la facture enregistrée
    private async Task<FactureCreee> TerminerAsync(UtilisateurConnecte u, int numero, DateOnly date, bool aControler, bool editerDefinitif,
        CancellationToken ct)
    {
        if (!editerDefinitif || aControler) return new FactureCreee(numero, date, aControler, false, null);
        try
        {
            await definitive.EditerAsync(u.Societe, numero, date, DateOnly.FromDateTime(DateTime.Today), ct);
            return new FactureCreee(numero, date, false, true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new FactureCreee(numero, date, false, false, $"Facture enregistrée, mais non éditée en définitif : {ex.Message}");
        }
    }

    public async Task<FactureSaisie> OuvrirAsync(int numero, DateOnly date, CancellationToken ct = default)
    {
        var u = await ExigerFactureAsync(numero, date, ActionFacture.Modification, ct);
        var f = await bls.LireFactureAsync(u.Societe, numero, date, ct)
                ?? throw new RegleMetierException($"La facture {numero} du {date:dd/MM/yyyy} n'existe pas.");
        if (f.Edition == "E") throw new RegleMetierException($"La facture {numero} est déjà éditée : elle ne se modifie plus (faites un avoir).");
        if (f.Edition == "S") throw new RegleMetierException($"La facture {numero} est supprimée.");
        if (f.Edition is not ("O" or "A")) throw new RegleMetierException($"La facture {numero} n'est ni « à éditer » ni « à contrôler » : elle ne se modifie pas ici.");
        var p = await bls.ParametresAsync(u.Societe, ct);
        var (modes, representants) = await donnees.ListesAsync(u.Societe, ct);
        var c = f.Conditions;
        return new FactureSaisie
        {
            Numero = numero, DateOrigine = date, Date = date, Avoir = f.Avoir, CompteGeneral = f.CompteGeneral, CompteClient = f.CompteClient,
            NomClient = f.NomClient,
            CompteGeneralFacture = f.CompteClientFacture == 0 ? null : f.CompteGeneralFacture,
            CompteClientFacture = f.CompteClientFacture == 0 ? null : f.CompteClientFacture,
            ModeReglement = c.Mode, NombreJours = c.NombreJours, Le = c.Le, Arrete = c.Arrete, Escompte = c.Escompte,
            Representant = f.Representant == 0 ? null : f.Representant, Reference = f.Reference, AlAttention = f.AlAttention,
            Acompte = f.Acompte, PeutEditerDefinitif = await PeutEditerAsync(u, ct),
            Elements = [.. f.Elements.Select(e => new ElementSaisie
            {
                Numero = e.Numero, Commande = e.Commande, Cle = e.Cle, Designation = e.Designation, Bls = e.Bls,
                Monnaie = e.Lignes.Select(l => l.Monnaie).FirstOrDefault(m => m.Trim() != "") ?? e.Monnaie,
                // Prix à 0 déjà accepté : facture « à éditer », ou article (gratuit de la commande, comme en automatique)
                Lignes = [.. e.Lignes.Select(l => SaisieFacture.VersLigne(l, p, l.Pu == 0 && (f.Edition == "O" || l.Article.Trim() != "")))],
            })],
            CodesLigne = SaisieFacture.CodesLigne(p),
            Unites = p.Diviseurs, ModesReglement = modes, Representants = representants,
        };
    }

    public async Task<FactureCreee> ModifierAsync(FactureSaisie s, bool editerDefinitif, CancellationToken ct = default)
    {
        if (s.Numero is not { } numero || s.DateOrigine is not { } origine) throw new RegleMetierException("Cette facture n'est pas une facture existante.");
        var u = await ExigerFactureAsync(numero, origine, ActionFacture.Modification, ct);
        if (editerDefinitif && !await PeutEditerAsync(u, ct))
            throw new RegleMetierException("Vous n'avez pas le droit d'éditer les factures en définitif (FAC200).");
        var controle = SaisieFacture.Recalculer(s);
        if (controle.Erreurs.Count > 0) throw new RegleMetierException(string.Join("\n", controle.Erreurs.Select(e => e.Message)));
        var date = s.Date!.Value;

        var lue = await bls.LireFactureAsync(u.Societe, numero, origine, ct)
                  ?? throw new RegleMetierException($"La facture {numero} du {origine:dd/MM/yyyy} n'existe plus.");
        if (lue.Elements.Select(e => e.Numero).Order().SequenceEqual(s.Elements.Select(e => e.Numero).Order()) is false)
            throw new RegleMetierException("Les éléments de la facture ont changé depuis son ouverture : rouvrez-la.");
        var contexte = await bls.ContexteAsync(u.Societe, lue.Enseigne, ct);
        // Nouvelle date seulement : une facture restée « à éditer » d'une période close se corrige encore
        if (date != origine && !contexte.PeriodeOuverte(date))
            throw new RegleMetierException("La nouvelle date n'est pas dans une période comptable ouverte de la société.");
        if (contexte.TypeNumerotation is { } type && !(type == 3 && contexte.GereEnseignes)
            && SaisieFacture.ControlerNouvelleDate(type, origine, date) is { } refus)
            throw new RegleMetierException(refus);
        // Client facturé inchangé (même vide) : zones FAC080 gardées telles quelles, adresse figée comprise
        int gf = s.CompteGeneralFacture ?? 0, cf = s.CompteClientFacture ?? 0;
        var avant = lue.CompteClientFacture == 0 ? (0, 0) : (lue.CompteGeneralFacture, lue.CompteClientFacture);
        var clientChange = (gf, cf) != avant;
        if (!clientChange) (gf, cf) = (lue.CompteGeneralFacture, lue.CompteClientFacture);
        else if (cf != 0 && !await donnees.ClientExisteAsync(u.Societe, gf, cf, ct))
            throw new RegleMetierException($"Le client facturé {gf} {cf} n'existe pas.");

        var p = SaisieFacture.Parametres(s);
        var elements = s.Elements.Select(e =>
        {
            var (ca, ventes, lignes) = SaisieFacture.Lignes(e, p);
            return new ElementModifie(e.Numero, ca, ventes, lignes);
        }).ToList();
        var escompte = Math.Truncate(s.Escompte * 10m) / 10m;   // FDESC sur 2,1
        var conditions = new ConditionsReglement(s.ModeReglement, s.NombreJours, s.Le, escompte, s.Arrete);
        var echeance = CalculFacture.Echeance(new ConditionsFacture(s.Avoir, date, escompte, s.Acompte, s.Le, s.NombreJours, s.Arrete, null));
        var aControler = controle.Anomalies.Count > 0;
        await bls.ModifierFactureAsync(u.Societe, new ModificationFacture(numero, origine, date, gf, cf, clientChange, conditions, echeance, s.Representant ?? 0, s.Reference.Trim(),
            s.AlAttention.Trim(), SaisieFacture.CodeVente(elements.SelectMany(e => e.Lignes), p), aControler, elements), ct);
        return await TerminerAsync(u, numero, date, aControler, editerDefinitif, ct);
    }

    public async Task<BlsAFacturer> ChercherBlsAsync(CleFacturation cle, CancellationToken ct = default)
    {
        var u = await ExigerDroitAsync(ct);
        var cles = new[] { cle.Bl is not null, cle.Commande is not null, cle.CompteGeneral is not null || cle.CompteClient is not null }.Count(x => x);
        if (cles != 1) return Refus(new ErreurLancement("", "Indiquez un BL, une commande ou un client, et un seul."));
        if (cle.CompteGeneral is null != cle.CompteClient is null)
            return Refus(new ErreurLancement(nameof(cle.CompteGeneral), "Indiquez le compte général et le compte client."));

        var r = await donnees.ResoudreAsync(u.Societe, cle, ct);
        if (r.Erreur is not null) return Refus(r.Erreur);
        var (retenus, ecartes, tronque) = await bls.LireManuelAsync(u.Societe, r.Commande is null ? (r.CompteGeneral, r.CompteClient) : null,
            r.Commande, null, Maxi, ct);
        var tout = r.Commande is not null;   // clé commande : tous ses BL cochés
        return new BlsAFacturer([], r.CompteGeneral, r.CompteClient, r.NomClient,
            [.. retenus.Select(e => new BlAFacturer(e.Bl, e.Element, e.Expedition, e.Dossier.Numero, e.Dossier.Cle, e.Dossier.Designation1,
                e.Dossier.Reference, e.QuantiteLivree, e.Retour, tout || r.Proposes.Contains((e.Bl, e.Element))))
                .OrderByDescending(b => b.Propose).ThenByDescending(b => b.Bl).ThenBy(b => b.Element)],   // cochés d'abord, puis les plus récents
            [.. ecartes.Select(e => new BlEcarte(e.Bl, e.Element, e.Dossier, e.Cle, e.CompteGeneral, e.CompteClient, e.Nom, e.Raison))], tronque);

        static BlsAFacturer Refus(ErreurLancement e) => new([e], 0, 0, "", [], [], false);
    }

    // Éléments de BL choisis relus : tous doivent être encore facturables, pas encore facturés, avec leur commande
    private async Task<(FacturePrevue Facture, ParametresGeneration Parametres, string Enseigne, string Nom)> RelireAsync(int societe,
        IReadOnlyList<ElementDeBl> choisis, CancellationToken ct)
    {
        if (choisis.Count == 0) throw new RegleMetierException("Cochez au moins un BL à facturer.");
        var numeros = choisis.Select(b => b.Bl).Distinct().ToList();
        if (numeros.Count > BlsMaxi) throw new RegleMetierException($"Une facture saisie porte au plus {BlsMaxi} BL.");
        var (retenus, ecartes, _) = await bls.LireManuelAsync(societe, null, null, numeros, Maxi, ct);
        var voulus = choisis.Select(b => (b.Bl, b.Element)).ToHashSet();
        foreach (var e in ecartes.Where(e => voulus.Contains((e.Bl, e.Element))))
            throw new RegleMetierException($"Le BL {e.Bl} (élément {e.Element}) ne peut pas être facturé : {e.Raison}.");
        var lus = retenus.Where(e => voulus.Contains((e.Bl, e.Element))).ToList();
        var absent = choisis.FirstOrDefault(b => !lus.Any(e => e.Bl == b.Bl && e.Element == b.Element));
        if (absent is not null)
            throw new RegleMetierException($"Le BL {absent.Bl} (élément {absent.Element}) n'est plus à facturer (facturé ou modifié entre-temps).");
        var enseignes = lus.Select(e => e.Enseigne).Distinct().ToList();
        if (enseignes.Count > 1) throw new RegleMetierException("Les BL choisis ne sont pas tous de la même enseigne.");
        var parametres = await bls.ParametresAsync(societe, ct);
        return (GenerationFactures.Regrouper(lus, parametres), parametres, enseignes[0], lus[0].NomClient);
    }

    public async Task<FactureSaisie> PreparerAsync(IReadOnlyList<ElementDeBl> choisis, CancellationToken ct = default)
    {
        var u = await ExigerDroitAsync(ct);
        var (f, p, _, nom) = await RelireAsync(u.Societe, choisis, ct);
        var (modes, representants) = await donnees.ListesAsync(u.Societe, ct);
        return SaisieFacture.VersSaisie(f, choisis, nom, p, DateOnly.FromDateTime(DateTime.Today), modes, representants, await PeutEditerAsync(u, ct));
    }

    public ControleFactureSaisie Recalculer(FactureSaisie facture) => SaisieFacture.Recalculer(facture);

    // Facture à compléter : existante, pas encore éditée, accessible (FAC085 : facture inexistante, éditée ou supprimée refusée)
    private async Task<(UtilisateurConnecte U, FactureEnregistree F)> FactureACompleterAsync(int numero, DateOnly date, CancellationToken ct)
    {
        var u = await ExigerFactureAsync(numero, date, ActionFacture.Creation, ct);
        var f = await bls.LireFactureAsync(u.Societe, numero, date, ct)
                ?? throw new RegleMetierException($"La facture {numero} du {date:dd/MM/yyyy} n'existe pas.");
        if (f.Edition is not ("O" or "A"))
            throw new RegleMetierException($"La facture {numero} est {(f.Edition == "S" ? "supprimée" : "éditée")} : on ne peut plus lui ajouter de BL.");
        return (u, f);
    }

    public async Task<BlsAFacturer> ChercherBlsAjoutAsync(int numero, DateOnly date, CancellationToken ct = default)
    {
        var (_, f) = await FactureACompleterAsync(numero, date, ct);
        var r = await ChercherBlsAsync(new CleFacturation { CompteGeneral = f.CompteGeneral, CompteClient = f.CompteClient }, ct);
        return r with { Bls = [.. r.Bls.Where(b => b.Retour == f.Avoir).Select(b => b with { Propose = false })] };
    }

    // BL relus et regroupés pour la facture à compléter : même donneur d'ordre, même type, même enseigne
    private async Task<(UtilisateurConnecte U, FacturePrevue Facture, ParametresGeneration Parametres, string Nom)> RelireAjoutAsync(int numero,
        DateOnly date, IReadOnlyList<ElementDeBl> choisis, CancellationToken ct)
    {
        var (u, fe) = await FactureACompleterAsync(numero, date, ct);
        var (f, p, enseigne, nom) = await RelireAsync(u.Societe, choisis, ct);
        if ((f.CompteGeneral, f.CompteClient) != (fe.CompteGeneral, fe.CompteClient))
            throw new RegleMetierException($"Les BL choisis ne sont pas du donneur d'ordre de la facture {numero} ({fe.CompteGeneral} {fe.CompteClient}).");
        if (f.Avoir != fe.Avoir)
            throw new RegleMetierException(f.Avoir ? $"Des retours (avoir) ne s'ajoutent pas à la facture {numero}." : $"Des BL livrés ne s'ajoutent pas à l'avoir {numero}.");
        if (enseigne.Trim() != fe.Enseigne.Trim())
            throw new RegleMetierException($"Les BL choisis ne sont pas de l'enseigne de la facture {numero} ({fe.Enseigne.Trim()}).");
        return (u, f, p, nom);
    }

    public async Task<FactureSaisie> PreparerAjoutAsync(int numero, DateOnly date, IReadOnlyList<ElementDeBl> choisis, CancellationToken ct = default)
    {
        var (u, f, p, nom) = await RelireAjoutAsync(numero, date, choisis, ct);
        var (modes, representants) = await donnees.ListesAsync(u.Societe, ct);
        return SaisieFacture.VersSaisie(f, choisis, nom, p, date, modes, representants, await PeutEditerAsync(u, ct), ajoutA: numero);
    }

    public async Task<FactureCreee> AjouterAsync(FactureSaisie s, bool editerDefinitif, CancellationToken ct = default)
    {
        if (!s.Ajout || s.Numero is not { } numero || s.DateOrigine is not { } date)
            throw new RegleMetierException("Cette saisie n'est pas un ajout à une facture existante.");
        var controle = SaisieFacture.Recalculer(s);
        if (controle.Erreurs.Count > 0) throw new RegleMetierException(string.Join("\n", controle.Erreurs.Select(e => e.Message)));
        var (u, relue, p, _) = await RelireAjoutAsync(numero, date, s.Bls, ct);
        if (editerDefinitif && !await PeutEditerAsync(u, ct))
            throw new RegleMetierException("Vous n'avez pas le droit d'éditer les factures en définitif (FAC200).");
        if (SaisieFacture.Empreinte(relue) != s.Empreinte)
            throw new RegleMetierException("Les BL ont changé depuis la préparation (lignes, prix ou commande) : recommencez.");
        var f = SaisieFacture.Appliquer(relue, s, p, controle.Anomalies);
        var aControler = await bls.AjouterElementsAsync(u.Societe, numero, date, f, ct);
        return await TerminerAsync(u, numero, date, aControler, editerDefinitif, ct);
    }

    public async Task<FactureCreee> CopierAsync(int numero, DateOnly date, CancellationToken ct = default)
    {
        var u = await ExigerFactureAsync(numero, date, ActionFacture.Creation, ct);
        await ExigerDroitAsync(ct, ActionFacture.Modification);   // la copie s'ouvre en modification pour être corrigée
        var f = await bls.LireFactureAsync(u.Societe, numero, date, ct)
                ?? throw new RegleMetierException($"La facture {numero} du {date:dd/MM/yyyy} n'existe pas.");
        var jour = DateOnly.FromDateTime(DateTime.Today);
        var contexte = await bls.ContexteAsync(u.Societe, f.Enseigne, ct);
        if (!contexte.PeriodeOuverte(jour))
            throw new RegleMetierException("La date du jour n'est pas dans une période comptable ouverte de la société.");
        if (contexte.TypeNumerotation is not { } type) throw new RegleMetierException("Les paramètres de facturation de la société (FAC010) n'existent pas.");
        if (contexte.GereEnseignes && type == 3 && f.Enseigne.Trim() == "")
            throw new RegleMetierException("La numérotation est par enseigne et la facture n'a pas d'enseigne.");
        var c = f.Conditions;
        var echeance = CalculFacture.Echeance(new ConditionsFacture(f.Avoir, jour, c.Escompte, 0, c.Le, c.NombreJours, c.Arrete, null));
        // Compteur absent : CompteurFacturesAbsentException levée à l'écriture (transaction annulée), l'écran propose de le créer
        var (copie, aControler) = await bls.CopierFactureAsync(u.Societe, u.Profil, new NumerotationFactures(type, contexte.GereEnseignes, f.Enseigne),
            numero, date, jour, echeance, ct);
        return new FactureCreee(copie, jour, aControler, false, null);
    }

    public async Task<FactureCreee> CreerAsync(FactureSaisie s, bool editerDefinitif, CancellationToken ct = default)
    {
        var u = await ExigerDroitAsync(ct);
        if (editerDefinitif && EditionFacturesService.EditionInterdite(
                await droits.OptionsProgrammeAsync(EditionFacturesService.ProgrammeEdition, u.Profil, ct)))
            throw new RegleMetierException("Vous n'avez pas le droit d'éditer les factures en définitif (FAC200).");
        var controle = SaisieFacture.Recalculer(s);
        if (controle.Erreurs.Count > 0) throw new RegleMetierException(string.Join("\n", controle.Erreurs.Select(e => e.Message)));
        var date = s.Date!.Value;

        var (relue, p, enseigne, _) = await RelireAsync(u.Societe, s.Bls, ct);
        if (SaisieFacture.Empreinte(relue) != s.Empreinte)
            throw new RegleMetierException("Les BL ont changé depuis la préparation de la facture (lignes, prix ou commande) : recommencez.");
        var contexte = await bls.ContexteAsync(u.Societe, enseigne, ct);
        if (!contexte.PeriodeOuverte(date))
            throw new RegleMetierException("La date de la facture n'est pas dans une période comptable ouverte de la société.");
        if (contexte.TypeNumerotation is not { } type) throw new RegleMetierException("Les paramètres de facturation de la société (FAC010) n'existent pas.");
        if (contexte.GereEnseignes && type == 3 && enseigne.Trim() == "")
            throw new RegleMetierException("La numérotation est par enseigne et les BL n'ont pas d'enseigne.");
        if (s.CompteGeneralFacture is { } gf && s.CompteClientFacture is { } cf && !await donnees.ClientExisteAsync(u.Societe, gf, cf, ct))
            throw new RegleMetierException($"Le client facturé {gf} {cf} n'existe pas.");

        // Compteur absent : CompteurFacturesAbsentException levée à l'écriture (transaction annulée), l'écran propose de le créer
        var numerotation = new NumerotationFactures(type, contexte.GereEnseignes, enseigne);
        var f = SaisieFacture.Appliquer(relue, s, p, controle.Anomalies);
        var numero = await bls.EcrireAsync(u.Societe, u.Profil, numerotation, date, f, ct);
        return await TerminerAsync(u, numero, date, f.AControler, editerDefinitif, ct);
    }
}
