// Porté de Devis2017 (Net.Axilog.Model/ResultatValidation.cs) le 02/11/2026 — moteur de devis, lot S1 (docs/compte-rendu-saisie-devis.md).
// Code repris tel quel, à l'espace de noms près ; les corrections métier sont signalées « CORRECTION ».
namespace NetPrint.Modules.Ventes.Moteur.Modele
{
    public enum SeveriteValidation
    {
        Erreur,
        Avertissement
    }

    public class ResultatValidation
    {
        public string ChampConcerne { get; set; }
        public string Message { get; set; }
        public SeveriteValidation Severite { get; set; }
    }
}
