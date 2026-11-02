using NetPrint.Modules.Ventes.Moteur.Modele;

// Porté de Devis2017 (Net.Axilog.Tests/Model/ElementDevisTests.cs) en xUnit le 02/11/2026.

namespace NetPrint.Modules.Ventes.Tests.MoteurDevis
{
    public class ElementDevisTests
    {
        private Devis CreateDevisWithQuantite(int quantite, int gacheVariable = 0)
        {
            var devis = new Devis();
            devis.Quantite = quantite;
            devis.GacheVariable = gacheVariable;
            devis.formatFini = new FormatFini(210M, 297M);
            return devis;
        }

        private ElementDevis CreateElement(int quantite = 1000, int poses = 4, int elementsIdentiques = 1,
            decimal tirageL = 420M, decimal tirageH = 594M, int refente = 1)
        {
            var devis = CreateDevisWithQuantite(0, 5);
            var elt = new ElementDevis(devis, 1, "Test");
            elt.Quantite = quantite;
            elt.Poses = poses;
            elt.ElementsIdentiques = elementsIdentiques;
            elt.formatTirage = new FormatBase(tirageL, tirageH);
            elt.Refente = refente;
            elt.GacheVariable = 5M;
            return elt;
        }

        // ToursUtiles
        [Fact]
        public void ToursUtiles_SimpleDivision()
        {
            var elt = CreateElement(quantite: 1000, poses: 4, elementsIdentiques: 1);
            Assert.Equal(250, elt.ToursUtiles);
        }

        [Fact]
        public void ToursUtiles_WithCeiling()
        {
            // 1001 / 4 = 250.25 => Ceiling = 251
            var elt = CreateElement(quantite: 1001, poses: 4, elementsIdentiques: 1);
            Assert.Equal(251, elt.ToursUtiles);
        }

        [Fact]
        public void ToursUtiles_WithElementsIdentiques()
        {
            // 1000 * 2 / 4 = 500
            var elt = CreateElement(quantite: 1000, poses: 4, elementsIdentiques: 2);
            Assert.Equal(500, elt.ToursUtiles);
        }

        [Fact]
        public void ToursUtiles_PosesOne_NoDivision()
        {
            // poses <= 1 : pas de division
            var elt = CreateElement(quantite: 1000, poses: 1, elementsIdentiques: 1);
            Assert.Equal(1000, elt.ToursUtiles);
        }

        // GetToursUtilesMP (base 1000)
        [Fact]
        public void GetToursUtilesMP_Base1000()
        {
            var elt = CreateElement(poses: 4, elementsIdentiques: 1);
            // 1000 * 1 / 4 = 250
            Assert.Equal(250, elt.GetToursUtilesMP());
        }

        // MetrageUtile
        [Fact]
        public void MetrageUtile_CalculatesCorrectly()
        {
            var elt = CreateElement(quantite: 1000, poses: 4, tirageH: 594M);
            // ToursUtiles = 250, HauteurM = 594/1000 = 0.594
            // MetrageUtile = 250 * 0.594 = 148.5
            Assert.Equal(148.5M, elt.MetrageUtile);
        }

        // ToursGacheVariable
        [Fact]
        public void ToursGacheVariable_CalculatesPercentage()
        {
            var elt = CreateElement(quantite: 1000, poses: 4);
            elt.GacheVariable = 10M;
            // ToursUtiles = 250, Ceiling(250 * 10 / 100) = 25
            Assert.Equal(25, elt.ToursGacheVariable);
        }

        // FeuillesUtiles
        [Fact]
        public void FeuillesUtiles_WithoutRefente_EqualsToursUtiles()
        {
            var elt = CreateElement(quantite: 1000, poses: 4, refente: 1);
            Assert.Equal(250M, elt.FeuillesUtiles);
        }

        [Fact]
        public void FeuillesUtiles_WithRefente_Divides()
        {
            var elt = CreateElement(quantite: 1000, poses: 4, refente: 2);
            // ToursUtiles = 250, 250 / 2 = 125
            Assert.Equal(125M, elt.FeuillesUtiles);
        }

        // ToursTotal
        [Fact]
        public void ToursTotal_SumsAllTours()
        {
            var elt = CreateElement(quantite: 1000, poses: 4);
            elt.ToursGacheFixe = 50;
            // ToursUtiles(250) + ToursGacheFixe(50) + ToursGacheVariable(Ceil(250*5/100)=13)
            Assert.Equal(250 + 50 + 13, elt.ToursTotal);
        }

        // GetQuantite
        [Fact]
        public void GetQuantite_ElementHasQuantite_ReturnsElementQuantite()
        {
            var elt = CreateElement(quantite: 500);
            Assert.Equal(500, elt.GetQuantite());
        }

