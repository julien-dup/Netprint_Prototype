using NetPrint.Modules.Logistique.Services;
using NetPrint.Socle.Documents;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using static NetPrint.Socle.Documents.DocumentsPdf;

namespace NetPrint.Modules.Logistique.Impressions;

// =====================================================================
// Mise en page de la fiche de préparation (reprise modernisée d'EXP078PR) : une page (ou plus) par jour × client × adresse ;
// en-tête « Départ le », destinataire, transporteur ; commandes et lignes avec conditionnement, textes, lots en stock, en
// fabrication, code-barres Code 39 de l'article ; bandeau si le client est bloqué ; cases opérateur / date / poids / colis /
// palettes / transporteur à remplir à la main.
// =====================================================================
// tronque : sélection limitée à 10 000 lignes, signalé en tête de chaque fiche
internal sealed class DocumentPreparation(IReadOnlyList<FichePreparation> fiches, DateOnly du, DateOnly au, bool tronque = false) : IDocument
{
    public void Compose(IDocumentContainer conteneur)
    {
        if (fiches.Count == 0)
        {
            conteneur.Page(page =>
            {
                page.PageStandard(false);
                page.Content().AlignCenter().PaddingTop(60).Text($"Aucune commande à préparer du {du:dd/MM/yyyy} au {au:dd/MM/yyyy}.").Italic();
            });
            return;
        }
        foreach (var f in fiches) conteneur.Page(page =>
        {
            page.PageStandard(false);
            page.Header().Element(c => Entete(c, f, tronque));
            page.Content().PaddingTop(8).Element(c => Contenu(c, f));
            page.Footer().Element(c => Pied(c, f));
        });
    }

    private static void Entete(IContainer c, FichePreparation f, bool tronque) => c.Column(col =>
    {
        if (tronque)
            col.Item().PaddingBottom(4).Background(Colors.Orange.Lighten4).Padding(4).AlignCenter()
                .Text("SÉLECTION INCOMPLÈTE (plus de 10 000 lignes) : réduisez la période ou précisez les critères").Bold();
        col.Item().Row(r =>
        {
            r.RelativeItem().Column(g =>
            {
                g.Item().Text("PRÉPARATION DE COMMANDE").Bold().FontSize(11).FontColor(Couleurs.Accent);
                g.Item().Text(f.Date is { } d ? $"Départ le {DateLongue(d)}" : "Sans date de livraison").Bold().FontSize(14);
                if (f.Transporteur != "") g.Item().Text($"Transporteur : {f.Transporteur}");
            });
            r.ConstantItem(250).Border(0.6f).BorderColor(Couleurs.Trait).Padding(8).Column(a =>
            {
                a.Item().Text(f.Client).FontSize(8).FontColor(Couleurs.Discret);
                a.Item().Text(f.Adresse.RaisonSociale).Bold().FontSize(10);
                foreach (var l in new[] { f.Adresse.Rue1, f.Adresse.Rue2, $"{f.Adresse.CodePostal} {f.Adresse.Ville}".Trim(), f.Adresse.Pays }.Where(x => x != ""))
                    a.Item().Text(l);
                foreach (var co in f.Commentaires) a.Item().Text(co).FontSize(8).Italic();
            });
        });
        if (f.ClientBloque)
            col.Item().PaddingTop(4).Background(Colors.Red.Lighten4).Padding(4).AlignCenter()
                .Text("CLIENT BLOQUÉ — NE PAS EXPÉDIER SANS ACCORD").Bold().FontColor(Colors.Red.Darken3);
    });

    private static void Contenu(IContainer c, FichePreparation f) => c.Column(col =>
    {
        col.Spacing(8);
        foreach (var cde in f.Commandes)
            col.Item().Border(0.6f).BorderColor(Couleurs.Trait).Column(bloc =>
            {
                bloc.Item().Background(Couleurs.Fond).Padding(4).Text(t =>
                {
                    t.Span($"Commande {cde.Numero}").Bold();
                    if (cde.ReferenceCommande != "") t.Span($"  · réf. {cde.ReferenceCommande}");
                    if (cde.DateCommande is { } d) t.Span($" du {d:dd/MM/yyyy}");
                    if (cde.Contact != "") t.Span($"  · {cde.Contact}");
                    t.Span(cde.Enlevement ? "   ENLÈVEMENT CLIENT" : "   À LIVRER").Bold().FontColor(Couleurs.Accent);
                });
                foreach (var l in cde.Lignes)
                    bloc.Item().BorderTop(0.5f).BorderColor(Couleurs.Trait).Padding(4).Row(r =>
                    {
                        r.RelativeItem().Column(g =>
                        {
                            g.Item().Text(l.Article).Bold();
                            g.Item().Text(l.Designation);
                            foreach (var tx in l.Textes) g.Item().Text(tx).FontSize(8).FontColor(Couleurs.Discret);
                            if (l.CodeBarre != "") g.Item().PaddingTop(2).MaxWidth(110).Height(18).Svg(CodeBarres.Code39Svg(l.CodeBarre)).FitArea();
                        });
                        r.ConstantItem(60).AlignRight().Text(Quantite(l.Quantite)).Bold().FontSize(11);
                        r.ConstantItem(12);
                        r.ConstantItem(125).Column(g =>
                        {
                            foreach (var co in l.Conditionnement) g.Item().Text(co).FontSize(8);
                        });
                        r.ConstantItem(125).Column(g =>
                        {
                            foreach (var lot in l.Lots) g.Item().Text(lot).FontSize(8);
                            if (l.EnFabrication != "") g.Item().Text(l.EnFabrication).FontSize(8).Italic();
                        });
                    });
            });
    });

    // Cases à remplir à la main (EXP07815), poids, colis et palettes théoriques en rappel
    private static void Pied(IContainer c, FichePreparation f) => c.Column(col =>
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
            Case("Poids", f.Poids > 0 ? $"théorique {Quantite(f.Poids)} kg" : "");
            Case("Colis", f.Colis > 0 ? $"théorique {f.Colis}" : "");
            Case("Palettes", f.Palettes > 0 ? $"théorique {f.Palettes}" : "");
            Case("Transporteur", f.Transporteur);
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
