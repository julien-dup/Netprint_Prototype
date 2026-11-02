using System.Text;

namespace NetPrint.Socle.Documents;

// =====================================================================
// Message à envoyer depuis la messagerie de l'utilisateur, sans serveur SMTP : fichier .eml marqué « non envoyé »
// (X-Unsent: 1). Ouvert depuis le navigateur, Outlook (ou une autre messagerie) le présente comme un brouillon prêt à
// partir, pièces jointes comprises ; l'utilisateur relit et envoie (comme EnvoiARC / VisuArc avec Outlook).
// Pas d'expéditeur : la messagerie prend le compte de l'utilisateur.
// =====================================================================
public sealed record PieceJointe(string Nom, byte[] Contenu, string Type = DocumentGenere.TypePdf);

public sealed record MessageBrouillon(
    IReadOnlyList<string> Destinataires, IReadOnlyList<string> Copies, string Objet, string Texte, IReadOnlyList<PieceJointe> PiecesJointes)
{
    public const string TypeEml = "message/rfc822";

    public DocumentGenere VersEml(string nom)
    {
        var frontiere = "----=_NetPrint_" + Guid.NewGuid().ToString("N");
        var sb = new StringBuilder();
        void L(string ligne = "") => sb.Append(ligne).Append("\r\n");

        L("X-Unsent: 1");
        if (Destinataires.Count > 0) L("To: " + string.Join(", ", Destinataires.Select(Adresse)));
        if (Copies.Count > 0) L("Cc: " + string.Join(", ", Copies.Select(Adresse)));
        L("Subject: " + EnTete(Objet));
        // RFC 5322 : fuseau « +0100 » (sans deux-points)
        var maintenant = DateTimeOffset.Now;
        L("Date: " + maintenant.ToString("ddd, dd MMM yyyy HH:mm:ss ", System.Globalization.CultureInfo.InvariantCulture)
            + (maintenant.Offset < TimeSpan.Zero ? "-" : "+") + maintenant.Offset.ToString("hhmm"));
        L("MIME-Version: 1.0");
        L($"Content-Type: multipart/mixed; boundary=\"{frontiere}\"");
        L();
        L("--" + frontiere);
        L("Content-Type: text/plain; charset=utf-8");
        L("Content-Transfer-Encoding: base64");
        L();
        Base64(Encoding.UTF8.GetBytes(Texte.Replace("\r\n", "\n").Replace("\n", "\r\n")));
        foreach (var p in PiecesJointes)
        {
            L("--" + frontiere);
            L($"Content-Type: {p.Type}; name=\"{EnTete(p.Nom)}\"");
            L("Content-Transfer-Encoding: base64");
            L($"Content-Disposition: attachment; filename=\"{EnTete(p.Nom)}\"");
            L();
            Base64(p.Contenu);
        }
        L("--" + frontiere + "--");
        return new DocumentGenere(nom, Encoding.ASCII.GetBytes(sb.ToString()));

        void Base64(byte[] octets)
        {
            var b64 = Convert.ToBase64String(octets);
            for (var i = 0; i < b64.Length; i += 76) L(b64.Substring(i, Math.Min(76, b64.Length - i)));
        }
    }

    // En-tête non ASCII encodé selon la RFC 2047 : mots encodés de 75 caractères au plus (45 octets UTF-8 chacun, sans
    // couper un caractère), repliés sur plusieurs lignes
    internal static string EnTete(string texte)
    {
        if (texte.All(c => c is >= ' ' and <= '~' && c != '"')) return texte;
        var mots = new List<string>();
        var morceau = new StringBuilder();
        foreach (var r in texte.EnumerateRunes())
        {
            if (Encoding.UTF8.GetByteCount(morceau.ToString() + r) > 45)
            {
                mots.Add(morceau.ToString());
                morceau.Clear();
            }
            morceau.Append(r.ToString());
        }
        if (morceau.Length > 0) mots.Add(morceau.ToString());
        return string.Join("\r\n ", mots.Select(m => $"=?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(m))}?="));
    }

    // Adresse seule (les retours à la ligne sont retirés : pas d'injection d'en-tête)
    internal static string Adresse(string adresse) => adresse.Replace("\r", "").Replace("\n", "").Replace(",", "").Trim();
}
