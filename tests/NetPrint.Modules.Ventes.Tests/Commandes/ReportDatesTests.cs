using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Services;

namespace NetPrint.Modules.Ventes.Tests.Commandes;

// Report de date en masse (NEG520 F19) : règle appliquée à une ligne
public class ReportDatesTests
{
    private static readonly DateOnly X = new(2026, 11, 12), Y = new(2026, 11, 20), Z = new(2026, 11, 25);

    [Fact]
    public void Date_de_livraison_reportee()
    {
        var r = new ReportDates(ZoneDate.Livraison, X, ZoneDate.Livraison, Y);
        Assert.Equal((Y, (DateOnly?)null), SaisieLignesService.ReporterDate(StatutsLigne.Normal, X, null, r));
        Assert.Equal((Y, (DateOnly?)Z), SaisieLignesService.ReporterDate(StatutsLigne.Normal, X, Z, r));   // la date confirmée reste
    }

    [Fact]
    public void Ligne_d_une_autre_date_non_concernee()
    {
        var r = new ReportDates(ZoneDate.Livraison, X, ZoneDate.Livraison, Y);
        Assert.Null(SaisieLignesService.ReporterDate(StatutsLigne.Normal, Z, null, r));
        Assert.Null(SaisieLignesService.ReporterDate(StatutsLigne.Normal, null, null, r));
    }

    [Fact]
    public void Date_confirmee_comme_origine_ou_destination()
    {
        // Origine confirmée : seules les lignes qui en ont une
        var parConfirmee = new ReportDates(ZoneDate.Confirmee, Z, ZoneDate.Confirmee, Y);
        Assert.Equal((X, (DateOnly?)Y), SaisieLignesService.ReporterDate(StatutsLigne.Normal, X, Z, parConfirmee));
        Assert.Null(SaisieLignesService.ReporterDate(StatutsLigne.Normal, Z, null, parConfirmee));
        // Destination confirmée : la date de livraison reste ; égale à elle, la confirmée n'est plus distincte
        Assert.Equal((X, (DateOnly?)Y), SaisieLignesService.ReporterDate(StatutsLigne.Normal, X, null, new(ZoneDate.Livraison, X, ZoneDate.Confirmee, Y)));
        Assert.Equal((X, (DateOnly?)null), SaisieLignesService.ReporterDate(StatutsLigne.Normal, X, Z, new(ZoneDate.Confirmee, Z, ZoneDate.Confirmee, X)));
    }

    [Fact]
    public void Ligne_a_la_demande_sans_date_confirmee()
    {
        Assert.Null(SaisieLignesService.ReporterDate(StatutsLigne.ALaDemande, X, null, new(ZoneDate.Livraison, X, ZoneDate.Confirmee, Y)));
        Assert.Equal((Y, (DateOnly?)null),
            SaisieLignesService.ReporterDate(StatutsLigne.ALaDemande, X, null, new(ZoneDate.Livraison, X, ZoneDate.Livraison, Y)));
    }
}
