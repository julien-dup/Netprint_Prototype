using System.Globalization;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Socle.Documents;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using static NetPrint.Socle.Documents.DocumentsPdf;

namespace NetPrint.Modules.Logistique.Impressions;

// =====================================================================
// Feuilles du pré-BL en paysage : en-tête commun (société, départ, transporteur, adresse, n° de pré-BL en code-barres).
// =====================================================================
internal static class EnTeteFeuille
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static string N(int v) => v.ToString("#,##0", Fr);

    public static void Page(PageDescriptor page)
    {
        page.PageStandard(false);
        page.Size(PageSizes.A4.Landscape());
        page.MarginHorizontal(10, Unit.Millimetre);
        page.DefaultTextStyle(t => t.FontSize(8).FontFamily(Polices.Texte).FontColor(Couleurs.Texte));
    }

    public static void Composer(IContainer c, string titre, string societe, EnTeteFeuillePreBl e, string complement, bool codeBarre) => c.Column(col =>
    {
        col.Item().Row(r =>
        {
            r.RelativeItem().Text(societe).Bold();
            r.RelativeItem().AlignRight().Text($"Imprimé le {DateTime.Now:dd/MM/yyyy à HH:mm}").FontColor(Couleurs.Discret);
        });
        col.Item().PaddingTop(4).Row(r =>
        {
            r.RelativeItem().Column(g =>
            {
                g.Item().Text(titre).Bold().FontSize(12).FontColor(Couleurs.Accent);
                g.Item().Text(t =>
                {
                    t.Span($"Pré-BL {e.Numero}").Bold().FontSize(11);
                    if (e.Depart is { } d) t.Span($"   ·   départ le {d:dd/MM/yyyy}");
                });
                if (e.Transporteur != "") g.Item().Text($"Transporteur : {e.Transporteur}");
                if (complement != "") g.Item().Text(complement);
            });
            if (codeBarre)
                r.ConstantItem(150).Column(g =>
                {
                    g.Item().Height(28).Svg(CodeBarres.Code39Svg(e.Numero.ToString("000000")));
                    g.Item().AlignCenter().Text(e.Numero.ToString("000000")).FontSize(7);
                });
            r.ConstantItem(230).PaddingLeft(10).Border(0.6f).BorderColor(Couleurs.Trait).Padding(6).Column(a =>
            {
                a.Item().Text($"{e.NomClient}  ({e.CompteGeneral} {e.CompteClient})").FontSize(7).FontColor(Couleurs.Discret);
                a.Item().Text(e.Adresse.RaisonSociale).Bold();
                foreach (var l in new[] { e.Adresse.Rue1, e.Adresse.Rue2, $"{e.Adresse.CodePostal} {e.Adresse.Ville}".Trim() }.Where(x => x != ""))
                    a.Item().Text(l);
            });
        });
    });

    public static IContainer Titre(IContainer x) => x.Background(Couleurs.Accent).PaddingVertical(3).PaddingHorizontal(3);
    public static IContainer Cellule(IContainer x) => x.BorderBottom(0.4f).BorderColor(Couleurs.Trait).PaddingVertical(3).PaddingHorizontal(3);
}

// Feuille de préparation (ETAT_FeuilleKimmel) : palettes à prendre, par dépôt (un dépôt par page), emplacement, SSCC ; code-barres
// du n° de pré-BL et, si demandé, de chaque palette ; total des palettes du dépôt (une seule par palette groupée)
internal sealed class DocumentFeuillePreparation(FeuillePreparation f) : IDocument
{
    public void Compose(IDocumentContainer conteneur)
    {
        var depots = f.Palettes.GroupBy(p => (p.Depot, p.NomDepot)).ToList();
        if (depots.Count == 0)
        {
            conteneur.Page(page =>
            {
                EnTeteFeuille.Page(page);
                page.Header().Element(c => EnTeteFeuille.Composer(c, "FEUILLE DE PRÉPARATION", f.Societe, f.EnTete, "", true));
                page.Content().PaddingTop(30).AlignCenter().Text("Aucune palette choisie sur ce pré-BL.").Italic();
            });
            return;
        }
        foreach (var depot in depots)
            conteneur.Page(page =>
            {
                EnTeteFeuille.Page(page);
                page.Header().Element(c => EnTeteFeuille.Composer(c, "FEUILLE DE PRÉPARATION", f.Societe, f.EnTete,
                    $"Dépôt {depot.Key.Depot} {depot.Key.NomDepot}".Trim(), true));
                page.Content().PaddingTop(8).Element(c => Palettes(c, depot.ToList()));
                page.Footer().AlignRight().Text(t =>
                {
                    t.Span($"Pré-BL {f.EnTete.Numero} · ");
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });
            });
    }

