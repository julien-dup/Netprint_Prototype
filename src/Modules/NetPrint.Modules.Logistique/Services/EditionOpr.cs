using System.Globalization;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Modules.Logistique.Impressions;
using NetPrint.Socle;
using NetPrint.Socle.Documents;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Logistique.Services;

// =====================================================================
// Édition d'un OPR (reprise d'OPR150 en PDF natif). Mêmes informations, chacune à sa place : l'état RPG superposait
// plusieurs zones (norme et emballage, n° de BL et conditionnement, code-barres et colis, groupage, options…), tronquait le
// n° d'OPR et le code article du code-barres, reportait des valeurs d'une ligne à l'autre et doublait lots bloqués et
// fabrications ; ici chaque bloc a sa colonne et chaque ligne ses propres valeurs.
// =====================================================================
internal sealed record EnteteEditionOpr(int Numero, string Initiales, bool AjoutsSeulement, IReadOnlyList<string> Textes, DateTime Edition);

// Une page (ou plusieurs) par date de départ et adresse de livraison ; avec « page par commande », une par commande
internal sealed record PageEditionOpr(
    DateOnly? Depart, AdresseBl? Adresse, string Pays, IReadOnlyList<string> Observations, IReadOnlyList<CommandeEditionOpr> Commandes,
    int Poids, bool PoidsIncomplet, bool ImprimerPoids);

internal sealed record CommandeEditionOpr(
    int Dossier, string Cle, bool Manuelle, string Client, int CompteGeneral, int CompteClient, string Contact, string Reference,
    DateOnly? DateCommande, IReadOnlyList<LigneEditionOprImprimee> Lignes);

internal sealed record LigneEditionOprImprimee(
    string Article, string Designation, string Quantite, string Norme, IReadOnlyList<string> Conditionnement, IReadOnlyList<string> Infos,
    IReadOnlyList<string> TextesCommande, string MaxiLivrable, string Livre, IReadOnlyList<string> Observations, string CodeBarre,
    string Transporteur, string Groupage, IReadOnlyList<(string Titre, IReadOnlyList<string> Lots)> Lots, string Poids, IReadOnlyList<string> Stock,
    IReadOnlyList<string> Fabrication, string Options, string Mentions, string SousTraitance);

