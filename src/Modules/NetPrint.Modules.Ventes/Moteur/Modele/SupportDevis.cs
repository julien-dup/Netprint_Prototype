// Porté de Devis2017 (Net.Axilog.Model/SupportDevis.cs) le 02/11/2026 — moteur de devis, lot S1 (docs/compte-rendu-saisie-devis.md).
// Code repris tel quel, à l'espace de noms près ; les corrections métier sont signalées « CORRECTION ».
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NetPrint.Modules.Ventes.Moteur.Modele
{
    public class SupportDevis : Support
    {
        public bool NonCode { get; set; }
        public String Provenance { get; set; }
        public decimal PrixAchat { get; set; }
        public String UniteAchat { get; set; }
        public int ConditionnePar { get; set; }
    }
}
