using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace NetPrint.Socle.Documents;

// =====================================================================
// Éditions PDF produites par l'application (ARC, devis, BL, factures...) : socle commun, sans règle métier.
// Chaque module garde son modèle de document (dossier Impressions/) ; il y trouve ici la page standard, le filigrane
// d'aperçu, les formats et la réponse HTTP. Guide : docs/editions-pdf.md.
// =====================================================================
public sealed record DocumentGenere(string Nom, byte[] Contenu)
{
    public const string TypePdf = "application/pdf";
}

public static class DocumentsPdf
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    // Licence QuestPDF : Community (voir Directory.Packages.props). Appelée avant toute génération.
    // Polices du système acceptées (Arial, Courier New sous Windows) ; à défaut, Lato, livrée avec QuestPDF.
    // Réglages globaux du processus : appliqués une seule fois.
    private static readonly Lazy<bool> Reglages = new(() =>
    {
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.UseEnvironmentFonts = true;
        QuestPDF.Settings.ThrowOnMissingFontFamilies = false;   // les listes de polices sont des replis voulus
        return true;
    });

    public static void Initialiser() => _ = Reglages.Value;

    // Génère un document QuestPDF
    public static DocumentGenere Generer(string nom, IDocument document)
    {
        Initialiser();
        return new DocumentGenere(nom, document.GeneratePdf());
    }

    // Page A4 portrait commune : marges, police, filigrane « APERÇU » pour un document non édité
    public static void PageStandard(this PageDescriptor page, bool apercu)
    {
        page.Size(PageSizes.A4);
        page.MarginHorizontal(15, Unit.Millimetre);
        page.MarginVertical(12, Unit.Millimetre);
        page.DefaultTextStyle(t => t.FontSize(9).FontFamily(Polices.Texte).FontColor(Couleurs.Texte));
        if (apercu)
            page.Foreground().AlignCenter().AlignMiddle().Rotate(-35)
                .Text("APERÇU").FontSize(110).Bold().FontColor("#22B91C1C");
    }

    // Gras conditionnel (zones « double brillance » des canevas, totaux)
    public static TextSpanDescriptor Gras(this TextSpanDescriptor texte, bool gras) =>
        gras ? texte.Bold() : texte.NormalWeight();

    // Formats français des éditions
    public static string Montant(decimal v) => v.ToString("#,##0.00", Fr);
    public static string Prix(decimal v) => v.ToString("#,##0.00##", Fr);
    public static string Quantite(decimal v) => v.ToString("#,##0.###", Fr);
    public static string Date(DateOnly? d) => d?.ToString("dd/MM/yyyy", Fr) ?? "";
    public static string DateLongue(DateOnly d) => d.ToString("d MMMM yyyy", Fr);

    // Réponse HTTP : PDF affiché dans le navigateur (ou téléchargé), sous son nom
    public static IResult Reponse(HttpContext http, DocumentGenere document, bool telecharger = false)
    {
        http.Response.Headers.ContentDisposition =
            new ContentDispositionHeaderValue(telecharger ? "attachment" : "inline") { FileNameStar = document.Nom }.ToString();
        http.Response.Headers.CacheControl = "no-store";
        return Results.File(document.Contenu, DocumentGenere.TypePdf);
    }

    public static class Polices
    {
        // Première police disponible de la liste
        public static readonly string[] Texte = ["Arial", "Lato"];
        public static readonly string[] ChasseFixe = ["Courier New", "Liberation Mono", "DejaVu Sans Mono", "Lato"];   // canevas en colonnes
    }

    public static class Couleurs
    {
        public const string Texte = "#1F2933";
        public const string Discret = "#6B7280";
        public const string Accent = "#1E3A5F";
        public const string Fond = "#F1F4F8";
        public const string Trait = "#C9D1DB";
    }
}
