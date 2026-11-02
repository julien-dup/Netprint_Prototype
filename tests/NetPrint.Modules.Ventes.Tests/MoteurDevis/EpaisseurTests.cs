using NetPrint.Modules.Ventes.Moteur.Modele;

// Porté de Devis2017 (Net.Axilog.Tests/Model/EpaisseurTests.cs) en xUnit le 02/11/2026.

namespace NetPrint.Modules.Ventes.Tests.MoteurDevis
{
    public class EpaisseurTests
    {
        // GetEpaisseurEnMM
        [Theory]
        [InlineData("MM", 0.5, 0.5)]
        [InlineData("/10", 5.0, 0.5)]
        [InlineData("\u00B5", 500.0, 0.5)]
        public void GetEpaisseurEnMM_ConvertsCorrectly(string unite, double longueur, double expected)
        {
            var ep = new Epaisseur { Longueur = (decimal)longueur, Unite = unite };
            Assert.Equal((decimal)expected, ep.GetEpaisseurEnMM());
        }

        // GetEpaisseurEn10MM
        [Theory]
        [InlineData("MM", 0.5, 5.0)]
        [InlineData("/10", 5.0, 5.0)]
        [InlineData("\u00B5", 500.0, 5.0)]
        public void GetEpaisseurEn10MM_ConvertsCorrectly(string unite, double longueur, double expected)
        {
            var ep = new Epaisseur { Longueur = (decimal)longueur, Unite = unite };
            Assert.Equal((decimal)expected, ep.GetEpaisseurEn10MM());
        }

        // GetEpaisseurEnMicron
        [Theory]
        [InlineData("MM", 0.5, 500.0)]
        [InlineData("/10", 5.0, 500.0)]
        [InlineData("\u00B5", 500.0, 500.0)]
        public void GetEpaisseurEnMicron_ConvertsCorrectly(string unite, double longueur, double expected)
        {
            var ep = new Epaisseur { Longueur = (decimal)longueur, Unite = unite };
            Assert.Equal((decimal)expected, ep.GetEpaisseurEnMicron());
        }

        [Fact]
        public void GetEpaisseurEnMM_UnknownUnite_ReturnsLongueur()
        {
            var ep = new Epaisseur { Longueur = 42M, Unite = "XYZ" };
            Assert.Equal(42M, ep.GetEpaisseurEnMM());
        }

        [Fact]
        public void GetEpaisseurEn10MM_UnknownUnite_ReturnsLongueur()
        {
            var ep = new Epaisseur { Longueur = 42M, Unite = "XYZ" };
            Assert.Equal(42M, ep.GetEpaisseurEn10MM());
        }

        [Fact]
        public void GetEpaisseurEnMicron_UnknownUnite_ReturnsLongueur()
        {
            var ep = new Epaisseur { Longueur = 42M, Unite = "XYZ" };
            Assert.Equal(42M, ep.GetEpaisseurEnMicron());
        }
    }
}
