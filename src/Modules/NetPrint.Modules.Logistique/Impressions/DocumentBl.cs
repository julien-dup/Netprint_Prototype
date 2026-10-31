using NetPrint.Modules.Logistique.Services;
using NetPrint.Noyau.Societes;
using NetPrint.Socle.Documents;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using static NetPrint.Socle.Documents.DocumentsPdf;

namespace NetPrint.Modules.Logistique.Impressions;

// =====================================================================
// Mise en page du bon de livraison (reprise modernisée d'EXP035P2) : en-tête société et logo, destinataire, transport,
// lignes par élément (référence, quantité, désignation, état, colis, poids), puis colisage et zone de réception.
// =====================================================================
internal sealed class DocumentBl(ModeleBl m, IdentiteSociete? societe, bool apercu) : IDocument
{
    // Logos des éditions WinDev propres au BL : LogoBl<soc>…
    public const string TypeLogo = "Bl";

    private string Titre => m.Retour ? "Retour de marchandises" : "Bon de livraison";

    public void Compose(IDocumentContainer conteneur) => conteneur.Page(page =>
    {
        page.PageStandard(apercu);
        page.Header().Element(Entete);
        page.Content().PaddingTop(8).Element(Contenu);
        page.Footer().Element(Pied);
    });

    // BL revendeur : ni logo ni identité de la société, le revendeur est l'expéditeur
    private IdentiteSociete? Emetteur => m.Expediteur is null ? societe : null;

