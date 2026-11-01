using System.Globalization;
using NetPrint.Modules.Facturation.Services;
using NetPrint.Noyau.Societes;
using NetPrint.Socle.Documents;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using static NetPrint.Socle.Documents.DocumentsPdf;

namespace NetPrint.Modules.Facturation.Impressions;

// =====================================================================
// Mise en page de la facture (reprise modernisée de FAC202P5, sur le modèle de la facture réelle K:\Factures_pdf) : en-tête
// société et logo, client facturé, cartouche n° / date, références, lignes par élément, puis en dernière page : mentions
// légales (FAC120), récapitulatif de la TVA par taux, totaux, acompte, net à payer, règlement, IBAN, papillon de règlement.
// Écarts voulus avec le RPG : TVA par taux (le RPG n'imprimait qu'un taux), facture d'origine sur un avoir, n° de TVA du client,
// mentions de 70 caractères entières, bandeau BROUILLON tant que la facture n'est pas éditée en définitif.
// =====================================================================
internal sealed class DocumentFacture(DonneesEditionFacture d, TotauxFacture t, IdentiteSociete? societe) : IDocument
{
    public const string TypeLogo = "Fac";
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    private EnTeteEditionFacture E => d.EnTete;
    private LibellesFacture L => d.L;
    // Avoir : montants imprimés positifs, seul le net à payer est négatif (comme FAC202P5)
    private int Signe => E.Avoir ? -1 : 1;
    private string Titre => E.Avoir ? L.AvoirTitre : L.Facture;

    private static string M(decimal v) => v.ToString("#,##0.00", Fr);
    private static string Q(decimal v) => v.ToString("#,##0.###", Fr);

    public void Compose(IDocumentContainer conteneur) => conteneur.Page(page =>
    {
        page.PageStandard(apercu: false);
        if (E.Brouillon || E.Annulee)
            page.Foreground().AlignCenter().AlignMiddle().Rotate(-35)
                .Text(E.Annulee ? L.Annulee.ToUpperInvariant() : "BROUILLON").FontSize(90).Bold().FontColor("#22B91C1C");
        page.Header().Element(Entete);
        page.Content().PaddingTop(8).Element(Contenu);
        page.Footer().Element(Pied);
    });

    private void Entete(IContainer c) => c.Column(col =>
    {
        col.Item().Row(r =>
        {
            r.RelativeItem().Column(s =>
            {
                if (societe?.Logo is { } logo) s.Item().MaxHeight(50).MaxWidth(180).Image(logo).FitArea();
                if (societe is not null)
                {
                    s.Item().Text(societe.RaisonSociale).Bold().FontSize(11).FontColor(Couleurs.Accent);
                    foreach (var l in new[] { societe.Rue1, societe.Rue2, $"{societe.CodePostal} {societe.Ville}".Trim() }.Where(x => x != ""))
                        s.Item().Text(l).FontSize(8);
                }
            });
            r.ConstantItem(250).Border(0.6f).BorderColor(Couleurs.Trait).Padding(8).Column(a =>
            {
                var premiere = true;
                foreach (var l in E.Adresse)
                {
                    a.Item().Text(l).FontSize(premiere ? 10 : 9).Gras(premiere);
                    premiere = false;
                }
                if (E.AlAttention != "") a.Item().PaddingTop(2).Text($"{L.Attention} {E.AlAttention}").FontSize(8);
            });
        });
        col.Item().PaddingTop(8).Row(r =>
        {
            r.RelativeItem().Column(g =>
            {
                void Ligne(string libelle, string valeur)
                {
                    if (valeur != "") g.Item().Text(t => { t.Span($"{libelle} : ").FontColor(Couleurs.Discret); t.Span(valeur); });
                }
                if (E.RefCommande != "")
                    Ligne(L.RefCommande, E.RefCommande + (E.DateCommande is { } dc ? $" ({dc:dd/MM/yy})" : ""));
                Ligne("Ref CEE", E.RefExportSociete);                  // n° de TVA de la société (non traduit, comme le RPG)
                Ligne(L.RefFournisseur, E.RefFournisseur);
                Ligne(L.VotreRefCee, E.TvaClient);                      // n° de TVA du client, que le RPG n'imprimait pas
                if (E.Interlocuteur != "") g.Item().Text(E.Interlocuteur);
                if (E.Representant != "") g.Item().Text(E.Representant).FontColor(Couleurs.Discret);
            });
            r.ConstantItem(250).Column(g =>
            {
                g.Item().AlignRight().Text(Titre.ToUpperInvariant()).Bold().FontSize(13).FontColor(Couleurs.Accent);
                g.Item().AlignRight().Text(E.NumeroImprime).Bold().FontSize(16);
                g.Item().AlignRight().Text($"{L.DateFacture} {E.Date:dd/MM/yyyy}");
                if (E.Origine is { } o) g.Item().AlignRight().Text($"Sur facture n° {o.Numero}" + (o.Annee > 0 ? $" de {o.Annee}" : "")).FontSize(8);
            });
        });
    });

