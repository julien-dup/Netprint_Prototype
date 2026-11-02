// Porté de Devis2017 (Net.Axilog.Model/Machine.cs) le 02/11/2026 — moteur de devis, lot S1 (docs/compte-rendu-saisie-devis.md).
// Code repris tel quel, à l'espace de noms près ; les corrections métier sont signalées « CORRECTION ».
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel;

namespace NetPrint.Modules.Ventes.Moteur.Modele
{
    public enum TypeMachineImpression { plat=1, rotoFixe=2, rotoVariable=3 }

    
      
    
    public class Section : INotifyPropertyChanged
    {
        public String Id { get; set; }
        public String Nom { get; set; }
        public String NomCourt { get; set; }
        public String Atelier { get; set; }

        public override String ToString() { return String.Concat(Id, " ", Nom); }

        public override bool Equals(object obj)
        {
            if (!(obj is Section)) return false;
            return this.Id==(obj as Section).Id;

          }

        public override int GetHashCode()
        {
            return this.Id.GetHashCode();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        // Create the OnPropertyChanged method to raise the event
        protected void OnPropertyChanged(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(name));
            }
        }

    }

    public class MachineImpression : Section
    {
        public TypeMachineImpression Type { get; set; }
        public FormatBase FormatMiniSupport { get; set; }
        public FormatBase FormatMaxiImpression { get; set; }
        public FormatBase FormatMaxiSupport { get; set; }

        public int BordDeFeuille { get; set; }
        public int PriseDePince { get; set; }
        public int FinDePression { get; set; }

        public bool RectoVersoEnUnPassage { get; set; }
        public int NombreDeGroupes { get; set; }

        //public DataTable TableauCadence { get; set; }

        public int[] grammages;
        public int[] quantites;
        public int[] abattements;

        public int GetGrammageMini() { return grammages[0]; }
        public int GetGrammageMaxi() { return grammages[5]; }

        public int GetQuantiteMini() { return quantites[0]; }
        public int GetQuantiteMaxi() { return quantites[5]; }

        public int GetAbattementCadence(int _grammage, int _quantite)
        {
            //TODO Tester les bornes avant traitement         
            int x1=0, y1=0, z1=0;
            for (int x = 1; x < 6; x++)
            {
                if (_grammage<grammages[x]) { x1=x-1; break;}                

            }
            for (int y = 1; y < 6; y++)
            {
                if (_quantite < quantites[y]) { y1 = y - 1; break; }                

            }

            // calcul de l'index du tableau abattement
            z1=(y1*5)+ x1;

            return abattements[z1];

        }


    }

    public class OperationMachine
    {
        public const string FraisFixe = "F";
        public const string FraisProportionnel = "P";

        public Section section { get; set; }
        public int Operation { get; set; }
        public String Nom { get; set; }
        public String TypeFrais { get; set; }

        public decimal TauxHoraireMachine { get; set; }
        public decimal TauxHoraireMO { get; set; }

        public int CadenceStandard { get; set; }
        public decimal TempsStandard { get; set; }
        public decimal TempsMini { get; set; }

        public String Commentaire { get; set; }

        public bool AvecGacheFixe { get; set; }
        public bool AvecGacheVariable { get; set; }

        public int Sequence { get; set; }

        public override String ToString() { return String.Concat(section.NomCourt, " ", Nom); }

    }
}
