using Microsoft.Extensions.Logging.Abstractions;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Noyau.Messagerie;
using NetPrint.Noyau.Tiers;
using NetPrint.Socle;
using NetPrint.Socle.Documents;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Logistique.Tests;

// Envoi groupé des pré-BL : sans base et sans messagerie réelle (faux services de l'envoi des BL), aucun message ne part
public class EnvoiPreBlTests
{
    private sealed class Utilisateur : IUtilisateurCourant
    {
        public Task<UtilisateurConnecte> LireAsync() => Task.FromResult(new UtilisateurConnecte("NP11", 29, "TEST", 411029));
    }

    // Pré-BL 1 et 2 : client 411029 1 ; 3 : autre client ; 4 : transformé en BL
    private sealed class PreBls : IPreBl
    {
        private static readonly AdresseBl Adresse = new(22, "", "ACR", "", "", "93200", "SAINT-DENIS", "93", "");

        public Task<FichePreBl?> LireAsync(int numero, CancellationToken ct = default) => Task.FromResult<FichePreBl?>(numero switch
        {
            1 or 2 or 4 => Fiche(numero, 1, numero == 4 ? EtatsPreBl.Livre : EtatsPreBl.APreparer),
            3 => Fiche(3, 4, EtatsPreBl.APreparer),
            _ => null,
        });

        private static FichePreBl Fiche(int numero, int client, string etat) => new(numero, etat, false, 411029, client, "ACR", Adresse,
            new DateOnly(2026, 11, 3), 1, "", "", "", [], "", 0, 0, 0, false, "", null, [], [], "");

        public Task<DroitsOpr> DroitsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ResultatRecherche<ElementPreBl>> RechercherAsync(CriteresPreBl criteres, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TransformationPreBl> TransformerAsync(int numero, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Edition : IEditionPreBl
    {
        public Task<DocumentGenere> EditerAsync(int numero, bool apercu, CancellationToken ct = default) =>
            Task.FromResult(new DocumentGenere($"Pré-BL {numero}.pdf", "%PDF-test"u8.ToArray()));
        public Task<DocumentGenere> FeuillePreparationAsync(int numero, int? depot, bool codesBarres, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<DocumentGenere> FeuilleCamionAsync(IReadOnlyList<int> numeros, bool valorisee, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static (EnvoiPreBlService Service, EnvoiBlTests.Messagerie Messagerie, EnvoiBlTests.Journal Journal) Creer(bool disponible = true)
    {
        var m = new EnvoiBlTests.Messagerie(disponible);
        var journal = new EnvoiBlTests.Journal();
        var contacts = new EnvoiBlTests.Contacts(new ContactEnvoi("Logistique", "logistique@client.fr", true));
        return (new EnvoiPreBlService(new PreBls(), new Edition(), contacts, m, journal, new EnvoiBlTests.Utilisateurs(), new EnvoiBlTests.Societes(),
            new Utilisateur(), NullLogger<EnvoiPreBlService>.Instance), m, journal);
    }

    [Fact]
    public async Task Envoi_groupe_un_message_un_pdf_et_une_trace_par_pre_bl()
    {
        var (service, messagerie, journal) = Creer();
        Assert.Equal([true], (await service.OptionsAsync([2, 1])).Contacts.Select(c => c.Propose));
        await service.EnvoyerAsync([2, 1, 2], ["logistique@client.fr"], []);

        var m = Assert.Single(messagerie.Envoyes);
        Assert.Equal("Bons de livraison n° 1, 2", m.Objet);
        Assert.Equal(["Bon de livraison 1.pdf", "Bon de livraison 2.pdf"], m.PiecesJointes.Select(p => p.Nom));
        Assert.Equal([1, 2], journal.Lignes.Select(l => l.Document));
        Assert.All(journal.Lignes, l => Assert.Equal((EnvoiPreBlService.TypeEnvoi, StatutsEnvoi.Envoye), (l.Type, l.Statut)));
        Assert.Single(journal.Lignes.Select(l => l.Message).Distinct());   // un seul message
        Assert.Equal(2, (await service.OptionsAsync([1, 2])).Historique.Count);
    }

    [Fact]
    public async Task Brouillon_d_un_seul_pre_bl()
    {
        var (service, messagerie, journal) = Creer(disponible: false);
        var eml = await service.PreparerMessageAsync([1], [], []);
        Assert.Equal("Pré-BL 1.eml", eml.Nom);
        Assert.StartsWith("X-Unsent: 1", System.Text.Encoding.UTF8.GetString(eml.Contenu));
        Assert.Empty(messagerie.Envoyes);
        Assert.Equal(StatutsEnvoi.Prepare, Assert.Single(journal.Lignes).Statut);
        await Assert.ThrowsAsync<RegleMetierException>(() => service.EnvoyerAsync([1], ["logistique@client.fr"], []));   // messagerie indisponible
    }

    [Fact]
    public async Task Refus()
    {
        var (service, messagerie, journal) = Creer();
        Assert.Contains("même client", (await Assert.ThrowsAsync<RegleMetierException>(() => service.EnvoyerAsync([1, 3], ["a@client.fr"], []))).Message);
        Assert.Contains("transformé", (await Assert.ThrowsAsync<RegleMetierException>(() => service.EnvoyerAsync([4], ["a@client.fr"], []))).Message);
        Assert.Contains("n'existe pas", (await Assert.ThrowsAsync<RegleMetierException>(() => service.EnvoyerAsync([9], ["a@client.fr"], []))).Message);
        Assert.Contains("au moins un pré-BL", (await Assert.ThrowsAsync<RegleMetierException>(() => service.OptionsAsync([]))).Message);
        Assert.Contains("50", (await Assert.ThrowsAsync<RegleMetierException>(() =>
            service.OptionsAsync([.. Enumerable.Range(1, 51)]))).Message);
        Assert.Contains("destinataire", (await Assert.ThrowsAsync<RegleMetierException>(() => service.EnvoyerAsync([1], ["pas une adresse"], []))).Message);
        Assert.Empty(messagerie.Envoyes);
        Assert.Empty(journal.Lignes);
    }
}
