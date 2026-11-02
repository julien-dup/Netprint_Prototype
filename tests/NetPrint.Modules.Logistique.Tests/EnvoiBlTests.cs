using Microsoft.Extensions.Logging.Abstractions;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Noyau.Messagerie;
using NetPrint.Noyau.Societes;
using NetPrint.Noyau.Tiers;
using NetPrint.Noyau.Utilisateurs;
using NetPrint.Socle;
using NetPrint.Socle.Documents;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Logistique.Tests;

// Envoi d'un BL par e-mail : sans base et sans messagerie réelle (faux services), aucun message ne part
public class EnvoiBlTests
{
    private const int Bl = 808001;

    private sealed class Utilisateur : IUtilisateurCourant
    {
        public Task<UtilisateurConnecte> LireAsync() => Task.FromResult(new UtilisateurConnecte("NP11", 8, "TEST", 411100));
    }

    private sealed class Consultation(bool existe = true) : IConsultationBl
    {
        public Task<ResultatRecherche<ElementBl>> RechercherAsync(CriteresBl criteres, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<FicheBl?> LireAsync(int numeroBl, CancellationToken ct = default) => Task.FromResult(existe
            ? new FicheBl(new EnteteBl(numeroBl, new DateOnly(2024, 9, 12), 411100, 20, "DIOR", 0, "DIOR", "", "", "45000", "ORLEANS", "", [], "",
                1, 0, 10, "", "", "", 0, "", "NP11", false, null, false), [], [])
            : null);
    }

    private sealed class Edition(bool complet = true) : IEditionBl
    {
        public int Marques;
        public Task<bool> CompletAsync(int numeroBl, CancellationToken ct = default) => Task.FromResult(complet);
        public Task<DocumentGenere> EditerAsync(int numeroBl, bool apercu, CancellationToken ct = default) =>
            Task.FromResult(new DocumentGenere($"BL {numeroBl}.pdf", "%PDF-test"u8.ToArray()));
        public Task MarquerEditeAsync(int numeroBl, CancellationToken ct = default) { Marques++; return Task.CompletedTask; }
    }

    internal sealed class Contacts(params ContactEnvoi[] liste) : IContactsTiers
    {
        public DocumentEnvoye? Demande;
        public Task<IReadOnlyList<ContactEnvoi>> LireAsync(int societe, int compteGeneral, int compteClient, DocumentEnvoye document,
            CancellationToken ct = default)
        {
            Demande = document;
            return Task.FromResult<IReadOnlyList<ContactEnvoi>>(liste);
        }
    }

    internal sealed class Messagerie(bool disponible = true, bool echec = false) : IMessagerie
    {
        public readonly List<MessageBrouillon> Envoyes = [];
        public Task<EtatMessagerie> EtatAsync(int societe, string? expediteur = null, CancellationToken ct = default) =>
            Task.FromResult(new EtatMessagerie(disponible, disponible ? "" : "Messagerie désactivée", expediteur ?? "", null));
        public Task EnvoyerAsync(int societe, MessageBrouillon message, string? expediteur = null, CancellationToken ct = default)
        {
            if (echec) throw new InvalidOperationException("serveur injoignable");
            Envoyes.Add(message);
            return Task.CompletedTask;
        }
    }