        [Fact]
        public void GetQuantite_ElementZero_ReturnsDevisQuantite()
        {
            var devis = CreateDevisWithQuantite(2000);
            var elt = new ElementDevis(devis, 1, "Test");
            elt.Quantite = 0;
            Assert.Equal(2000, elt.GetQuantite());
        }

        // GetLibelleColoris
        [Fact]
        public void GetLibelleColoris_RectoOnly()
        {
            var elt = CreateElement();
            elt.CouleursRecto = 4;
            elt.CouleursVerso = 0;
            elt.CouleursBascule = 0;
            Assert.Equal("4R\u00B0", elt.GetLibelleColoris());
        }

        [Fact]
        public void GetLibelleColoris_RectoVerso()
        {
            var elt = CreateElement();
            elt.CouleursRecto = 4;
            elt.CouleursVerso = 2;
            elt.CouleursBascule = 0;
            Assert.Equal("4R\u00B0 2V\u00B0", elt.GetLibelleColoris());
        }

        [Fact]
        public void GetLibelleColoris_AvecBascule()
        {
            var elt = CreateElement();
            elt.CouleursRecto = 4;
            elt.CouleursVerso = 4;
            elt.CouleursBascule = 4;
            Assert.Equal("4R\u00B0 4V\u00B0 4BAS", elt.GetLibelleColoris());
        }

        // NombreDePassagesImpression
        [Fact]
        public void NombreDePassagesImpression_SansImpression_ReturnsZero()
        {
            var elt = CreateElement();
            elt.SansImpression = true;
            Assert.Equal(0, elt.NombreDePassagesImpression);
        }

        [Fact]
        public void NombreDePassagesImpression_ZeroCouleurs_ReturnsOne()
        {
            var elt = CreateElement();
            elt.SansImpression = false;
            elt.CouleursRecto = 0;
            elt.CouleursVerso = 0;
            elt.machineImpression = new MachineImpression { Type = TypeMachineImpression.plat, NombreDeGroupes = 4 };
            Assert.Equal(1, elt.NombreDePassagesImpression);
        }

        [Fact]
        public void NombreDePassagesImpression_Plat_RectoVersoSepare()
        {
            var elt = CreateElement();
            elt.SansImpression = false;
            elt.CouleursRecto = 4;
            elt.CouleursVerso = 2;
            elt.CouleursBascule = 0;
            elt.machineImpression = new MachineImpression
            {
                Type = TypeMachineImpression.plat,
                RectoVersoEnUnPassage = false,
                NombreDeGroupes = 4
            };
            // passR = Ceil(4/4) = 1, passV = Ceil(2/4) = 1 => 2
            Assert.Equal(2, elt.NombreDePassagesImpression);
        }

        [Fact]
        public void NombreDePassagesImpression_Plat_AvecBascule()
        {
            var elt = CreateElement();
            elt.SansImpression = false;
            elt.CouleursRecto = 4;
            elt.CouleursVerso = 4;
            elt.CouleursBascule = 4;
            elt.machineImpression = new MachineImpression
            {
                Type = TypeMachineImpression.plat,
                RectoVersoEnUnPassage = false,
                NombreDeGroupes = 4
            };
            // Bascule > 0 : Ceil(Max(4,4) / 4) = 1
            Assert.Equal(1, elt.NombreDePassagesImpression);
        }

        [Fact]
        public void NombreDePassagesImpression_Plat_RectoVersoEnUnPassage()
        {
            var elt = CreateElement();
            elt.SansImpression = false;
            elt.CouleursRecto = 2;
            elt.CouleursVerso = 2;
            elt.CouleursBascule = 0;
            elt.machineImpression = new MachineImpression
            {
                Type = TypeMachineImpression.plat,
                RectoVersoEnUnPassage = true,
                NombreDeGroupes = 6
            };
            // 2+2 = 4 <= 6 groupes => 1 passage
            Assert.Equal(1, elt.NombreDePassagesImpression);
        }

        // PropertyChanged
        [Fact]
        public void Quantite_Setter_RaisesPropertyChanged()
        {
            var elt = CreateElement();
            string changedProp = null;
            elt.PropertyChanged += (s, e) => changedProp = e.PropertyName;
            elt.Quantite = 999;
            Assert.Equal("Quantite", changedProp);
        }

        [Fact]
        public void Quantite_Setter_SetsFlags()
        {
            var elt = CreateElement();
            elt.ImpressionAValoriser = false;
            elt.CadenceARecalculer = false;
            elt.Quantite = 999;
            Assert.True(elt.ImpressionAValoriser);
            Assert.True(elt.CadenceARecalculer);
        }
    }
}
