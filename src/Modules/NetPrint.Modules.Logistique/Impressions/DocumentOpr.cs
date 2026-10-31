using NetPrint.Modules.Logistique.Services;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using static NetPrint.Socle.Documents.DocumentsPdf;

namespace NetPrint.Modules.Logistique.Impressions;

// =====================================================================
// Mise en page de l'ordre de préparation (reprise modernisée d'OPR150PR). En-tête : date et heure d'édition, « uniquement
// les ajouts », code-barres Code 39 du n° d'OPR, n° et initiales, date de départ, adresse de livraison, observations et
// commentaire de l'OPR. Une page par date et adresse (ou par commande, option DOS005.POPRSDP). Par commande : client,
// contact, référence ; par ligne : article (code-barres), désignation, quantité, norme, conditionnement, textes, maxi
// livrable, BL, observations, transporteur, groupage, lots, poids, stock, fabrication, options. Cases à remplir en bas.
// =====================================================================
internal sealed class DocumentOpr(EnteteEditionOpr entete, IReadOnlyList<PageEditionOpr> pages) : IDocument
{
    public void Compose(IDocumentContainer conteneur)
    {
        if (pages.Count == 0)
        {
            conteneur.Page(page =>
            {
                page.PageStandard(false);
                page.Header().Element(c => Entete(c, null));
                page.Content().AlignCenter().PaddingTop(60)
                    .Text(entete.AjoutsSeulement ? "Aucune ligne ajoutée depuis la dernière édition." : "Aucune ligne à préparer à imprimer.").Italic();
            });
            return;
        }
        foreach (var p in pages) conteneur.Page(page =>
        {
            page.PageStandard(false);
            page.Header().Element(c => Entete(c, p));
            page.Content().PaddingTop(8).Element(c => Contenu(c, p));
            page.Footer().Element(c => Pied(c, p));
        });
    }

    private void Entete(IContainer c, PageEditionOpr? p) => c.Column(col =>
    {
        col.Item().Row(r =>
        {
            r.RelativeItem().Text($"Le {entete.Edition:dd/MM/yyyy} à {entete.Edition:HH:mm}").FontSize(7).FontColor(Couleurs.Discret);
            if (entete.AjoutsSeulement) r.RelativeItem().AlignCenter().Text("** Uniquement les ajouts **").Bold();
        });
        col.Item().PaddingTop(4).Row(r =>
        {
            r.ConstantItem(130).Column(g =>
            {
                g.Item().MaxWidth(125).Height(26).Svg(CodeBarres.Code39Svg(entete.Numero.ToString())).FitArea();
                g.Item().Text(entete.Numero.ToString()).FontSize(7).FontColor(Couleurs.Discret);
            });
            r.RelativeItem().Column(g =>
            {
                g.Item().Text(t =>
                {
                    t.Span("ORDRE DE PRÉPARATION ").Bold().FontSize(13).FontColor(Couleurs.Accent);
                    t.Span($"{entete.Numero}").Bold().FontSize(13);
                    if (entete.Initiales != "") t.Span($" / {entete.Initiales}").FontSize(11);
                });
                if (p is not null)
                    g.Item().Text(p.Depart is { } d ? $"Départ le {DateLongue(d)}" : "Sans date de départ").Bold().FontSize(12);
            });
        });
        if (p is null) return;
        col.Item().PaddingTop(6).Row(r =>
        {
            r.RelativeItem().Border(0.6f).BorderColor(Couleurs.Trait).Padding(6).Column(a =>
            {
                a.Item().Text("Livraison").FontSize(7).FontColor(Couleurs.Discret);
                if (p.Adresse is null) a.Item().Text("OPR manuel, sans adresse").Italic();
                else
                {
                    var ad = p.Adresse;
                    a.Item().Text($"{ad.FormeJuridique} {ad.RaisonSociale}".Trim() + (ad.Numero > 0 ? $" ({ad.Numero})" : "")).Bold().FontSize(10);
                    foreach (var l in new[] { ad.Rue1, ad.Rue2 }.Where(x => x != "")) a.Item().Text(l);
                    a.Item().Text($"{ad.CodePostal} {ad.Ville}".Trim()).Bold();
                    if (p.Pays != "") a.Item().Text(p.Pays);
                }
            });
            r.ConstantItem(8);
            r.RelativeItem().Border(0.6f).BorderColor(Couleurs.Trait).Padding(6).Column(a =>
            {
                a.Item().Text("Observations").FontSize(7).FontColor(Couleurs.Discret);
                foreach (var o in p.Observations) a.Item().Text(o).Bold();
                foreach (var t in entete.Textes) a.Item().Text(t).Bold();
            });
        });
    });

    private static void Contenu(IContainer c, PageEditionOpr p) => c.Column(col =>
    {
        col.Spacing(8);
        foreach (var cde in p.Commandes)
            col.Item().Border(0.6f).BorderColor(Couleurs.Trait).Column(bloc =>
            {
                bloc.Item().Background(Couleurs.Fond).Padding(4).Column(e =>
                {
                    e.Item().Text(t =>
                    {
                        if (cde.Manuelle) t.Span("Ordre de préparation manuel").Bold();
                        else
                        {
                            t.Span($"Client {cde.Client}").Bold();
                            t.Span($"  ({cde.CompteGeneral} {cde.CompteClient})").FontColor(Couleurs.Discret);
                        }
                    });
                    e.Item().Text(t =>
                    {
                        t.Span($"Commande {cde.Dossier}/{cde.Cle}").Bold().FontSize(11);
                        if (cde.Reference != "") t.Span($"   réf. {cde.Reference}{(cde.DateCommande is { } d ? $" du {d:dd/MM/yyyy}" : "")}");
                        if (cde.Contact != "") t.Span($"   contact : {cde.Contact}");
                    });
                });
                bloc.Item().BorderTop(0.5f).BorderColor(Couleurs.Trait).PaddingHorizontal(4).PaddingVertical(2).Row(r =>
                {
                    r.RelativeItem().Text("Article · désignation").FontSize(7).FontColor(Couleurs.Discret);
                    r.ConstantItem(80).AlignRight().Text("Quantité").FontSize(7).FontColor(Couleurs.Discret);
                    r.ConstantItem(12);
                    r.ConstantItem(130).Text("Conditionnement").FontSize(7).FontColor(Couleurs.Discret);
                });
                foreach (var l in cde.Lignes) bloc.Item().BorderTop(0.5f).BorderColor(Couleurs.Trait).Padding(4).Element(x => Ligne(x, l));
            });
    });

