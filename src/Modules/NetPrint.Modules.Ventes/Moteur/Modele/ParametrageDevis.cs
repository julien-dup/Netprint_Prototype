// Porté de Devis2017 (Net.Axilog.Model/ParametrageDevis.cs) le 02/11/2026 — moteur de devis, lot S1 (docs/compte-rendu-saisie-devis.md).
// Code repris tel quel, à l'espace de noms près ; les corrections métier sont signalées « CORRECTION ».
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NetPrint.Modules.Ventes.Moteur.Modele
{

    public class ParametresGeneraux
    {
        public String NomSociete { get; set; }
        public String MethodeCoefficient { get; set; }

        public Decimal TauxFGPapierOuMethodeB { get; set; }
        public Decimal TauxFGMainOeuvre { get; set; }
        public Decimal TauxFGMachine { get; set; }
        public Decimal TauxFGSousTraitance { get; set; }
        public Decimal TauxFGMatiere { get; set; }

        public Decimal TauxPVMini { get; set; }
        public Decimal TauxPVConseille { get; set; }

        public Decimal MajorationCR60 { get; set; }
        public Decimal MajorationCR90 { get; set; }
        public Decimal MajorationCR120 { get; set; }

        public Decimal TauxManutention { get; set; }

        public Decimal MontantFraisDossier { get; set; }

        public Decimal TauxFraisStockageParMois { get; set; }
        public Decimal TauxFraisFinanciersParMois { get; set; }

        public decimal[] ratiosPV100 = new decimal[10];
        public decimal[] tauxCommission = new decimal[10];


    }

    public class TypeProduit
    {
        public String Id { get; set; }
        public String categorie { get; set; }
        public String Designation { get; set; }

        public String PresentationElement { get; set; }
        public String MethodeImposition { get; set; }

        public bool CalculGacheAutomatique { get; set; }
        public bool GacheMillePlus { get; set; }
        public bool FraisFixesMultiplies { get; set; }
        public bool SaisieEpaisseur { get; set; }

        public bool AfficheSensEnroulement { get; set; }
        public bool AfficheFormatOuvert { get; set; }
        public bool AfficheCodeHauteur { get; set; }
        public bool Affiche3emeCote { get; set; }

        public bool AfficheNombreCahier { get; set; }
        public bool AfficheNombreCarbone { get; set; }

        public String UniteHauteurParDefaut { get; set; }
        public int PourcentageMacule { get; set; }
        
        public String libelleFormatFini { get; set; }
        public String libelleElements { get; set; }

        public bool AfficheNombrePages { get; set; }
        public int NombrePagesDefaut { get; set; }

        public bool AfficheElementsIdentiques { get; set; }
        public int NombreElementsIdentiquesDefaut { get; set; }
        
        public bool AfficheTypePli { get; set; }
        public String TypePliParDefaut { get; set; }

        public String ProvenanceDefaut { get; set; }

        public String EnlevementAlivrerDefaut { get; set; }
        public String TypePortDefaut { get; set; }
        public int NombreLieuDefaut { get; set; }

        public int NombreFractionsLivraisonDefaut { get; set; }
        public int NombreMoisDefaut { get; set; }

        public bool AfficheGacheVariable { get; set; }

        public String FeuilleOuBobineDefaut { get; set; }  // VDFOUB : "F" ou "B"
    }
    
    public class Rubrique
    {
        public int Id { get; set; }

        public String Nom { get; set; }
        
        public QuestionDefinition Question1 { get; set; }
        public QuestionDefinition Question2 { get; set; }
        public QuestionDefinition Question3 { get; set; }
        public QuestionDefinition Question4 { get; set; }
        public QuestionDefinition Question5 { get; set; }

        public String OperationProcess { get; set; }

        public String Commentaire { get; set; }
        public List<LienRubriqueMachine> LiensMachine { get; set; }

        public Rubrique()
        {
            LiensMachine = new List<LienRubriqueMachine>();
        }
    }

    public class QuestionDefinition
    {
        public String Libelle { get; set; }
        public bool reponseObligatoire { get; set; }
        public decimal ReponseParDefaut { get; set; }
        public decimal BorneMini { get; set; }
        public decimal BorneMaxi { get; set; }
        public String codeRecherche { get; set; }

        public List<ValeurPossible> ValeursPossibles {get; set;}

        public QuestionDefinition()
        {
            ValeursPossibles = new List<ValeurPossible>();
        }

    }

    public class ValeurPossible
    {
        public String Libelle { get; set; }
        public decimal Valeur { get; set; }
    }

    public class LienRubriqueMachine
    {
        public bool LieMachine { get; set; }
        public MachineImpression machine { get; set; }

        public int ReductionCadence { get; set; }
        public string TypeReductionCadence { get; set; }
        public int SequenceGache {get; set;}
        public int GacheFixeEtape { get; set; }
        public decimal GacheVariableEtape { get; set; }
        public String CodeRechercheTableauGacheFixe { get; set; }
        public String CodeRechercheTableauGacheVariable { get; set; }
        public String ReponseMultiplieLaGacheFixe { get; set; }

        public List<ModeleLigneRubrique> LignesDetail { get; set; }

        public LienRubriqueMachine()
        {
            LignesDetail = new List<ModeleLigneRubrique>();
        }
    }

    public class RubriqueImpression
    {
        public MachineImpression machine { get; set; }
        public int CouleursRecto { get; set; }
        public int CouleursVerso { get; set; }
        public int CouleursBasculees { get; set; }
        public bool CasGeneral { get; set; }

        public int GacheFixeInitiale { get; set; }
        public int GacheFixeParGroupe { get; set; }

        public int SequenceGache { get; set; }
        
        public decimal GacheVariableEtape { get; set; }
        public String CodeRechercheTableauGacheFixe { get; set; }
        public String CodeRechercheTableauGacheVariable { get; set; }

        public List<ModeleLigneRubrique> LignesDetail { get; set; }

        public RubriqueImpression()
        {
            LignesDetail = new List<ModeleLigneRubrique>();
        }
    }


    public abstract class  ModeleLigneRubrique
    {
        public RegleCalcul regle { get; set; }
        public decimal Nombre { get; set; }
        public decimal TempsUnitaire { get; set; }
        public decimal Cadence { get; set; }
        public decimal Quantite { get; set; }
        public decimal Diviseur { get; set; }

        public int ReponseNombre { get; set; }
        public int ReponseTemps { get; set; }
        public int ReponseCadence { get; set; }
        public int ReponseQuantite { get; set; }
        public int ReponseDiviseur { get; set; }

        public String Commentaire { get; set; }

        public override String ToString()
        {
            return String.Empty;
        }

    }

    public class ModeleLigneRubriqueOperation : ModeleLigneRubrique
    {
        
        public OperationMachine operationMachine { get; set; }

        public override String ToString()
        {
            return String.Concat("Opé ", this.operationMachine.section.Id, "-", this.operationMachine.Operation.ToString());
        }

    }

    public class ModeleLigneRubriqueMatiere : ModeleLigneRubrique
    {
        
        public Matiere matiere { get; set; }
        public int ReponseMatiere { get; set; }

        public override String ToString()
        {
            if (this.matiere != null) return String.Concat("Mat ", this.matiere.Id);
            else return "Mat";
        }
    }

    public class ModeleLigneRubriqueSousTraitance : ModeleLigneRubrique
    {

        public OperationSousTraitance operationSousTraitance { get; set; }

    }


    public enum TypeBase { Aucune = 0, Exemplaires = 1, MetrageElement = 2, ToursElement = 3, ToursTotal = 4, MetrageEntete = 5, Feuilles = 6, UC = 7 }
    public enum TypeBaseSurfacePoids { Aucune = 0, FormatOuvertUnitaire = 1, FormatTirageUnitaire = 2, FormatPapierUnitaire = 3, FormatOuvertTotal = 4, FormatTirageTotal = 5, FormatPapierTotal = 6 }

    public class RegleCalcul
    {
        public static readonly string[] PrecalculsDependantsDuFormat = { "HAT", "HT0", "HT1", "LT0", "LT1", "PPC", "PGC", "CAR" };

        public static readonly string[] ReglesDeRoulage = { "RM", "RO", "RP" };
        
        public String Id { get; set; }
        public String Nom { get; set; }
        public TypeBase typeBase { get; set; }
        public String TypeLigne { get; set; }
        public bool ProportionnelElementsIdentiques { get; set; }
        public TypeBaseSurfacePoids typeBaseSurface { get; set; }
        public TypeBaseSurfacePoids typeBasePoids { get; set; }

        //TODO revoir la structure des bases surfaces et poids unitaire et total

        public bool UtilisationNombre { get; set; }
        public bool UtilisationTemps { get; set; }
        public bool UtilisationQuantite { get; set; }
        public bool UtilisationCadence { get; set; }
        public bool UtilisationDiviseur { get; set; }

        public bool UtilisationTableau { get; set; }

        public String PrecalculNombre { get; set; }
        public String PrecalculTemps { get; set; }
        public String PrecalculQuantite { get; set; }
        public String PrecalculCadence { get; set; }
        public String PrecalculDiviseur { get; set; }

        public override String ToString()
        {
            return Id;
        }

        public bool DependantQuantite
        {
            get
            {
                if ((typeBase == TypeBase.Exemplaires) || (typeBase == TypeBase.Feuilles) || (typeBase == TypeBase.MetrageElement) || (typeBase == TypeBase.ToursElement)) return true;
                if ((typeBasePoids == TypeBaseSurfacePoids.FormatOuvertTotal) || (typeBasePoids == TypeBaseSurfacePoids.FormatTirageTotal) || (typeBasePoids == TypeBaseSurfacePoids.FormatPapierTotal)) return true;
                if ((typeBaseSurface == TypeBaseSurfacePoids.FormatOuvertTotal) || (typeBaseSurface == TypeBaseSurfacePoids.FormatTirageTotal) || (typeBaseSurface == TypeBaseSurfacePoids.FormatPapierTotal)) return true;
                return false;

            }
        }

        
        public bool DependantFormat
        {
            get
            {
                if (typeBase == TypeBase.MetrageElement) return true;
                if (typeBasePoids != TypeBaseSurfacePoids.Aucune) return true;
                if (typeBaseSurface != TypeBaseSurfacePoids.Aucune) return true;

                if (Array.IndexOf(PrecalculsDependantsDuFormat, PrecalculNombre) >= 0) return true;
                if (Array.IndexOf(PrecalculsDependantsDuFormat, PrecalculQuantite) >= 0) return true;
                if (Array.IndexOf(PrecalculsDependantsDuFormat, PrecalculDiviseur) >= 0) return true;

                return false;

            }
        }
    }

    public class TableauCalcul
    {
        public RegleCalcul regle { get; set; }
        public String reference { get; set; }

        public String critereX { get; set; }
        public String critereY { get; set; }
        public String modeRechercheX { get; set; }
        public String modeRechercheY { get; set; }

        public String TypeResultat { get; set; }
        public bool Special {get; set;}
        public bool Proportionnel { get; set; }

        public decimal[] abcisses=new decimal[10];
        public decimal[] ordonnees=new decimal[20];
        public decimal[] resultats=new decimal[200];

        public decimal GetResult(decimal _abs, decimal _ord)
        {
            int rangAbs=-1;
            int rangOrd=-1;

            for (int i = 0; i < 10; i++)
            {
                if (this.modeRechercheX == "=")
                {
                    if (_abs== this.abcisses[i] )
                    {
                        rangAbs = i;
                        break;
                    }
                }
                if (this.modeRechercheX == ">")
                {
                    if (this.abcisses[i]>=_abs )
                    {
                        rangAbs = i;
                        break;
                    }
                }
                if (this.modeRechercheX == "<")
                {
                    if (_abs < this.abcisses[i])
                    {
                        rangAbs = i;
                        break;
                    }
                }

            }
            // abscisse non trouvée
            if (rangAbs == -1) return -1.0M;

            for (int i = 0; i < 20; i++)
            {
                if (this.modeRechercheY == "=")
                {
                    if (_ord == this.ordonnees[i])
                    {
                        rangOrd = i;
                        break;
                    }
                }
                if (this.modeRechercheY == ">")
                {
                    if (this.ordonnees[i]>=_ord)
                    {
                        rangOrd = i;
                        break;
                    }
                }
                if (this.modeRechercheY == "<")
                {
                    if (_ord < this.ordonnees[i])
                    {
                        rangOrd = i;
                        break;
                    }
                }

                
            }

            // ordonnée non trouvée
            if (rangOrd == -1) return -1.0M;

            return this.resultats[rangOrd * 10 + rangAbs];
            
        }

    }

    //TODO Gérer la sous-catégorie
    public class TableauRubrique
    {
        public String typeProduit { get; set; }
        public String typeTableau { get; set; }
        public String catégorie { get; set; }
        public String machine { get; set; }

        public int[] rubriques = new int[68];
        public String[] libelles = new String[68];
        public bool[] dft=new bool[68];

    }

    public static class ModeleFactory
    {
        public static ModeleLigneRubrique CreateModele(String _type)
        {

            switch (_type)
            {
                case "O":
                    return new ModeleLigneRubriqueOperation();
                case "M":
                    return new ModeleLigneRubriqueMatiere();
                case "S":
                    return new ModeleLigneRubriqueSousTraitance();
                default:
                    return null;
            }
        }

    }

}