    private void Contenu(IContainer c) => c.Column(col =>
    {
        col.Item().Table(table =>
        {
            table.ColumnsDefinition(cd =>
            {
                cd.ConstantColumn(75); cd.ConstantColumn(55); cd.RelativeColumn(); cd.ConstantColumn(45); cd.ConstantColumn(60); cd.ConstantColumn(70);
            });
            table.Header(h =>
            {
                IContainer T(IContainer x) => x.Background(Couleurs.Accent).PaddingVertical(4).PaddingHorizontal(4);
                h.Cell().Element(T).Text(L.Reference).FontColor(Colors.White).Bold();
                h.Cell().Element(T).AlignRight().Text(L.Quantite).FontColor(Colors.White).Bold();
                h.Cell().Element(T).Text(L.Designation).FontColor(Colors.White).Bold();
                h.Cell().Element(T).Text(L.Unite).FontColor(Colors.White).Bold();
                h.Cell().Element(T).AlignRight().Text(L.PrixUnitaire).FontColor(Colors.White).Bold();
                h.Cell().Element(T).AlignRight().Text(L.Montant).FontColor(Colors.White).Bold();
            });
            IContainer Cellule(IContainer x) => x.PaddingVertical(2).PaddingHorizontal(4);
            // Lignes rattachées à aucun élément : imprimées à la fin (elles comptent dans les totaux)
            var orphelines = d.Lignes.Where(l => d.Elements.All(el => el.Numero != l.Element)).Select(l => l.Element).Distinct()
                .Select(n => new ElementEditionFacture(n, 0, "", "", []));
            foreach (var el in d.Elements.Concat(orphelines))
            {
                if (el.Titre != "") table.Cell().ColumnSpan(6).Background(Couleurs.Fond).PaddingVertical(3).PaddingHorizontal(4).Text(el.Titre).Bold();
                foreach (var (texte, gras) in el.Textes) table.Cell().ColumnSpan(6).Element(Cellule).Text(texte).Gras(gras);
                foreach (var l in d.Lignes.Where(x => x.Element == el.Numero))
                {
                    if (l.Texte)
                    {
                        table.Cell().ColumnSpan(2).Element(Cellule).Text("");
                        table.Cell().ColumnSpan(4).Element(Cellule).Text(l.Designation).Italic().FontColor(Couleurs.Discret);
                        continue;
                    }
                    table.Cell().Element(Cellule).Text(l.Reference).FontSize(8);
                    table.Cell().Element(Cellule).AlignRight().Text(l.Quantite != 0 ? Q(l.Quantite) : "");
                    table.Cell().Element(Cellule).Text(l.Remise != 0 ? $"{l.Designation}  (−{Q(l.Remise)} %)" : l.Designation);
                    table.Cell().Element(Cellule).Text(l.Unite);
                    table.Cell().Element(Cellule).AlignRight().Text(l.PrixUnitaire != 0 ? l.PrixUnitaire.ToString("#,##0.00##", Fr) : "");
                    table.Cell().Element(Cellule).AlignRight().Text(l.Montant != 0 ? M(l.Montant) : "");
                }
            }
            foreach (var p in d.TextesPied) table.Cell().ColumnSpan(6).Element(Cellule).Text(p);
        });
        col.Item().ShowEntire().PaddingTop(10).Element(Totaux);
    });