    private static void Ligne(IContainer c, LigneEditionOprImprimee l) => c.Column(col =>
    {
        col.Item().Row(r =>
        {
            r.RelativeItem().Column(g =>
            {
                g.Item().Text(t =>
                {
                    t.Span(l.Article).Bold();
                    if (l.Norme != "") t.Span($"   norme {l.Norme}").Bold().FontColor(Couleurs.Accent);
                });
                g.Item().Text(l.Designation);
                foreach (var i in l.Infos) g.Item().Text(i).FontSize(8).Italic();
                foreach (var tx in l.TextesCommande) g.Item().Text(tx).FontSize(8);
                if (l.Observations.Count > 0)
                {
                    g.Item().PaddingTop(2).Text("Observations :").FontSize(7).FontColor(Couleurs.Discret);
                    foreach (var o in l.Observations) g.Item().Text(o).FontSize(8);
                }
            });
            r.ConstantItem(80).AlignRight().Column(g =>
            {
                g.Item().AlignRight().Text(l.Quantite).Bold().FontSize(11);
                if (l.MaxiLivrable != "") g.Item().AlignRight().Text(l.MaxiLivrable).FontSize(7);
                if (l.Livre != "") g.Item().AlignRight().Text(l.Livre).Bold().FontColor(Colors.Red.Darken2);
            });
            r.ConstantItem(12);
            r.ConstantItem(130).Column(g =>
            {
                foreach (var co in l.Conditionnement) g.Item().Text(co).FontSize(8);
                if (l.CodeBarre != "") g.Item().PaddingTop(2).MaxWidth(125).Height(18).Svg(CodeBarres.Code39Svg(l.CodeBarre)).FitArea();
            });
        });
        if (l.Transporteur != "" || l.Groupage != "")
            col.Item().PaddingTop(2).Row(r =>
            {
                r.RelativeItem().Text(l.Transporteur).FontSize(8);
                if (l.Groupage != "") r.ConstantItem(130).Text(l.Groupage).Bold();
            });
        if (l.Lots.Count > 0 || l.Poids != "")
            col.Item().PaddingTop(2).Row(r =>
            {
                r.RelativeItem().Column(g =>
                {
                    foreach (var (titre, lots) in l.Lots)
                        g.Item().Text(t =>
                        {
                            t.Span($"{titre} : ").FontSize(8).FontColor(Couleurs.Discret);
                            t.Span(string.Join("   ", lots)).FontSize(8).Bold();
                        });
                });
                if (l.Poids != "") r.ConstantItem(130).Text(l.Poids).FontSize(8);
            });
        if (l.Stock.Count > 0 || l.Fabrication.Count > 0)
            col.Item().PaddingTop(2).Row(r =>
            {
                r.RelativeItem().Column(g => { foreach (var s in l.Stock) g.Item().Text(s).FontSize(8); });
                r.ConstantItem(12);
                r.RelativeItem().Column(g => { foreach (var f in l.Fabrication) g.Item().Text(f).FontSize(8).Italic(); });
            });
        if (l.Options != "" || l.Mentions != "" || l.SousTraitance != "")
            col.Item().PaddingTop(2).Row(r =>
            {
                r.RelativeItem().Text(l.Options).FontSize(8).Italic();
                if (l.Mentions != "") r.AutoItem().PaddingLeft(8).Text(l.Mentions).Bold().FontColor(Couleurs.Accent);
                if (l.SousTraitance != "") r.AutoItem().PaddingLeft(8).Text(l.SousTraitance).Bold();
            });
    });

    // Cases à remplir à la main (OPR15015), poids théorique en rappel (entre parenthèses s'il manque des données de poids)
    private static void Pied(IContainer c, PageEditionOpr p) => c.Column(col =>
    {
        col.Item().Row(r =>
        {
            void Case(string libelle, string valeur) => r.RelativeItem().Border(0.6f).BorderColor(Couleurs.Trait).Padding(4).Column(g =>
            {
                g.Item().Text(libelle).FontSize(7).FontColor(Couleurs.Discret);
                g.Item().MinHeight(14).Text(valeur).FontSize(8).Italic().FontColor(Couleurs.Discret);
            });
            Case("Opérateur", "");
            Case("Date", "");
            Case("Poids", p.ImprimerPoids && p.Poids > 0 ? (p.PoidsIncomplet ? $"({p.Poids:#,##0} kg)" : $"{p.Poids:#,##0} kg") : "");
            Case("Colis", "");
            Case("Palettes", "");
            Case("Transporteur", "");
        });
        col.Item().AlignRight().Text(t =>
        {
            t.DefaultTextStyle(s => s.FontSize(7).FontColor(Couleurs.Discret));
            t.Span("Page ");
            t.CurrentPageNumber();
            t.Span(" / ");
            t.TotalPages();
        });
    });
}