    private void Palettes(IContainer c, IReadOnlyList<PalettePreparation> palettes) => c.Column(col =>
    {
        col.Item().Table(t =>
        {
            t.ColumnsDefinition(d =>
            {
                d.ConstantColumn(f.CodesBarres ? 150 : 90); d.ConstantColumn(70); d.ConstantColumn(60); d.RelativeColumn(3); d.ConstantColumn(60);
                d.ConstantColumn(50); d.ConstantColumn(40); d.ConstantColumn(45); d.ConstantColumn(50); d.RelativeColumn(2);
            });
            t.Header(h =>
            {
                foreach (var titre in new[] { "N° palette", "Article", "Réf. imp.", "Désignation", "Emplacement", "Quantité", "Multi", "Picking", "Client", "Observation" })
                    h.Cell().Element(EnTeteFeuille.Titre).Text(titre).FontColor(Colors.White).Bold();
            });
            foreach (var p in palettes)
            {
                t.Cell().Element(EnTeteFeuille.Cellule).Column(x =>
                {
                    x.Item().Text(p.Sscc).Bold();
                    if (f.CodesBarres && p.Sscc != "") x.Item().Height(22).Svg(CodeBarres.Code128Svg(p.Sscc));
                });
                t.Cell().Element(EnTeteFeuille.Cellule).Text($"{p.Article} {p.Indice}".Trim());
                t.Cell().Element(EnTeteFeuille.Cellule).Text(p.RefImprime);
                t.Cell().Element(EnTeteFeuille.Cellule).Text(p.Designation);
                t.Cell().Element(EnTeteFeuille.Cellule).Text(p.Emplacement).Bold();
                t.Cell().Element(EnTeteFeuille.Cellule).AlignRight().Text(EnTeteFeuille.N(p.Reste));
                t.Cell().Element(EnTeteFeuille.Cellule).Text(p.Groupe != "" ? $"oui {p.Groupe}" : "");
                t.Cell().Element(EnTeteFeuille.Cellule).AlignRight().Text(p.Picking > 0 ? EnTeteFeuille.N(p.Picking) : "");
                t.Cell().Element(EnTeteFeuille.Cellule).Text(p.Client);
                t.Cell().Element(EnTeteFeuille.Cellule).Text(p.Observation);
            }
        });
        col.Item().PaddingTop(6).AlignRight().Text($"Total palettes : {ReglesFeuillesPreBl.Palettes(palettes)}").Bold();
    });
}

// Feuille camion (ETAT_FeuilleCamion) : pré-BL d'un même client, départ, transporteur et adresse ; une ligne par lot (palettes
// complètes puis palette mixte) ou par frais ; totaux par pré-BL, palettes par dépôt et cumuls ; montants si valorisée
internal sealed class DocumentFeuilleCamion(FeuilleCamion f) : IDocument
{
    public void Compose(IDocumentContainer conteneur) => conteneur.Page(page =>
    {
        EnTeteFeuille.Page(page);
        page.Header().Element(c => EnTeteFeuille.Composer(c, "FEUILLE CAMION", f.Societe, f.EnTete,
            f.Totaux.Count > 1 ? $"Pré-BL {string.Join(", ", f.Totaux.Select(t => t.PreBl))}" : "", false));
        page.Content().PaddingTop(8).Element(Contenu);
        page.Footer().AlignRight().Text(t =>
        {
            t.CurrentPageNumber();
            t.Span(" / ");
            t.TotalPages();
        });
    });