    private void Entete(IContainer c) => c.Row(ligne =>
    {
        ligne.RelativeItem().Column(col =>
        {
            if (m.Expediteur is { } expediteur)
            {
                col.Item().Text("Expéditeur").FontSize(7.5f).FontColor(Couleurs.Discret);
                var premiere = true;
                foreach (var l in expediteur)
                {
                    col.Item().Text(l).FontSize(premiere ? 11 : 8).Gras(premiere);
                    premiere = false;
                }
                return;
            }
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
        ligne.ConstantItem(250).AlignRight().Column(col =>
        {
            col.Item().AlignRight().Text(Titre.ToUpperInvariant()).Bold().FontSize(11).FontColor(Couleurs.Accent);
            col.Item().AlignRight().Text($"N° {m.Numero}").Bold().FontSize(16);
            if (m.Date is { } d) col.Item().AlignRight().Text($"Expédié le {DateLongue(d)}").FontColor(Couleurs.Discret);
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

        // Transport et références (gauche), destinataire (droite, à la place de la fenêtre d'enveloppe)
        col.Item().ShowOnce().Row(ligne =>
        {
            ligne.RelativeItem().Background(Couleurs.Fond).Padding(8).Table(t =>
            {
                t.ColumnsDefinition(d => { d.ConstantColumn(85); d.RelativeColumn(); });
                void Info(string libelle, string valeur)
                {
                    t.Cell().PaddingBottom(2).Text(libelle).FontColor(Couleurs.Discret);
                    t.Cell().PaddingBottom(2).Text(valeur);
                }
                foreach (var (libelle, valeur) in m.Transport) Info(libelle, valeur);
                if (m.RefFournisseur != "") Info("Réf. fournisseur", m.RefFournisseur);
                if (m.Representant != "") Info("Représentant", m.Representant);
            });
            ligne.ConstantItem(14);
            ligne.ConstantItem(230).Border(0.6f).BorderColor(Couleurs.Trait).Padding(10).Column(adr =>
            {
                adr.Item().PaddingBottom(2).Text("Livré à").FontSize(7.5f).FontColor(Couleurs.Discret);
                var premiere = true;
                foreach (var l in m.Destinataire)
                {
                    adr.Item().Text(l).FontSize(10).Gras(premiere);
                    premiere = false;
                }
                foreach (var o in m.Observations) adr.Item().PaddingTop(2).Text(o).FontSize(8).Italic();
            });
        });

        if (m.Retour)
            col.Item().Background(Couleurs.Fond).Padding(4).AlignCenter().Text("RETOUR DE MARCHANDISES").Bold().FontColor(Couleurs.Accent);
        if (m.Introduction.Count > 0) col.Item().ShowOnce().Element(x => Bloc(x, m.Introduction));

        col.Item().Element(Lignes);

        if (m.TextesPied.Count > 0) col.Item().Element(x => Bloc(x, m.TextesPied));
        col.Item().PaddingTop(4).ShowEntire().Element(Reception);
    });

    private static void Bloc(IContainer c, IReadOnlyList<string> lignes) =>
        c.BorderLeft(2).BorderColor(Couleurs.Accent).PaddingLeft(8).Column(col =>
        {
            foreach (var l in lignes) col.Item().Text(l == "" ? " " : l);
        });

    private void Lignes(IContainer c) => c.Table(t =>
    {
        t.ColumnsDefinition(d =>
        {
            d.ConstantColumn(95);
            d.ConstantColumn(55);
            d.RelativeColumn();
            d.ConstantColumn(50);
            d.ConstantColumn(80);
            d.ConstantColumn(42);
        });
        t.Header(h =>
        {
            IContainer Titre(IContainer x) => x.Background(Couleurs.Accent).PaddingVertical(4).PaddingHorizontal(4);
            h.Cell().Element(Titre).Text("Référence").FontColor(Colors.White).Bold();
            h.Cell().Element(Titre).AlignRight().Text("Quantité").FontColor(Colors.White).Bold();
            h.Cell().Element(Titre).Text("Désignation").FontColor(Colors.White).Bold();
            h.Cell().Element(Titre).Text("État").FontColor(Colors.White).Bold();
            h.Cell().Element(Titre).Text("Colis").FontColor(Colors.White).Bold();
            h.Cell().Element(Titre).AlignRight().Text("Poids").FontColor(Colors.White).Bold();
        });

        static IContainer Cellule(IContainer x) => x.BorderBottom(0.5f).BorderColor(Couleurs.Trait).PaddingVertical(3).PaddingHorizontal(4);

        foreach (var g in m.Groupes)
        {
            if (g.Titre != "")
                t.Cell().ColumnSpan(6).Background(Couleurs.Fond).PaddingVertical(3).PaddingHorizontal(4).Text(g.Titre).Bold();
            foreach (var l in g.Lignes)
            {
                t.Cell().Element(Cellule).Text(x =>
                {
                    x.Line(l.Reference).Bold();
                    foreach (var comp in l.Complements) x.Line(comp).FontSize(7.5f).FontColor(Couleurs.Discret);
                });
                t.Cell().Element(Cellule).AlignRight().Text(l.Quantite).Bold();
                t.Cell().Element(Cellule).Text(x =>
                {
                    foreach (var des in l.Designations) x.Line(des);
                    foreach (var det in l.Details) x.Line(det).FontSize(7.5f).FontColor(Couleurs.Discret);
                });
                t.Cell().Element(Cellule).Text(l.Code);
                t.Cell().Element(Cellule).Text(x => { foreach (var co in l.Colis) x.Line(co); });
                t.Cell().Element(Cellule).AlignRight().Text(l.Poids);
            }
            if (g.Textes.Count > 0)
                t.Cell().ColumnSpan(6).PaddingVertical(3).PaddingHorizontal(4).Text(x =>
                {
                    foreach (var tx in g.Textes) x.Line(tx).FontSize(8);
                });
        }
        if (m.Groupes.Count == 0)
            t.Cell().ColumnSpan(6).Padding(8).AlignCenter().Text("Aucune ligne.").Italic().FontColor(Couleurs.Discret);
    });

    // Colisage (totaux d'EXP009, non recalculés) et zone de réception à remplir à la main
    private void Reception(IContainer c) => c.Row(r =>
    {
        r.ConstantItem(200).Border(0.6f).BorderColor(Couleurs.Trait).Padding(6).Column(col =>
        {
            col.Item().Text("Colisage").Bold();
            void Ligne(string libelle, int valeur, string unite)
            {
                if (valeur != 0)
                    col.Item().Row(x =>
                    {
                        x.RelativeItem().Text(libelle);
                        x.ConstantItem(80).AlignRight().Text($"{Quantite(valeur)} {unite}");
                    });
            }
            Ligne("Poids total", m.PoidsTotal, "kg");
            Ligne("Nombre de colis", m.ColisTotal, "colis");
            Ligne("Palettes", m.PalettesTotal, m.PalettesTotal > 1 ? "palettes" : "palette");
        });
        r.ConstantItem(14);
        r.RelativeItem().Border(0.6f).BorderColor(Couleurs.Trait).Padding(6).Column(col =>
        {
            col.Item().Text("Observations à la réception").Bold();
            col.Item().Height(18);
            col.Item().Row(x =>
            {
                x.RelativeItem().Text("Reçu par :").FontColor(Couleurs.Discret);
                x.RelativeItem().Text("Date :").FontColor(Couleurs.Discret);
                x.RelativeItem().Text("Signature :").FontColor(Couleurs.Discret);
            });
            col.Item().Height(16);
        });
    });

    private void Pied(IContainer c) => c.BorderTop(0.5f).BorderColor(Couleurs.Trait).PaddingTop(4).Row(r =>
    {
        r.RelativeItem().Text(Emetteur is not { } s ? "" : $"{s.RaisonSociale} · {s.MentionsLegales}".Trim(' ', '·'))
            .FontSize(7).FontColor(Couleurs.Discret);
        r.ConstantItem(170).AlignRight().Text(t =>
        {
            t.DefaultTextStyle(s => s.FontSize(7).FontColor(Couleurs.Discret));
            t.Span($"{Titre} {m.Numero} · ");
            t.CurrentPageNumber();
            t.Span(" / ");
            t.TotalPages();
        });
    });
}
