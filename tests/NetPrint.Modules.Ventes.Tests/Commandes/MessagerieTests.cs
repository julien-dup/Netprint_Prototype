using NetPrint.Modules.Ventes.Services;
using NetPrint.Noyau.Messagerie;
using NetPrint.Noyau.Tiers;
using NetPrint.Socle.Documents;

namespace NetPrint.Modules.Ventes.Tests.Commandes;

// Envoi par le serveur SMTP de la société (FAC010) : garde-fou, redirection de test, destinataires par défaut
public class MessagerieTests
{
    private static readonly ParametresSmtp Smtp = new("smtp.exemple.fr", 25, "", "", false, "adv@exemple.fr");

    [Theory]
    [InlineData("Reel", null, true)]
    [InlineData("Redirection", "test@axilog.net", true)]
    [InlineData("Redirection", null, false)]     // redirection sans adresse : rien ne part
    [InlineData("Desactive", "test@axilog.net", false)]
    [InlineData("", null, false)]                // défaut : désactivé
    public void Rien_ne_part_sans_le_dire(string mode, string? redirection, bool disponible) =>
        Assert.Equal(disponible, ReglesMessagerie.Etat(Smtp, mode, redirection).Disponible);

    [Fact]
    public void L_utilisateur_est_l_expediteur_sinon_la_societe()
    {
        Assert.Equal("david@exemple.fr", ReglesMessagerie.AvecExpediteur(Smtp, "david@exemple.fr")!.Expediteur);   // même domaine
        // Autre domaine : la société reste l'expéditeur (le serveur refuserait), l'utilisateur en « Répondre à »
        var autre = ReglesMessagerie.AvecExpediteur(Smtp, "david@axilog.net")!;
        Assert.Equal(("adv@exemple.fr", "david@axilog.net"), (autre.Expediteur, autre.RepondreA));
        using var mail = ReglesMessagerie.Preparer(autre, new MessageBrouillon(["c@x.fr"], [], "o", "t", []), null);
        Assert.Equal("david@axilog.net", Assert.Single(mail.ReplyToList).Address);
        Assert.Equal("adv@exemple.fr", ReglesMessagerie.AvecExpediteur(Smtp, "")!.Expediteur);
        Assert.Equal("adv@exemple.fr", ReglesMessagerie.AvecExpediteur(Smtp, "pas une adresse")!.Expediteur);
        // Société sans adresse d'expédition : l'adresse de l'utilisateur suffit
        Assert.True(ReglesMessagerie.Etat(ReglesMessagerie.AvecExpediteur(Smtp with { Expediteur = "" }, "david@axilog.net"), "Reel", null).Disponible);
    }

    [Fact]
    public void Serveur_et_expediteur_obligatoires()
    {
        Assert.False(ReglesMessagerie.Etat(null, "Reel", null).Disponible);
        Assert.False(ReglesMessagerie.Etat(Smtp with { Serveur = "" }, "Reel", null).Disponible);
        Assert.Contains("expédition", ReglesMessagerie.Etat(Smtp with { Expediteur = "" }, "Reel", null).Raison);
    }

    [Fact]
    public void Redirection_de_test_vers_une_seule_adresse()
    {
        var m = new MessageBrouillon(["client@exemple.fr"], ["copie@exemple.fr"], "ARC du client", "Bonjour",
            [new PieceJointe("ARC.pdf", [1, 2, 3])]);
        using var test = ReglesMessagerie.Preparer(Smtp, m, "test@axilog.net");
        Assert.Equal(["test@axilog.net"], test.To.Select(a => a.Address));
        Assert.Empty(test.CC);
        Assert.Equal("[TEST] ARC du client", test.Subject);
        Assert.Contains("client@exemple.fr", test.Body);
        Assert.Single(test.Attachments);

        using var reel = ReglesMessagerie.Preparer(Smtp, m, null);
        Assert.Equal(["client@exemple.fr"], reel.To.Select(a => a.Address));
        Assert.Equal(["copie@exemple.fr"], reel.CC.Select(a => a.Address));
        Assert.Equal("adv@exemple.fr", reel.From!.Address);
        Assert.Equal("adv@exemple.fr", reel.Headers["Disposition-Notification-To"]);   // accusé de lecture (EnvoideFactures)
    }

    [Fact]
    public void Destinataires_par_defaut_de_l_envoi_groupe()
    {
        List<ContactEnvoi> contacts = [new("A", "a@x.fr", false), new("B", "b@x.fr", true), new("C", "c@x.fr", true)];
        var (a, cc) = EditionArcService.DestinatairesParDefaut(contacts);
        Assert.Equal(["b@x.fr"], a);
        Assert.Equal(["c@x.fr"], cc);
        // Aucun « par défaut » : tous les contacts ARC (comme EnvoiARC)
        var (a2, cc2) = EditionArcService.DestinatairesParDefaut([new("A", "a@x.fr", false), new("B", "b@x.fr", false)]);
        Assert.Equal(["a@x.fr"], a2);
        Assert.Equal(["b@x.fr"], cc2);
        Assert.Empty(EditionArcService.DestinatairesParDefaut([]).A);
    }
}
