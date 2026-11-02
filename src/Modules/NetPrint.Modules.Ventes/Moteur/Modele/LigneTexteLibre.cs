// Porté de Devis2017 (Net.Axilog.Model/LigneTexteLibre.cs) le 02/11/2026 — moteur de devis, lot S1 (docs/compte-rendu-saisie-devis.md).
// Code repris tel quel, à l'espace de noms près ; les corrections métier sont signalées « CORRECTION ».
namespace NetPrint.Modules.Ventes.Moteur.Modele
{
    public class LigneTexteLibre
    {
        public int NumeroLigne { get; set; }
        public string Texte { get; set; }
        public bool Gras { get; set; }

        public LigneTexteLibre()
        {
            Texte = string.Empty;
        }

        public LigneTexteLibre(int numeroLigne, string texte, bool gras)
        {
            NumeroLigne = numeroLigne;
            Texte = texte ?? string.Empty;
            Gras = gras;
        }
    }
}
