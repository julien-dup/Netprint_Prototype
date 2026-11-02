using NetPrint.Modules.Ventes.Moteur.Modele;

// Porté de Devis2017 (Net.Axilog.Tests/Model/MachineImpressionTests.cs) en xUnit le 02/11/2026.

namespace NetPrint.Modules.Ventes.Tests.MoteurDevis
{
    public class MachineImpressionTests
    {
        private MachineImpression CreateMachine()
        {
            return new MachineImpression
            {
                grammages = new int[] { 50, 100, 150, 200, 250, 300 },
                quantites = new int[] { 500, 1000, 5000, 10000, 50000, 100000 },
                abattements = new int[]
                {
                    // 5 colonnes (grammages) x 6 lignes (quantites) = 30 valeurs
                    100, 95, 90, 85, 80,   // quantite bracket 0
                     98, 93, 88, 83, 78,   // quantite bracket 1
                     96, 91, 86, 81, 76,   // quantite bracket 2
                     94, 89, 84, 79, 74,   // quantite bracket 3
                     92, 87, 82, 77, 72,   // quantite bracket 4
                     90, 85, 80, 75, 70    // quantite bracket 5 (note: not used since loop < 6)
                }
            };
        }

        [Fact]
        public void GetGrammageMini_ReturnsFirst()
        {
            var m = CreateMachine();
            Assert.Equal(50, m.GetGrammageMini());
        }

        [Fact]
        public void GetGrammageMaxi_ReturnsLast()
        {
            var m = CreateMachine();
            Assert.Equal(300, m.GetGrammageMaxi());
        }

        [Fact]
        public void GetQuantiteMini_ReturnsFirst()
        {
            var m = CreateMachine();
            Assert.Equal(500, m.GetQuantiteMini());
        }

        [Fact]
        public void GetQuantiteMaxi_ReturnsLast()
        {
            var m = CreateMachine();
            Assert.Equal(100000, m.GetQuantiteMaxi());
        }

        [Fact]
        public void GetAbattementCadence_FirstBracket()
        {
            var m = CreateMachine();
            // grammage 60 < 100 => x1=0, quantite 600 < 1000 => y1=0
            // index = 0*5 + 0 = 0 => 100
            Assert.Equal(100, m.GetAbattementCadence(60, 600));
        }

        [Fact]
        public void GetAbattementCadence_MiddleBracket()
        {
            var m = CreateMachine();
            // grammage 120 < 150 => x1=1, quantite 3000 < 5000 => y1=1
            // index = 1*5 + 1 = 6 => 93
            Assert.Equal(93, m.GetAbattementCadence(120, 3000));
        }

        [Fact]
        public void GetAbattementCadence_HighBracket()
        {
            var m = CreateMachine();
            // grammage 220 < 250 => x1=3, quantite 30000 < 50000 => y1=3
            // index = 3*5 + 3 = 18 => 79
            Assert.Equal(79, m.GetAbattementCadence(220, 30000));
        }

        [Fact]
        public void Section_Equals_ComparesByID()
        {
            var s1 = new Section { Id = "SEC01", Nom = "Section 1" };
            var s2 = new Section { Id = "SEC01", Nom = "Section Diff" };
            var s3 = new Section { Id = "SEC02", Nom = "Section 1" };

            Assert.True(s1.Equals(s2));
            Assert.False(s1.Equals(s3));
        }

        [Fact]
        public void Section_ToString_ConcatenatesIdAndNom()
        {
            var s = new Section { Id = "IMP1", Nom = "Impression" };
            Assert.Equal("IMP1 Impression", s.ToString());
        }
    }
}
