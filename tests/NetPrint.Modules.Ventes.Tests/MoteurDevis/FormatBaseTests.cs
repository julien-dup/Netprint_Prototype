using NetPrint.Modules.Ventes.Moteur.Modele;

// Porté de Devis2017 (Net.Axilog.Tests/Model/FormatBaseTests.cs) en xUnit le 02/11/2026.

namespace NetPrint.Modules.Ventes.Tests.MoteurDevis
{
    public class FormatBaseTests
    {
        [Fact]
        public void Constructor_Default_InitializesZero()
        {
            var fmt = new FormatBase();
            Assert.Equal(0.0M, fmt.Largeur);
            Assert.Equal(0.0M, fmt.Hauteur);
            Assert.Equal(FormatBase.UNITEMM, fmt.UniteHauteur);
        }

        [Fact]
        public void Constructor_WithDimensions_SetsValues()
        {
            var fmt = new FormatBase(210M, 297M);
            Assert.Equal(210M, fmt.Largeur);
            Assert.Equal(297M, fmt.Hauteur);
            Assert.Equal(FormatBase.UNITEMM, fmt.UniteHauteur);
        }

        [Fact]
        public void Constructor_WithUnite_SetsPouce()
        {
            var fmt = new FormatBase(100M, 120M, FormatBase.UNITEPOUCE);
            Assert.Equal(FormatBase.UNITEPOUCE, fmt.UniteHauteur);
        }

        // GetHauteurMM
        [Fact]
        public void GetHauteurMM_UniteMillimetre_ReturnsHauteurDirectly()
        {
            var fmt = new FormatBase(210M, 297M);
            Assert.Equal(297M, fmt.GetHauteurMM());
        }

        [Fact]
        public void GetHauteurMM_UnitePouce_ConvertsToMM()
        {
            // 100 pouces (stocké en dixièmes) = 10 pouces = 254 mm
            var fmt = new FormatBase(210M, 100M, FormatBase.UNITEPOUCE);
            Assert.Equal(254.0M, fmt.GetHauteurMM());
        }

        [Fact]
        public void GetHauteurMM_Pouce_12Dixiemes()
        {
            // 12 dixièmes de pouce = 1.2 pouce = 1.2 * 25.4 = 30.48 mm
            var fmt = new FormatBase(100M, 12M, FormatBase.UNITEPOUCE);
            Assert.Equal(30.48M, fmt.GetHauteurMM());
        }

        // GetHauteurM
        [Fact]
        public void GetHauteurM_UniteMillimetre_DividesByThousand()
        {
            var fmt = new FormatBase(210M, 1000M);
            Assert.Equal(1.0M, fmt.GetHauteurM());
        }

        [Fact]
        public void GetHauteurM_UnitePouce_ConvertsAndDivides()
        {
            // 100 dixièmes = 10 pouces = 254 mm = 0.254 m
            var fmt = new FormatBase(210M, 100M, FormatBase.UNITEPOUCE);
            Assert.Equal(0.254M, fmt.GetHauteurM());
        }

        // GetSurfaceM2
        [Fact]
        public void GetSurfaceM2_Standard_CalculatesCorrectly()
        {
            // 1000mm x 1000mm = 1 m²
            var fmt = new FormatBase(1000M, 1000M);
            Assert.Equal(1.0M, fmt.GetSurfaceM2());
        }

        [Fact]
        public void GetSurfaceM2_A4_CalculatesCorrectly()
        {
            // 210mm x 297mm = 0.06237 m²
            var fmt = new FormatBase(210M, 297M);
            Assert.Equal(0.062370M, fmt.GetSurfaceM2());
        }

        [Fact]
        public void GetSurfaceM2_WithPouce_UsesConvertedHauteur()
        {
            // Largeur 1000mm, Hauteur 100 dixièmes = 254mm => 1000*254/1000000 = 0.254 m²
            var fmt = new FormatBase(1000M, 100M, FormatBase.UNITEPOUCE);
            Assert.Equal(0.254M, fmt.GetSurfaceM2());
        }

        // ToString
        [Fact]
        public void ToString_WithBothDimensions_ReturnsLxH()
        {
            var fmt = new FormatBase(210M, 297M);
            Assert.Equal("210x297", fmt.ToString());
        }

        [Fact]
        public void ToString_HauteurZero_ReturnsLargeurOnly()
        {
            var fmt = new FormatBase(500M, 0M);
            Assert.Equal("500", fmt.ToString());
        }

        // FormatFini
        [Fact]
        public void FormatFini_Constructor_SetsCote3()
        {
            var fmt = new FormatFini(210M, 297M, 50M);
            Assert.Equal(210M, fmt.Largeur);
            Assert.Equal(297M, fmt.Hauteur);
            Assert.Equal(50M, fmt.Cote3);
        }

        [Fact]
        public void FormatFini_ConstructorWithoutCote3_DefaultsToZero()
        {
            var fmt = new FormatFini(210M, 297M);
            Assert.Equal(0.0M, fmt.Cote3);
        }
    }
}