    private void Totaux(IContainer c) => c.Column(col =>
    {
        // Mentions légales (FAC120 : ML à gauche, M2 à droite), en entier
        if (d.MentionsGauche.Count + d.MentionsDroite.Count > 0)
            col.Item().PaddingBottom(6).Row(r =>
            {
                r.RelativeItem().Column(m => { foreach (var l in d.MentionsGauche) m.Item().Text(l).FontSize(6.5f); });
                r.ConstantItem(10);
                r.RelativeItem().Column(m => { foreach (var l in d.MentionsDroite) m.Item().Text(l).FontSize(6.5f); });
            });
        col.Item().Row(r =>
        {
            // Récapitulatif de la TVA par taux
            r.RelativeItem().Table(tv =>
            {
                tv.ColumnsDefinition(cd => { cd.RelativeColumn(); cd.ConstantColumn(55); cd.RelativeColumn(); });
                IContainer Tete(IContainer x) => x.BorderBottom(0.6f).BorderColor(Couleurs.Trait).PaddingVertical(2).PaddingHorizontal(3);
                IContainer Cel(IContainer x) => x.PaddingVertical(2).PaddingHorizontal(3);
                tv.Cell().Element(Tete).AlignRight().Text(L.BaseHt).Bold();
                tv.Cell().Element(Tete).AlignRight().Text(L.TauxTva).Bold();
                tv.Cell().Element(Tete).AlignRight().Text(L.Tva).Bold();
                foreach (var x in t.Tva)
                {
                    tv.Cell().Element(Cel).AlignRight().Text(M(x.Base));
                    tv.Cell().Element(Cel).AlignRight().Text(x.Taux == 0 ? "EXO" : $"{x.Taux.ToString("0.##", Fr)} %");
                    tv.Cell().Element(Cel).AlignRight().Text(M(x.Montant));
                }
            });
            r.ConstantItem(12);
            // Totaux
            r.ConstantItem(220).Border(0.6f).BorderColor(Couleurs.Trait).Padding(6).Column(tt =>
            {
                void Ligne(string libelle, decimal montant, bool gras = false) => tt.Item().Row(x =>
                {
                    x.RelativeItem().Text(libelle).Gras(gras);
                    x.ConstantItem(90).AlignRight().Text(M(montant)).Gras(gras);
                });
                Ligne($"{L.Totaux} {L.Ht}", t.Ht - t.Escompte);
                if (t.Escompte != 0) tt.Item().Text($"(escompte déduit : {M(t.Escompte)})").FontSize(7).FontColor(Couleurs.Discret);
                if (t.PortTaxable + t.PortExonere != 0) Ligne($"{L.Port} {L.Ht}", t.PortTaxable + t.PortExonere);
                Ligne(L.Tva, t.TotalTva);
                Ligne($"{L.Totaux} {L.Ttc}", t.Ttc, true);
                if (t.Acompte != 0) Ligne(L.Acompte, -t.Acompte);
                tt.Item().PaddingTop(4).Background(Couleurs.Fond).Padding(4).Row(x =>
                {
                    x.RelativeItem().Text(L.NetAPayer).Bold().FontSize(11);
                    x.ConstantItem(110).AlignRight().Text($"{M(Signe * t.NetAPayer)} {E.Devise}").Bold().FontSize(11);
                });
            });
        });
        // Règlement, banque et papillon
        col.Item().PaddingTop(10).Row(r =>
        {
            r.RelativeItem().Column(g =>
            {
                if (E.ModeReglement != "") g.Item().Text(t => { t.Span($"{L.ModeReglement} : ").FontColor(Couleurs.Discret); t.Span(E.ModeReglement); });
                if (E.Echeance != "") g.Item().Text(E.Echeance).Bold();
                if (d.Banque is { } b)
                {
                    g.Item().PaddingTop(4).Text($"IBAN-BIC {b.Nom}").FontSize(8);
                    g.Item().Text(b.Iban).FontSize(8);
                    g.Item().Text(b.Bic).FontSize(8);
                }
            });
            // Papillon de règlement : à découper et joindre au paiement (gardé jusqu'à la facture électronique, David 01/11)
            r.ConstantItem(220).Border(0.6f).BorderColor(Couleurs.Trait).Padding(6).Column(p =>
            {
                p.Item().Text($"{L.NumeroClient} {E.CompteGeneral} {E.CompteClient:0000000}" + (E.Tiers != "" ? $" · {E.Tiers}" : "")).FontSize(8);
                p.Item().Text($"{(E.Avoir ? L.NumeroAvoir : L.NumeroFacture)} {E.NumeroImprime}").FontSize(8);
                p.Item().Text($"{L.NetAPayer} {M(Signe * t.NetAPayer)} {E.Devise}").Bold();
            });
        });
    });

    private void Pied(IContainer c) => c.BorderTop(0.5f).BorderColor(Couleurs.Trait).PaddingTop(4).Row(r =>
    {
        r.RelativeItem().Text(societe is not { } s ? "" : $"{s.RaisonSociale} · {s.MentionsLegales}".Trim(' ', '·')).FontSize(7).FontColor(Couleurs.Discret);
        r.ConstantItem(170).AlignRight().Text(x =>
        {
            x.DefaultTextStyle(s => s.FontSize(7).FontColor(Couleurs.Discret));
            x.Span($"{Titre} {E.NumeroImprime} · ");
            x.CurrentPageNumber();
            x.Span(" / ");
            x.TotalPages();
        });
    });
}