internal static class ModeleOpr
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private static string N(int v) => v.ToString("#,##0", Fr);

    public static (EnteteEditionOpr Entete, List<PageEditionOpr> Pages) Construire(DonneesEditionOpr d, bool ajoutsSeulement, DateTime maintenant)
    {
        var entete = new EnteteEditionOpr(d.Numero, d.Initiales, ajoutsSeulement, [.. d.Textes.Where(t => t != "")], maintenant);
        var pages = new List<PageEditionOpr>();
        foreach (var groupe in d.Lignes.GroupBy(l => (l.DateDepart, l.CompteGeneral, l.CompteClient, Cle: CleAdresse(l.Adresse))))
        {
            var premiere = groupe.First();
            var commandes = groupe.GroupBy(l => (l.Dossier, l.Cle)).Select(c => Commande([.. c])).ToList();
            var poids = groupe.Select(Poids).ToList();
            var parPage = d.PageParCommande ? commandes.Select(c => (IReadOnlyList<CommandeEditionOpr>)[c]).ToList() : [commandes];
            // Observations de livraison de toutes les lignes de l'adresse, une fois chacune
            var observations = groupe.SelectMany(l => l.ObservationsLivraison).Distinct().ToList();
            var pays = groupe.Select(l => l.LibellePays).FirstOrDefault(x => x != "") ?? "";
            foreach (var cdes in parPage)
                pages.Add(new PageEditionOpr(premiere.DateDepart, premiere.Adresse, pays, observations, cdes,
                    poids.Sum(p => p ?? 0), poids.Any(p => p is null), d.ImprimerPoids));
        }
        return (entete, pages);
    }

    private static string CleAdresse(AdresseBl? a) =>
        a is null ? "" : string.Join("|", a.Numero, a.RaisonSociale.Trim(), a.Rue1.Trim(), a.Rue2.Trim(), a.CodePostal.Trim(), a.Ville.Trim()).ToUpperInvariant();

    private static CommandeEditionOpr Commande(List<LigneEditionOpr> lignes)
    {
        var l0 = lignes[0];
        var manuelle = l0.Dossier == OprDb2.CommandeManuelle;
        return new CommandeEditionOpr(l0.Dossier, l0.Cle, manuelle, l0.NomClient, l0.CompteGeneral, l0.CompteClient, l0.Contact, l0.ReferenceCommande,
            l0.DateCommande, [.. lignes.Select(Ligne)]);
    }

    // Poids théorique d'une ligne (OPR150) : quantité ÷ emballage × poids de l'UC, tronqué au kg ; null si l'article n'a pas
    // les données de poids (le total de la page est alors « incomplet », entre parenthèses)
    public static int? Poids(LigneEditionOpr l) =>
        l.EmballagePar != 0 && l.PoidsUc != 0 ? (int)Math.Truncate(l.Quantite / (decimal)l.EmballagePar * l.PoidsUc) : null;

    // Colonne « Conditionnement » d'OPR150, chaque valeur sur sa ligne : emballage unitaire, cartons, palettes, colis
    public static List<string> Conditionnement(int quantite, string typeEmballage, int emballagePar, int ucParCarton, int colisParPalette)
    {
        var r = new List<string>();
        var nom = typeEmballage switch { "P" => "paquet", "C" => "carton", _ => "" };
        if (nom != "" && emballagePar != 0) r.Add($"{nom} de {N(emballagePar)} ex");
        if (ucParCarton != 0 && (typeEmballage != "C" || ucParCarton != 1))
            r.Add($"carton de {ucParCarton} {(nom == "" ? "UC" : nom + "s")}");
        var colis = 0;
        if (emballagePar != 0)
        {
            colis = Plafond(quantite, emballagePar);
            if (ucParCarton != 0) colis = Plafond(colis, ucParCarton);
        }
        if (colisParPalette != 0)
        {
            var palettes = colis != 0 ? Plafond(colis, colisParPalette) : 0;
            r.Add(palettes != 0 ? $"Mise sur {palettes} palette{(palettes > 1 ? "s" : "")}" : "Mise sur palette");
        }
        if (colis != 0) r.Add($"Soit {N(colis)} colis");
        return r;
    }

    private static int Plafond(int a, int b) => b <= 0 ? 0 : (a + b - 1) / b;

    private static LigneEditionOprImprimee Ligne(LigneEditionOpr l)
    {
        var infos = new List<string>();
        if (l.RefImprime != "") infos.Add($"Réf. imprimé {l.RefImprime}");
        if (l.Appel != "") infos.Add($"Appel n° {l.Appel}");
        var lots = new List<(string, IReadOnlyList<string>)>();
        if (l.LotsBloques.Count > 0) lots.Add(("Lots bloqués", l.LotsBloques));
        if (l.LotsPrelevement.Count > 0) lots.Add(("Avec prélèvements", l.LotsPrelevement));
        if (l.LotsAffectes.Count > 0) lots.Add(("Lots affectés", l.LotsAffectes));
        var poids = Poids(l);
        var stock = new List<string>();
        foreach (var depot in l.Stock)
        {
            stock.Add($"{(depot.Principal ? "Stock" : $"Stock dépôt {depot.Depot}")} : {N(depot.Quantite)} {l.Unite}");
            stock.AddRange(depot.Positions.Select(p => $"   lot {p.Lot}{(p.Emplacement != "" ? $" · empl. {p.Emplacement}" : "")} = {N(p.Quantite)} {l.Unite}"));
        }
        var fabrication = new List<string>();
        if (l.EnFabrication.Count > 0)
        {
            fabrication.Add($"En fab{(l.CommandeFournisseur != 0 ? $" (commandé {N(l.CommandeFournisseur)})" : "")} :");
            fabrication.AddRange(l.EnFabrication.Select(f => $"   {f.Lot} = {N(f.Quantite)} {l.Unite}"));
        }
        var options = new List<string>();
        if (l.LargeurFormat > 0) options.Add($"Format {l.LargeurFormat.ToString("0.##", Fr)} × {l.HauteurFormat.ToString("0.##", Fr)}");
        if (l.MatiereUc > 0) options.Add($"Matière UC n° {l.MatiereUc}");
        if (l.ASolder) options.Add("À solder");
        if (l.Mecompte) options.Add("Mécompte autorisé");
        var mentions = new List<string>();
        if (l.Enlevement) mentions.Add("ENLÈVEMENT CLIENT");
        if (l.Imperatif) mentions.Add("DÉLAI IMPÉRATIF");
        if (l.Acompte) mentions.Add("ACOMPTE");
        return new LigneEditionOprImprimee(
            l.Indice == "" ? l.Article : $"{l.Article} {l.Indice}", l.Designation, $"{N(l.Quantite)} {l.Unite}", l.Norme,
            Conditionnement(l.Quantite, l.TypeEmballage, l.EmballagePar, l.UcParCarton, l.ColisParPalette), infos, l.TextesCommande,
            l.MaxiLivrable > 0 ? $"Maxi livrable : {N(l.MaxiLivrable)}" : "", l.Bl > 0 ? $"LIVRÉ BL {l.Bl}" : "", l.Observations,
            BonLivraison.Code39Possible(l.Indice == "" ? l.Article : $"{l.Article} {l.Indice}") ? (l.Indice == "" ? l.Article : $"{l.Article} {l.Indice}") : "",
            l.Transporteur != 0 ? $"Transporteur : {l.Transporteur} {l.NomTransporteur}".Trim() : "", l.Groupage != 0 ? $"Groupage : {l.Groupage}" : "",
            lots, poids is > 0 ? $"Poids théo. : {N(poids.Value)} kg" : "", stock, fabrication, string.Join(" / ", options), string.Join(" / ", mentions),
            l.SousTraitance != "" ? $"STR : {l.SousTraitance}" : "");
    }
}

