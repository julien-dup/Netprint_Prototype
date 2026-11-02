using NetPrint.Modules.Ventes.Moteur.Modele;

// Porté de Devis2017 (Net.Axilog.Tests/Model/TableauCalculTests.cs) en xUnit le 02/11/2026.

namespace NetPrint.Modules.Ventes.Tests.MoteurDevis
{
    public class TableauCalculTests
    {
        private TableauCalcul CreateTableau(string modeX = ">", string modeY = ">")
        {
            var tc = new TableauCalcul();
            tc.critereX = "2";
            tc.critereY = "2";
            tc.modeRechercheX = modeX;
            tc.modeRechercheY = modeY;
            tc.abcisses = new decimal[10];
            tc.ordonnees = new decimal[20];
            tc.resultats = new decimal[200];

            // Abscisses: 10, 20
            tc.abcisses[0] = 10M;
            tc.abcisses[1] = 20M;

            // Ordonnees: 100, 200
            tc.ordonnees[0] = 100M;
            tc.ordonnees[1] = 200M;

            // Resultats: 4 values (2x2)
            // [ord0,abs0]=1.5, [ord0,abs1]=2.5
            // [ord1,abs0]=3.5, [ord1,abs1]=4.5
            tc.resultats[0] = 1.5M;   // ord0*10 + abs0 = 0
            tc.resultats[1] = 2.5M;   // ord0*10 + abs1 = 1
            tc.resultats[10] = 3.5M;  // ord1*10 + abs0 = 10
            tc.resultats[11] = 4.5M;  // ord1*10 + abs1 = 11

            return tc;
        }

        [Fact]
        public void GetResult_ExactMatch_ReturnsCorrectValue()
        {
            var tc = CreateTableau(modeX: "=", modeY: "=");
            Assert.Equal(1.5M, tc.GetResult(10M, 100M));
        }

        [Fact]
        public void GetResult_GreaterThan_FindsBracket()
        {
            var tc = CreateTableau(modeX: ">", modeY: ">");
            // 5 < 10 => x=0, 50 < 100 => y=0
            Assert.Equal(1.5M, tc.GetResult(5M, 50M));
        }

        [Fact]
        public void GetResult_SecondBracket()
        {
            var tc = CreateTableau(modeX: ">", modeY: ">");
            // 15 < 20 => x=1, 150 < 200 => y=1
            Assert.Equal(4.5M, tc.GetResult(15M, 150M));
        }

        [Fact]
        public void GetResult_NotFound_ReturnsMinus1()
        {
            var tc = CreateTableau(modeX: "=", modeY: "=");
            // 99 doesn't match any abscisse
            Assert.Equal(-1.0M, tc.GetResult(99M, 100M));
        }
    }
}
