using NetPrint.Modules.Ventes.Services;
using NetPrint.Noyau.Societes;
using NetPrint.Socle.Documents;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using static NetPrint.Socle.Documents.DocumentsPdf;

namespace NetPrint.Modules.Ventes.Impressions;

// =====================================================================
// Mise en page de l'ARC (reprise modernisée du fichier d'impression ARC105PR) : en-tête société (remplace l'overlay
// de l'IBM i), références de la commande et adresse du destinataire, textes des canevas (chapitres 1 et 2, en chasse
// fixe comme dans le 5250), lignes regroupées par livraison, total, texte complémentaire, canevas de pied (chapitre 5).
// =====================================================================
internal sealed class DocumentArc(ModeleArc m, IdentiteSociete? societe, bool apercu) : IDocument
{
    // Logos des éditions WinDev propres à l'ARC : LogoArc<soc>…
    public const string TypeLogo = "Arc";

    public void Compose(IDocumentContainer conteneur) => conteneur.Page(page =>
    {
        page.PageStandard(apercu);
        page.Header().Element(Entete);
        page.Content().PaddingTop(8).Element(Contenu);
        page.Footer().Element(Pied);
    });

    private void Entete(IContainer c) => c.Row(ligne =>
    {
        ligne.RelativeItem().Column(col =>
        {
            if (societe?.Logo is { } logo) col.Item().MaxHeight(48).MaxWidth(170).Image(logo).FitArea();
            if (societe is not null)
            {
                col.Item().Text(societe.RaisonSociale).Bold().FontSize(11).FontColor(Couleurs.Accent);
                foreach (var l in new[] { societe.Rue1, societe.Rue2, $"{societe.CodePostal} {societe.Ville}".Trim() }.Where(x => x != ""))
                    col.Item().Text(l).FontSize(8);
                var contact = string.Join(" · ", new[] { societe.Telephone == "" ? "" : $"Tél. {societe.Telephone}", societe.Email, societe.SiteWeb }
                    .Where(x => x != ""));
                if (contact != "") col.Item().Text(contact).FontSize(8).FontColor(Couleurs.Discret);
            }
        });
        ligne.ConstantItem(280).AlignRight().Column(col =>
        {
            col.Item().AlignRight().Text(m.L.Titre.ToUpperInvariant()).Bold().FontSize(11).FontColor(Couleurs.Accent);
            col.Item().AlignRight().Text($"{m.L.Commande} {m.Numero}").Bold().FontSize(11);
            col.Item().AlignRight().Text(DateLongue(m.Date)).FontColor(Couleurs.Discret);
            col.Item().AlignRight().Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(8).FontColor(Couleurs.Discret));
                t.Span("Page ");
                t.CurrentPageNumber();
                t.Span(" / ");
                t.TotalPages();
            });
        });
    });

    private void Contenu(IContainer c) => c.Column(col =>
    {
        col.Spacing(8);

        // Références (gauche) et destinataire (droite, à la place de la fenêtre d'enveloppe), sauf si le canevas les place
        if (!m.EnteteParCanevas) col.Item().ShowOnce().Row(ligne =>
        {
            ligne.RelativeItem().Background(Couleurs.Fond).Padding(8).Table(t =>
            {
                t.ColumnsDefinition(d => { d.ConstantColumn(95); d.RelativeColumn(); });
                foreach (var (libelle, valeur) in m.References)
                {
                    t.Cell().PaddingBottom(2).Text(libelle).FontColor(Couleurs.Discret);
                    t.Cell().PaddingBottom(2).Text(valeur);
                }
            });
            ligne.ConstantItem(14);
            ligne.ConstantItem(220).Border(0.6f).BorderColor(Couleurs.Trait).Padding(10).Column(adr =>
            {
                var premiere = true;
                foreach (var l in m.Destinataire.Lignes)
                {
                    adr.Item().Text(l).FontSize(10).Gras(premiere);
                    premiere = false;
                }
            });
        });

        // Canevas : en-tête (chapitres 1 et 2), en chasse fixe pour garder leurs colonnes
        if (m.Introduction.Count > 0) col.Item().ShowOnce().Element(x => Canevas(x, m.Introduction));
        if (m.TexteHaut.Count > 0) col.Item().Element(x => Complement(x, m.TexteHaut));

        col.Item().Element(Lignes);
        col.Item().AlignRight().Width(260).Element(Total);

        if (m.TexteBas.Count > 0) col.Item().Element(x => Complement(x, m.TexteBas));
        if (m.Pied.Count > 0) col.Item().PaddingTop(6).Element(x => Canevas(x, m.Pied));
    });

    private static void Canevas(IContainer c, IReadOnlyList<LigneTexte> lignes) => c.Column(col =>
    {
        foreach (var l in lignes)
            col.Item().Text(l.Texte == "" ? " " : l.Texte).FontFamily(Polices.ChasseFixe).FontSize(8.5f).Gras(l.Gras);
    });

    private static void Complement(IContainer c, IReadOnlyList<string> lignes) =>
        c.BorderLeft(2).BorderColor(Couleurs.Accent).PaddingLeft(8).Column(col =>
        {
            foreach (var l in lignes) col.Item().Text(l == "" ? " " : l);
        });

    private void Lignes(IContainer c) => c.Table(t =>
    {
        t.ColumnsDefinition(d =>
        {
            d.ConstantColumn(78);
            d.ConstantColumn(58);
            d.RelativeColumn();
            d.ConstantColumn(62);
            d.ConstantColumn(44);
            d.ConstantColumn(66);
        });
        t.Header(h =>
        {
            IContainer Titre(IContainer x) => x.Background(Couleurs.Accent).PaddingVertical(4).PaddingHorizontal(4);
            h.Cell().Element(Titre).Text(m.L.Article).FontColor(Colors.White).Bold();
            h.Cell().Element(Titre).AlignRight().Text(m.L.Quantite).FontColor(Colors.White).Bold();
            h.Cell().Element(Titre).Text(m.L.Designation).FontColor(Colors.White).Bold();
            h.Cell().Element(Titre).AlignRight().Text(m.L.PrixUnitaire).FontColor(Colors.White).Bold();
            h.Cell().Element(Titre).AlignCenter().Text(m.L.Unite).FontColor(Colors.White).Bold();
            h.Cell().Element(Titre).AlignRight().Text(m.L.Montant).FontColor(Colors.White).Bold();
        });

        IContainer Cellule(IContainer x) => m.Cadre
            ? x.BorderBottom(0.5f).BorderColor(Couleurs.Trait).PaddingVertical(3).PaddingHorizontal(4)
            : x.PaddingVertical(3).PaddingHorizontal(4);

        foreach (var g in m.Groupes)
        {
            if (g.Titre != "" || g.Livraison != "")
                t.Cell().ColumnSpan(6).Background(Couleurs.Fond).PaddingVertical(3).PaddingHorizontal(4).Text(x =>
                {
                    x.Span(g.Titre).Bold();
                    if (g.Livraison != "")
                    {
                        x.Span($"   {m.L.Livraison} ").FontColor(Couleurs.Discret);
                        x.Span(g.Livraison);
                    }
                });
            foreach (var l in g.Lignes)
            {
                t.Cell().Element(Cellule).Text(l.Article).Bold();
                t.Cell().Element(Cellule).AlignRight().Text(l.Quantite is { } q ? Quantite(q) : "");
                t.Cell().Element(Cellule).Text(x =>
                {
                    x.Line(l.Designation);
                    foreach (var comp in l.Complements) x.Line(comp).FontSize(7.5f).FontColor(Couleurs.Discret);
                });
                t.Cell().Element(Cellule).AlignRight().Text(x =>
                {
                    if (l.Prix is { } p) x.Line(Prix(p));
                    if (l.PrixAvantRemise is { } r) x.Line(r).FontSize(7).Italic().FontColor(Couleurs.Discret);
                });
                t.Cell().Element(Cellule).AlignCenter().Text(l.Unite);
                t.Cell().Element(Cellule).AlignRight().Text(l.Montant is { } mt ? Montant(mt) : "");
            }
        }
        if (m.Groupes.Count == 0)
            t.Cell().ColumnSpan(6).Padding(8).AlignCenter().Text("Aucune ligne à confirmer.").Italic().FontColor(Couleurs.Discret);
    });

    private void Total(IContainer c) => c.Border(0.6f).BorderColor(Couleurs.Trait).Padding(6).Column(col =>
    {
        void Ligne(string libelle, string valeur, bool gras = false) => col.Item().Row(r =>
        {
            r.RelativeItem().Text(libelle).Gras(gras);
            r.ConstantItem(90).AlignRight().Text(valeur).Gras(gras);
        });
        if (m.TauxRemise != 0)
        {
            Ligne("Valeur totale HT", Montant(m.TotalBrut));
            Ligne($"Remise {m.TauxRemise:0.##} %", Montant(m.NetHt - m.TotalBrut));
        }
        Ligne($"{m.L.NetAPayer} en {m.Devise}", Montant(m.NetHt), gras: true);
    });

    private void Pied(IContainer c) => c.BorderTop(0.5f).BorderColor(Couleurs.Trait).PaddingTop(4).Row(r =>
    {
        r.RelativeItem().Text(societe is null ? "" : $"{societe.RaisonSociale} · {societe.MentionsLegales}".Trim(' ', '·'))
            .FontSize(7).FontColor(Couleurs.Discret);
        r.ConstantItem(170).AlignRight().Text(t =>
        {
            t.DefaultTextStyle(s => s.FontSize(7).FontColor(Couleurs.Discret));
            t.Span($"{m.L.Commande} {m.Numero} · ");
            t.CurrentPageNumber();
            t.Span(" / ");
            t.TotalPages();
        });
    });
}