// Édition PDF d'un OPR : société et profil de la session (représentant limité : OPR de ses commandes) ; produire le PDF n'écrit
// rien, « Imprimer » marque les lignes éditées (ODEDIT) comme OPR150 à la fin de l'impression
internal sealed class EditionOprService(OprDb2 donnees, IUtilisateurCourant utilisateur) : IEditionOpr
{
    public async Task<DocumentGenere> EditerAsync(int numeroOpr, bool ajoutsSeulement, CancellationToken ct = default)
    {
        var u = await AccesAsync(numeroOpr, ct);
        return (await PdfAsync(u, numeroOpr, ajoutsSeulement, ct)).Pdf;
    }

    public async Task MarquerEditeAsync(int numeroOpr, CancellationToken ct = default)
    {
        var u = await AccesAsync(numeroOpr, ct);
        await donnees.MarquerEditeAsync(u.Societe, numeroOpr, null, ct);
    }

    // Une ligne ajoutée entre-temps par un autre poste n'est pas marquée : elle sortira à la prochaine édition des ajouts
    public async Task<DocumentGenere> ImprimerAjoutsAsync(int numeroOpr, CancellationToken ct = default)
    {
        var u = await AccesAsync(numeroOpr, ct);
        var (pdf, lignes) = await PdfAsync(u, numeroOpr, true, ct);
        if (lignes.Count > 0) await donnees.MarquerEditeAsync(u.Societe, numeroOpr, lignes, ct);
        return pdf;
    }

    private async Task<(DocumentGenere Pdf, IReadOnlyList<int> Lignes)> PdfAsync(UtilisateurConnecte u, int numeroOpr, bool ajoutsSeulement,
        CancellationToken ct)
    {
        var d = await donnees.EditionAsync(u.Societe, numeroOpr, ajoutsSeulement, ct)
            ?? throw new RegleMetierException($"L'OPR {numeroOpr} n'existe pas.");
        var (entete, pages) = ModeleOpr.Construire(d, ajoutsSeulement, DateTime.Now);
        var pdf = await Task.Run(() => DocumentsPdf.Generer($"OPR {numeroOpr}{(ajoutsSeulement ? " ajouts" : "")}.pdf", new DocumentOpr(entete, pages)), ct);
        return (pdf, [.. d.Lignes.Select(l => l.NumeroLigne)]);
    }

    private async Task<UtilisateurConnecte> AccesAsync(int numeroOpr, CancellationToken ct)
    {
        var u = await utilisateur.ExigerAsync();
        var droits = ReglesOpr.Droits(await donnees.OptionsProgrammeAsync(ReglesOpr.Programme, u.Profil, ct));
        if (!droits.Acces) throw new RegleMetierException("Vous n'êtes pas autorisé à la gestion des OPR.");
        if (!droits.Editer) throw new RegleMetierException("Vous n'êtes pas autorisé à éditer les OPR.");
        _ = await donnees.LireAsync(u.Societe, numeroOpr, await donnees.RepresentantImposeAsync(u.Societe, u.Profil, ct), ct)
            ?? throw new RegleMetierException($"L'OPR {numeroOpr} n'existe pas ou ne vous est pas accessible.");
        return u;
    }
}
