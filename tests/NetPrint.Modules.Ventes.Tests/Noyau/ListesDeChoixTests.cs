using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Noyau.ListesDeChoix;
using NetPrint.Socle.Donnees;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Ventes.Tests.Noyau;

// Listes de choix du Noyau sur NP11F de test (lecture seule) : liste « Article ». Le Noyau n'a pas encore de projet de
// tests : ses tests d'intégration sont ici (InternalsVisibleTo du Noyau).
public class ListesDeChoixTests
{
    private sealed class Utilisateur : IUtilisateurCourant
    {
        public Task<UtilisateurConnecte> LireAsync() =>
            Task.FromResult(new UtilisateurConnecte("NP11", BaseDeTest.Societe, "TEST", BaseDeTest.CompteGeneral));
    }

    [FactDb2]
    public async Task Articles_du_client_par_code_ou_designation()
    {
        var listes = new ListesDeChoixDb2(BaseDeTest.Configuration, new Utilisateur());
        var client = $"{BaseDeTest.CompteGeneral} {BaseDeTest.CompteClient}";
        // Un article du client d'essai et un mot d'au moins 3 lettres de sa désignation
        string article, mot;
        await using (var cnx = new OdbcConnection(BaseDeTest.Configuration.GetConnectionString("Db2")))
        {
            await cnx.OpenAsync();
            await using var cmd = new OdbcCommand($"""
                SELECT AARTIC, ADES FROM {BaseDb2.Bibliotheque(BaseDeTest.Configuration)}.SPF010
                WHERE RSOC = ? AND AGEN = ? AND ACLI = ? AND ADES <> ' ' ORDER BY AARTIC
                """, cnx);
            cmd.Parameters.AddWithValue("s", BaseDeTest.Societe);
            cmd.Parameters.AddWithValue("g", BaseDeTest.CompteGeneral);
            cmd.Parameters.AddWithValue("c", BaseDeTest.CompteClient);
            await using var rd = await cmd.ExecuteReaderAsync();
            (article, mot) = ("", "");
            while (mot == "" && await rd.ReadAsync())
            {
                article = rd.GetString(0).Trim();
                mot = rd.GetString(1).Split(' ').FirstOrDefault(m => m.Length >= 3 && m.All(char.IsLetterOrDigit)) ?? "";
            }
            Assert.NotEqual("", mot);
        }

        // Recherche obligatoire : rien sans mot
        var vide = await listes.RechercherAsync(ListeChoix.Article, "", client);
        Assert.True(vide.RechercheObligatoire);
        Assert.Empty(vide.Lignes);

        // Par code, chez le client : l'article est trouvé (une ligne par indice), article et indice en codes
        var parCode = await listes.RechercherAsync(ListeChoix.Article, article, client);
        Assert.Contains(parCode.Lignes, l => l.Code == article && l.Codes.Count == 2 && l.Valeurs.Count == 5);

        // Par mot de la désignation : les articles de ce client seulement ; sans client, au moins autant
        var duClient = await listes.RechercherAsync(ListeChoix.Article, mot, client);
        Assert.NotEmpty(duClient.Lignes);
        Assert.Single(duClient.Lignes.Select(l => l.Valeurs[4]).Distinct());
        var tous = await listes.RechercherAsync(ListeChoix.Article, mot);
        Assert.True(tous.Lignes.Count >= duClient.Lignes.Count);

        // Même n° auxiliaire sous un autre compte général : aucun article de ce client
        var autreCompte = await listes.RechercherAsync(ListeChoix.Article, mot, $"{BaseDeTest.CompteGeneral + 1} {BaseDeTest.CompteClient}");
        Assert.Empty(autreCompte.Lignes);
    }
}
