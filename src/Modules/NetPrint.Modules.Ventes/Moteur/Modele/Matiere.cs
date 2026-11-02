// Porté de Devis2017 (Net.Axilog.Model/Matiere.cs) le 02/11/2026 — moteur de devis, lot S1 (docs/compte-rendu-saisie-devis.md).
// Code repris tel quel, à l'espace de noms près ; les corrections métier sont signalées « CORRECTION ».
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NetPrint.Modules.Ventes.Moteur.Modele
{
    public class Matiere
    {
        public int Id { get; set; }
        public String Nom { get; set; }
        public String CodeRecherche { get; set; }
        public Decimal PrixAchat { get; set; }
        public Decimal Coefficient { get; set; }
        public DateTime DateTarif { get; set; }
        public bool AvecGacheVariable { get; set; }

        public override String ToString() { return String.Concat(Id, " ", Nom); }
    }
}
