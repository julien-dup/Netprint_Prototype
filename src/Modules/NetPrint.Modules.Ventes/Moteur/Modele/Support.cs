// Porté de Devis2017 (Net.Axilog.Model/Support.cs) le 02/11/2026 — moteur de devis, lot S1 (docs/compte-rendu-saisie-devis.md).
// Code repris tel quel, à l'espace de noms près ; les corrections métier sont signalées « CORRECTION ».
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace NetPrint.Modules.Ventes.Moteur.Modele
{
    public enum TypeSupport { Feuille, Bobine };

    public class Norme : BaseTable
    {
    }

    //public class NatureSupport : BaseTable
    //{
    //}

    public class Sorte
    {
        public string code { get; set; }
        public string nom { get; set; }
        public List<SousSorte> sousSortes;

    }

    public class SousSorte
    {
        public string code { get; set; }
        public string nom { get; set; }
        public Sorte sorte { get; set; }
        public Norme norme { get; set; }
        public int natureSupport { get; set; }

    }

    
    public class Support
    {
        public FormatBase Format { get; set; }
        public int grammage { get; set; }
        public SousSorte sousSorte { get; set; }
        public String coloris { get; set; }
        public TypeSupport type
        {
            get
            {
                if (Format.Hauteur == 0) return TypeSupport.Bobine; else return TypeSupport.Feuille;

            }
        }
        public Epaisseur epaisseur { get; set; }

        public String BCK
        {
            get
            {
                if (this.coloris == String.Empty) return "B"; else return "C";

            }
        }

        /// <summary>Convertit une quantité _qteo exprimée en _uno dans _und pour un support donné
        /// </summary>
        public static decimal ConvertQuantite(Support _sup, string _uno, string _und, decimal _qteo)
        {
            return ConvertQuantite(_sup.Format, _sup.grammage, _uno, _und, _qteo);
        }

        /// <summary>Convertit une quantité _qteo exprimée en _uno dans _und pour un format et un grammage donnés
        /// </summary>
        public static decimal ConvertQuantite(FormatBase _fmt, int _gram, string _uno, string _und, decimal _qteo)
        {
            if (_uno == _und) return _qteo;
            return (Decimal)typeof(SupportConvertionQuantite).GetMethod(String.Concat(_uno, _und)).Invoke(null, new object[] { _fmt, _gram, _qteo });
        }

        public override string ToString()
        {
            return String.Concat(this.sousSorte.sorte.code, " ", this.sousSorte.code, " ", this.grammage, " g ", this.Format.ToString(), " ", this.epaisseur.ToString());
        }

    }

    
}