    internal sealed class Journal : IJournalEnvois
    {
        public readonly List<EnvoiDocument> Lignes = [];
        public Task EcrireAsync(IReadOnlyList<EnvoiDocument> envois, CancellationToken ct = default) { Lignes.AddRange(envois); return Task.CompletedTask; }
        public Task<IReadOnlyList<EnvoiJournalise>> LireAsync(int societe, string type, int dossier, string cle, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<(string Nom, byte[] Contenu)?> LirePdfAsync(int societe, long id, string type, int dossier, string cle, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<EnvoiJournalise>> LireDocumentAsync(int societe, string type, int document, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EnvoiJournalise>>(Lignes.Where(l => l.Type == type && l.Document == document)
                .Select((l, i) => new EnvoiJournalise(i + 1, DateTime.Now, l.Profil, l.Canal, l.Statut, l.Expediteur, string.Join("; ", l.Destinataires),
                    "", "", l.Objet, l.Erreur, l.NomPdf, l.Pdf is not null)).ToList());
        public Task<(string Nom, byte[] Contenu)?> LirePdfDocumentAsync(int societe, long id, string type, int document, CancellationToken ct = default) =>
            Task.FromResult<(string, byte[])?>(Lignes.Count >= id && Lignes[(int)id - 1].Pdf is { } pdf ? (Lignes[(int)id - 1].NomPdf, pdf) : null);
    }

    internal sealed class Utilisateurs : IUtilisateurs
    {
        public Task<string> EmailAsync(string profil, CancellationToken ct = default) => Task.FromResult("np11@exemple.fr");
    }

    internal sealed class Societes : ISocietes
    {
        public Task<string?> LireNomAsync(int societe, CancellationToken ct = default) => Task.FromResult<string?>("IMPRIMERIE TEST");
        public Task<IdentiteSociete?> LireIdentiteAsync(int societe, string enseigne = "", string typeLogo = "", CancellationToken ct = default) =>
            Task.FromResult<IdentiteSociete?>(null);
    }

    private static (EnvoiBlService Service, Edition Edition, Messagerie Messagerie, Journal Journal, Contacts Contacts) Creer(
        Messagerie? messagerie = null, Contacts? contacts = null, bool existe = true, bool complet = true)
    {
        var edition = new Edition(complet);
        var m = messagerie ?? new Messagerie();
        var journal = new Journal();
        var c = contacts ?? new Contacts(new ContactEnvoi("Logistique", "logistique@client.fr", true), new ContactEnvoi("Achats", "achats@client.fr", false));
        return (new EnvoiBlService(edition, new Consultation(existe), c, m, journal, new Utilisateurs(), new Societes(), new Utilisateur(),
            NullLogger<EnvoiBlService>.Instance), edition, m, journal, c);
    }

    [Fact]
    public async Task Options_contacts_bl_proposes_et_historique()
    {
        var (service, _, _, _, contacts) = Creer();
        var o = await service.OptionsAsync(Bl);
        Assert.Equal(DocumentEnvoye.BonLivraison, contacts.Demande);
        Assert.Equal([true, false], o.Contacts.Select(c => c.Propose));   // seul le contact « par défaut » est coché
        Assert.True(o.MessagerieDisponible);
        Assert.Empty(o.Historique);

        // Sans contact « par défaut », tous sont proposés
        (service, _, _, _, _) = Creer(contacts: new Contacts(new ContactEnvoi("A", "a@client.fr", false), new ContactEnvoi("B", "b@client.fr", false)));
        Assert.All((await service.OptionsAsync(Bl)).Contacts, c => Assert.True(c.Propose));
    }

    [Fact]
    public async Task Envoi_direct_trace_et_marque_edite()
    {
        var (service, edition, messagerie, journal, _) = Creer();
        await service.EnvoyerAsync(Bl, ["logistique@client.fr", "pas une adresse"], ["achats@client.fr; LOGISTIQUE@client.fr"]);

        var m = Assert.Single(messagerie.Envoyes);
        Assert.Equal(["logistique@client.fr"], m.Destinataires);
        Assert.Equal(["achats@client.fr"], m.Copies);                  // la copie déjà destinataire est retirée
        Assert.Equal($"Bon de livraison n° {Bl}", m.Objet);
        Assert.Contains("du 12/09/2024", m.Texte);
        Assert.Equal($"BL {Bl}.pdf", Assert.Single(m.PiecesJointes).Nom);

        var trace = Assert.Single(journal.Lignes);
        Assert.Equal((TypesEnvoi.Bl, 0, Bl, CanauxEnvoi.Smtp, StatutsEnvoi.Envoye), (trace.Type, trace.Dossier, trace.Document, trace.Canal, trace.Statut));
        Assert.NotNull(trace.Pdf);
        Assert.Equal(1, edition.Marques);

        var o = await service.OptionsAsync(Bl);
        Assert.Single(o.Historique);
        Assert.NotNull(await service.PdfEnvoyeAsync(Bl, o.Historique[0].Id));
    }

    [Fact]
    public async Task Brouillon_outlook()
    {
        var (service, edition, messagerie, journal, _) = Creer();
        var eml = await service.PreparerMessageAsync(Bl, [], []);
        Assert.Equal($"BL {Bl}.eml", eml.Nom);
        Assert.StartsWith("X-Unsent: 1", System.Text.Encoding.UTF8.GetString(eml.Contenu));
        Assert.Empty(messagerie.Envoyes);
        Assert.Equal(StatutsEnvoi.Prepare, Assert.Single(journal.Lignes).Statut);
        Assert.Equal(0, edition.Marques);                                // brouillon : peut-être jamais envoyé
    }

    [Fact]
    public async Task Refus_et_echec()
    {
        var (service, edition, _, journal, _) = Creer();
        await Assert.ThrowsAsync<RegleMetierException>(() => service.EnvoyerAsync(Bl, ["pas une adresse"], []));

        (service, edition, _, journal, _) = Creer(new Messagerie(disponible: false));
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => service.EnvoyerAsync(Bl, ["a@client.fr"], []));
        Assert.Equal("Messagerie désactivée", ex.Message);
        Assert.Empty(journal.Lignes);                                    // rien n'est tenté, rien n'est tracé

        (service, edition, _, journal, _) = Creer(new Messagerie(echec: true));
        await Assert.ThrowsAsync<RegleMetierException>(() => service.EnvoyerAsync(Bl, ["a@client.fr"], []));
        Assert.Equal(StatutsEnvoi.Echec, Assert.Single(journal.Lignes).Statut);
        Assert.Equal(0, edition.Marques);                                // BL non envoyé : pas marqué édité

        // Représentant limité : ni envoi, ni PDF d'un envoi précédent
        (service, _, var messagerie, journal, _) = Creer(complet: false);
        await Assert.ThrowsAsync<RegleMetierException>(() => service.EnvoyerAsync(Bl, ["a@client.fr"], []));
        await Assert.ThrowsAsync<RegleMetierException>(() => service.PreparerMessageAsync(Bl, [], []));
        await Assert.ThrowsAsync<RegleMetierException>(() => service.PdfEnvoyeAsync(Bl, 1));
        Assert.Empty(messagerie.Envoyes);
        Assert.Empty(journal.Lignes);

        (service, _, _, _, _) = Creer(existe: false);
        await Assert.ThrowsAsync<RegleMetierException>(() => service.OptionsAsync(Bl));
    }
}
