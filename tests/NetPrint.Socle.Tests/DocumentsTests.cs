using System.Text;
using NetPrint.Socle.Documents;

namespace NetPrint.Socle.Tests;

public class MessageBrouillonTests
{
    private static string Eml(MessageBrouillon m) => Encoding.ASCII.GetString(m.VersEml("test.eml").Contenu);

    [Fact]
    public void Brouillon_non_envoye_avec_destinataires_et_piece_jointe()
    {
        var texte = Eml(new MessageBrouillon(["a@exemple.fr"], ["b@exemple.fr", "c@exemple.fr"], "ARC du client", "Bonjour",
            [new PieceJointe("ARC 00024-1.pdf", Encoding.ASCII.GetBytes("%PDF-1.7"))]));
        Assert.StartsWith("X-Unsent: 1\r\n", texte);
        Assert.Contains("To: a@exemple.fr\r\n", texte);
        Assert.Contains("Cc: b@exemple.fr, c@exemple.fr\r\n", texte);
        Assert.Contains("Subject: ARC du client\r\n", texte);
        Assert.DoesNotContain("From:", texte);   // la messagerie prend le compte de l'utilisateur
        Assert.Contains("Content-Disposition: attachment; filename=\"ARC 00024-1.pdf\"", texte);
        Assert.Contains(Convert.ToBase64String(Encoding.ASCII.GetBytes("%PDF-1.7")), texte);
        Assert.Matches(@"Date: \w{3}, \d{2} \w{3} \d{4} \d{2}:\d{2}:\d{2} [+-]\d{4}\r\n", texte);
    }

    [Fact]
    public void Objet_accentue_encode_et_texte_en_utf8()
    {
        var texte = Eml(new MessageBrouillon([], [], "Accusé de réception", "Réception confirmée", []));
        Assert.Contains($"Subject: =?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes("Accusé de réception"))}?=", texte);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("Réception confirmée")), texte);
        Assert.DoesNotContain("To:", texte);   // sans destinataire : à compléter dans la messagerie
    }

    [Fact]
    public void Objet_long_replie_en_mots_encodes_courts()
    {
        var objet = "Accusé de réception de votre commande PO-123456789 du 02/10/2026 — LABORATOIRES CLARINS";
        var texte = Eml(new MessageBrouillon([], [], objet, "", []));
        var debut = texte.IndexOf("Subject: ", StringComparison.Ordinal);
        var entete = texte[debut..texte.IndexOf("\r\nDate:", StringComparison.Ordinal)];
        var mots = entete["Subject: ".Length..].Split("\r\n ");
        Assert.True(mots.Length > 1);
        Assert.All(mots, m => Assert.True(m.Length <= 75, m));
        Assert.Equal(objet, string.Concat(mots.Select(m => Encoding.UTF8.GetString(Convert.FromBase64String(m[10..^2])))));
    }

    [Fact]
    public void Pas_d_injection_d_en_tete_par_une_adresse()
    {
        var texte = Eml(new MessageBrouillon(["a@exemple.fr\r\nBcc: espion@exemple.fr"], [], "x", "y", []));
        Assert.DoesNotContain("\r\nBcc:", texte);
    }
}
