using System.Security.Claims;
using NetPrint.Socle.Donnees;
using NetPrint.Socle.Session;

namespace NetPrint.Socle.Tests;

public class BaseDb2Tests
{
    [Theory]
    [InlineData(null, "NP11F")]
    [InlineData("", "NP11F")]
    [InlineData("np11f", "NP11F")]
    [InlineData("NP11P", "NP11P")]
    public void Bibliotheque_valide(string? valeur, string attendu) =>
        Assert.Equal(attendu, BaseDb2.Bibliotheque(valeur));

    [Theory]
    [InlineData("NP11F; DROP TABLE X")]
    [InlineData("NP11F.AA0007")]
    [InlineData("TROPLONGNOM1")]
    [InlineData("1NP11F")]
    public void Bibliotheque_invalide_refusee(string valeur) =>
        Assert.Throws<InvalidOperationException>(() => BaseDb2.Bibliotheque(valeur));

    [Fact]
    public void Date_AS400_valide() => Assert.Equal(new DateOnly(2026, 10, 29), BaseDb2.Date(2026, 10, 29));

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(2026, 2, 30)]
    [InlineData(2026, 13, 1)]
    public void Date_AS400_vide_ou_invalide(int a, int m, int j) => Assert.Null(BaseDb2.Date(a, m, j));

    [Fact]
    public void AAAAMMJJ() => Assert.Equal(20261029, BaseDb2.AAAAMMJJ(new DateOnly(2026, 10, 29)));
}

public class BaseNetPrintTests
{
    [Theory]
    [InlineData("SELECT * FROM T WHERE A = ? AND B = ?", "SELECT * FROM T WHERE A = $1 AND B = $2")]
    [InlineData("SELECT '?' FROM T WHERE A = ?", "SELECT '?' FROM T WHERE A = $1")]
    [InlineData("SELECT 1", "SELECT 1")]
    public void Positionnel(string sql, string attendu) => Assert.Equal(attendu, BaseNetPrint.Positionnel(sql));
}

public class SessionTests
{
    [Fact]
    public void Utilisateur_non_connecte_est_anonyme()
    {
        var u = new ClaimsPrincipal(new ClaimsIdentity()).Utilisateur();
        Assert.False(u.EstConnecte);
        Assert.Equal(0, u.Societe);
    }

    [Fact]
    public void Utilisateur_connecte_lu_depuis_la_session()
    {
        var identite = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "JDUPARC"),
            new Claim(SessionNetPrint.ClaimSociete, "55"),
            new Claim(SessionNetPrint.ClaimNomSociete, "AXIOGRAPH"),
            new Claim(SessionNetPrint.ClaimCompteGeneral, "411000"),
        ], "test");
        var u = new ClaimsPrincipal(identite).Utilisateur();
        Assert.True(u.EstConnecte);
        Assert.Equal(new UtilisateurConnecte("JDUPARC", 55, "AXIOGRAPH", 411000), u);
        Assert.Equal("JD", u.Initiales);
    }
}
