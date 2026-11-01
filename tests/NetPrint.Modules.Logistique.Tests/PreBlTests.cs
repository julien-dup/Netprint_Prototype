using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Socle.Session;
using Xunit.Abstractions;

namespace NetPrint.Modules.Logistique.Tests;

// Pré-BL palettes : état, refus de transformation, puis lecture sur NP11F de test (société 8 : pré-BL 165260 en préparation,
// 165261 transformé en BL)
public class PreBlTests(ITestOutputHelper sortie)
{
    private static readonly AdresseBl Adresse = new(0, "", "DIOR", "", "", "45000", "ORLEANS", "45", "");

    private static FichePreBl Fiche(string etat, params PalettePreBl[] palettes) => new(
        1, etat, false, 411100, 20, "DIOR", Adresse, null, 0, "", "", "", [], "", 0, 0, 0, false, "", null,
        [new LignePreBl(1, 1, "9", 1, "", "", "ART", "", "", 100, [], false, "T", 1, 1, 0, false,
            [new LotPreBl(1, false, "123451", 1, 100, 1, 0, 1, "", false, [], palettes)])], [], "");

    private static PalettePreBl P(string statut, bool picking = false) => new("SSCC", 100, picking, statut, "");

    [Fact]
    public void Etat_et_refus_de_transformation()
    {
        Assert.Equal(EtatsPreBl.APreparer, ReglesPreBl.Etat(""));
        Assert.Equal(EtatsPreBl.EnPreparation, ReglesPreBl.Etat("E"));
        Assert.Equal(EtatsPreBl.Livre, ReglesPreBl.Etat("O"));

        Assert.Equal("", ReglesPreBl.RefusTransformation(Fiche(EtatsPreBl.EnPreparation, P("4"), P("4"))));
        Assert.Contains("déjà transformé", ReglesPreBl.RefusTransformation(Fiche(EtatsPreBl.Livre, P("4"))));
        Assert.Contains("aucune palette", ReglesPreBl.RefusTransformation(Fiche(EtatsPreBl.APreparer)));
        Assert.Contains("1 palette n'est pas pointée", ReglesPreBl.RefusTransformation(Fiche(EtatsPreBl.EnPreparation, P("4"), P("3"))));
        Assert.Contains("2 palettes ne sont pas pointées", ReglesPreBl.RefusTransformation(Fiche(EtatsPreBl.EnPreparation, P(""), P("2"))));
        Assert.Contains("picking", ReglesPreBl.RefusTransformation(Fiche(EtatsPreBl.EnPreparation, P("4"), P("4", picking: true))));
        Assert.Contains("aucune ligne", ReglesPreBl.RefusTransformation(Fiche(EtatsPreBl.APreparer) with { Lignes = [] }));

        Assert.Contains("1 palette n'est pas pointée", ReglesPreBl.RefusTransformation(Fiche(EtatsPreBl.EnPreparation, P("4"), P("5"))));
        var f = Fiche(EtatsPreBl.EnPreparation, P("4"));
        var aProduire = f with { Lignes = [f.Lignes[0], f.Lignes[0] with { Numero = 2, Lots = [f.Lignes[0].Lots[0] with { AProduire = true, PalettesSscc = [] }] }] };
        Assert.Equal("Ligne 2 (ART) : un lot est encore à produire.", ReglesPreBl.RefusTransformation(aProduire));
        var sansPalette = f with { Lignes = [f.Lignes[0], f.Lignes[0] with { Numero = 2, Lots = [] }] };
        Assert.Equal("Ligne 2 (ART) : aucune palette.", ReglesPreBl.RefusTransformation(sansPalette));
        Assert.Equal("", ReglesPreBl.RefusTransformation(sansPalette with { Lignes = [f.Lignes[0], sansPalette.Lignes[1] with { Frais = true }] }));

        Assert.True(ReglesPreBl.Pret(EtatsPreBl.EnPreparation, 3, 3, 0));
        Assert.False(ReglesPreBl.Pret(EtatsPreBl.EnPreparation, 3, 3, 1));
        Assert.False(ReglesPreBl.Pret(EtatsPreBl.EnPreparation, 3, 2, 0));
        Assert.False(ReglesPreBl.Pret(EtatsPreBl.APreparer, 0, 0, 0));
        Assert.False(ReglesPreBl.Pret(EtatsPreBl.Livre, 3, 3, 0));
    }

    private sealed class Utilisateur : IUtilisateurCourant
    {
        public Task<UtilisateurConnecte> LireAsync() =>
            Task.FromResult(new UtilisateurConnecte("NP11", BaseDeTest.Societe, "TEST", BaseDeTest.CompteGeneral));
    }

    private static PreBlService Service() =>
        new(new PreBlDb2(BaseDeTest.Configuration), new OprDb2(BaseDeTest.Configuration), new Utilisateur());

    [FactDb2]
    public async Task Liste_des_pre_bl_du_client()
    {
        var r = await Service().RechercherAsync(new CriteresPreBl { CompteGeneral = 411100, CompteClient = 20, AvecLivres = true });
        foreach (var e in r.Lignes) sortie.WriteLine($"{e.Numero} {e.Etat} {e.NomClient} {e.Pointees}/{e.Palettes} picking {e.Picking} lignes {e.Lignes}");
        Assert.Contains(r.Lignes, e => e.Numero == 165260 && e.Palettes == 3 && e.Picking == 1 && !e.Pret);
        Assert.Contains(r.Lignes, e => e.Numero == 165261 && e.Etat == EtatsPreBl.Livre);
        Assert.DoesNotContain((await Service().RechercherAsync(new CriteresPreBl { CompteGeneral = 411100, CompteClient = 20 })).Lignes,
            e => e.Etat == EtatsPreBl.Livre);
        Assert.All((await Service().RechercherAsync(new CriteresPreBl { Numero = 165260 })).Lignes, e => Assert.Equal(165260, e.Numero));
    }

    [FactDb2]
    public async Task Fiche_d_un_pre_bl_en_preparation()
    {
        var f = await Service().LireAsync(165260);
        Assert.NotNull(f);
        sortie.WriteLine($"{f.Numero} {f.Etat} {f.NomClient} {f.Adresse.Libelle} départ {f.DateDepart} refus : {f.Refus}");
        foreach (var l in f.Lignes)
        {
            sortie.WriteLine($"  {l.Numero} {l.Dossier}/{l.Cle} l.{l.LigneCommande} {l.Article} {l.Quantite}");
            foreach (var lot in l.Lots)
                sortie.WriteLine($"    lot {lot.Lot} dépôt {lot.Depot} {lot.Quantite} : " +
                                 string.Join(", ", lot.PalettesSscc.Select(p => $"{p.Sscc} {p.Quantite} st.{p.Statut}{(p.Picking ? " picking" : "")}")));
        }
        Assert.Equal(4, f.Lignes.Count);
        Assert.Equal(3, f.Lignes.SelectMany(l => l.Lots).SelectMany(l => l.PalettesSscc).Count());
        Assert.NotEqual("", f.Refus);

        var livre = await Service().LireAsync(165261);
        Assert.NotNull(livre);
        Assert.Equal(EtatsPreBl.Livre, livre.Etat);
        Assert.Contains("déjà transformé", livre.Refus);
        Assert.Null(await Service().LireAsync(999999));
    }
}
