// Porté de Devis2017 (Net.Axilog.Model/OutilBobine.cs) le 02/11/2026 — moteur de devis, lot S1 (docs/compte-rendu-saisie-devis.md).
// Code repris tel quel, à l'espace de noms près ; les corrections métier sont signalées « CORRECTION ».
using System;

namespace NetPrint.Modules.Ventes.Moteur.Modele
{
    public class OutilBobine
    {
        public int Code { get; set; }
        public String Designation { get; set; }
        public int NombrePoses { get; set; }

        public override String ToString() { return String.Concat(Code, " ", Designation); }
    }
}
