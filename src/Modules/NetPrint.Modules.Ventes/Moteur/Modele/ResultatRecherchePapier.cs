// Porté de Devis2017 (Net.Axilog.Model/ResultatRecherchePapier.cs) le 02/11/2026 — moteur de devis, lot S1 (docs/compte-rendu-saisie-devis.md).
// Code repris tel quel, à l'espace de noms près ; les corrections métier sont signalées « CORRECTION ».
using System;

namespace NetPrint.Modules.Ventes.Moteur.Modele
{
    public class ResultatRecherchePapier
    {
        public int CodePapier { get; set; }
        public string Sorte { get; set; }
        public string SousSorte { get; set; }
        public int Grammage { get; set; }
        public string Coloris { get; set; }
        public decimal FormatLargeur { get; set; }
        public decimal FormatHauteur { get; set; }
        public int Epaisseur { get; set; }
        public decimal PrixAchat { get; set; }
        public String UniteAchat { get; set; }
        public int Conditionnement { get; set; }

        public override string ToString()
        {
            return string.Concat(CodePapier, " ", Sorte, "/", SousSorte, " ", Grammage, "g");
        }
    }
}