    private void Contenu(IContainer c) => c.Column(col =>
    {
        col.Item().Table(t =>
        {
            t.ColumnsDefinition(d =>
            {
                d.ConstantColumn(38); d.ConstantColumn(55); d.ConstantColumn(50); d.RelativeColumn(3); d.ConstantColumn(50); d.ConstantColumn(28);
                d.ConstantColumn(55); d.ConstantColumn(45); d.ConstantColumn(28); d.ConstantColumn(38); d.ConstantColumn(38); d.ConstantColumn(30);
                d.ConstantColumn(38); d.ConstantColumn(40); d.ConstantColumn(48); d.RelativeColumn(1);
                if (f.Valorisee) { d.ConstantColumn(45); d.ConstantColumn(40); }
            });
            t.Header(h =>
            {
                var titres = new List<string> { "Pré-BL", "Article", "Réf. imp.", "Désignation", "Dépôt", "Payé", "N° palettes", "Quantité", "Pal.",
                    "Poids théo.", "Ex. / pal.", "Col. / pal.", "Ex. / col.", "Type colis", "Cde NP", "Cde client" };
                if (f.Valorisee) titres.AddRange(["Montant frais", "PU"]);
                foreach (var titre in titres) h.Cell().Element(EnTeteFeuille.Titre).Text(titre).FontColor(Colors.White).Bold();
            });
            foreach (var preBl in f.Lignes.GroupBy(l => l.PreBl))
            {
                foreach (var l in preBl)
                {
                    t.Cell().Element(EnTeteFeuille.Cellule).Text(l.PreBl.ToString());
                    t.Cell().Element(EnTeteFeuille.Cellule).Text(l.Article).Bold();
                    t.Cell().Element(EnTeteFeuille.Cellule).Text(l.RefImprime);
                    t.Cell().Element(EnTeteFeuille.Cellule).Text(l.Designation);
                    t.Cell().Element(EnTeteFeuille.Cellule).Text(l.Depot);
                    t.Cell().Element(EnTeteFeuille.Cellule).Text(l.StockPaye ? "O" : "N");
                    t.Cell().Element(EnTeteFeuille.Cellule).Text(l.NumerosPalettes);
                    t.Cell().Element(EnTeteFeuille.Cellule).AlignRight().Text(EnTeteFeuille.N(l.Quantite));
                    t.Cell().Element(EnTeteFeuille.Cellule).AlignRight().Text(l.Palettes > 0 ? l.Palettes.ToString() : "");
                    t.Cell().Element(EnTeteFeuille.Cellule).AlignRight().Text(l.Poids > 0 ? EnTeteFeuille.N(l.Poids) : "");
                    t.Cell().Element(EnTeteFeuille.Cellule).AlignRight().Text(l.ExParPalette > 0 ? EnTeteFeuille.N(l.ExParPalette) : "");
                    t.Cell().Element(EnTeteFeuille.Cellule).AlignRight().Text(l.ColisParPalette > 0 ? l.ColisParPalette.ToString() : "");
                    t.Cell().Element(EnTeteFeuille.Cellule).AlignRight().Text(l.ExParColis > 0 ? EnTeteFeuille.N(l.ExParColis) : "");
                    t.Cell().Element(EnTeteFeuille.Cellule).Text(l.TypeColis);
                    t.Cell().Element(EnTeteFeuille.Cellule).Text(l.Commande);
                    t.Cell().Element(EnTeteFeuille.Cellule).Text(l.RefCommande);
                    if (f.Valorisee)
                    {
                        t.Cell().Element(EnTeteFeuille.Cellule).AlignRight().Text(l.Montant != 0 ? Montant(l.Montant) : "");
                        t.Cell().Element(EnTeteFeuille.Cellule).AlignRight().Text(l.PrixUnitaire != 0 ? Prix(l.PrixUnitaire) : "");
                    }
                }
                var total = f.Totaux.First(x => x.PreBl == preBl.Key);
                t.Cell().ColumnSpan(8).Element(EnTeteFeuille.Cellule).AlignRight().Text($"Total pré-BL {total.PreBl}").Bold();
                t.Cell().Element(EnTeteFeuille.Cellule).AlignRight().Text(total.Palettes.ToString()).Bold();
                t.Cell().Element(EnTeteFeuille.Cellule).AlignRight().Text(EnTeteFeuille.N(total.Poids)).Bold();
                t.Cell().ColumnSpan(6).Element(EnTeteFeuille.Cellule).Text(total.PoidsSaisi > 0 ? $"(poids saisi : {EnTeteFeuille.N(total.PoidsSaisi)} kg)" : "");
                if (f.Valorisee)
                {
                    t.Cell().Element(EnTeteFeuille.Cellule).AlignRight().Text(total.Montant != 0 ? Montant(total.Montant) : "").Bold();
                    t.Cell().Element(EnTeteFeuille.Cellule).Text("");
                }
            }
        });
        col.Item().PaddingTop(10).Row(r =>
        {
            r.ConstantItem(260).Border(0.6f).BorderColor(Couleurs.Trait).Padding(6).Column(g =>
            {
                g.Item().Text("Palettes par dépôt").Bold();
                foreach (var (depot, palettes) in f.PalettesParDepot) g.Item().Text($"{palettes}  {depot}");
            });
            r.ConstantItem(220).PaddingLeft(10).Border(0.6f).BorderColor(Couleurs.Trait).Padding(6).Column(g =>
            {
                g.Item().Text($"Cumul palettes : {f.CumulPalettes}").Bold();
                g.Item().Text($"Cumul poids : {EnTeteFeuille.N(f.CumulPoids)} kg").Bold();
            });
        });
    });
}
