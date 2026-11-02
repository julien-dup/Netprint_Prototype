// Porté de Devis2017 (Net.Axilog.Model/Devis.cs) le 02/11/2026 — moteur de devis, lot S1 (docs/compte-rendu-saisie-devis.md).
// Code repris tel quel, à l'espace de noms près ; les corrections métier sont signalées « CORRECTION ».
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel;


namespace NetPrint.Modules.Ventes.Moteur.Modele
{
    public enum TypeAdresseDevis
    {Facturation=1,Livraison=2, Manuelle=3}

    public class DevisListItem
    {
        public int Numero { get; set; }
        public int Version { get; set; }
        public int Hypothese { get; set; }
        public String ClientNom { get; set; }
        public String Designation { get; set; }
        public DateTime DateCreation { get; set; }
        public Decimal MontantPR { get; set; }
        public int Quantite { get; set; }
        public String FormatFini { get; set; }
        public String FormatOuvert { get; set; }
        public String Representant { get; set; }
        public String Fabricant { get; set; }
        public String Profil { get; set; }
        public String Categorie { get; set; }
        public String SousCategorie { get; set; }
        public String RefImprime { get; set; }
        public bool PasseEnCommande { get; set; }
    }

    public class ligneRecap
    {
        public String CodeTri { get; set; }
        public Decimal TotalValeur { get; set; }
        public Decimal TotalValeurMP { get; set; }
        public Decimal TotalHeures { get; set; }
        public Decimal TotalFraisFixes { get; set; }
        public Decimal TotalPoids { get; set; }
    }
 
    public class Devis
    {
        public int Id { get; set; }
        public int Version { get; set; }
        public int Hypothese { get; set; }
        public String Designation { get; set; }
        public String Designation2 { get; set; }
        public DateTime DateCreation { get; set; }
        public DateTime DateDemande { get; set; }
        public int CollectifClient { get; set; }
        public int ClientId { get; set; }
        public int ProspectId { get; set; }
        public Tiers tiers { get; set; }
        public Contact contact { get; set; }
        public Representant representant { get; set; }
        public String fabricant { get; set; }
        public CategorieProduit Categorie { get; set; }
        public SousCategorieProduit souscategorie { get; set; }
        public TypeProduit typeProduit { get; set; }
        public int Quantite { get; set; }
        public FormatFini formatFini { get; set; }
        public FormatBase formatOuvert { get; set; }
        public int GacheVariable { get; set; }
        public String sensEnroulement { get; set; }
        public int EmballagePar { get; set; }
        public String refImprime { get; set; }

        // Propriétés manquantes D5000F
        public String TypeDevis { get; set; }  // RCOUT - toujours 'C'
        public String InitialeAgentTech { get; set; }  // DEINAT
        public String CodeHauteur { get; set; }  // DECODH
        public int NombreCahiers { get; set; }  // DENCAH
        public int NombreCarbones { get; set; }  // DENCAR
        public int NbCahierAssembler { get; set; }  // DENRCA
        public int LiasseParCarnet { get; set; }  // DELIPC
        public decimal NbPli { get; set; }  // DEPLI
        public String TypePli { get; set; }  // DEPPLI
        public String Profil { get; set; }  // DEPROF
        public int CarollGauche { get; set; }  // DECAGL
        public int CarollDroite { get; set; }  // DECADL
        public String FraisDossier { get; set; }  // DEFRDO
        public String OffreAEditer { get; set; }  // DEOFFR
        public String QteSupOuMille { get; set; }  // DEQTBM
        public decimal TauxCommRep { get; set; }  // DETXRP
        public String NomHonoraire { get; set; }  // DENMHO
        public int DossierPrecedent { get; set; }  // DEDOSP
        public int DelaiDemandeSemaines { get; set; }  // DEDSDS
        public int DelaiDemandeJours { get; set; }  // DEDSDA
        public int DelaiConfirmeSemaines { get; set; }  // DEDSCS
        public int DelaiConfirmeJours { get; set; }  // DEDSCA
        public DateTime DateProposition { get; set; }  // DEDTPA+M+J
        public String CheminFaconnage { get; set; }  // DECHEF
        public String CheminPrepresse { get; set; }  // DECHEP
        public String ObservationPrix { get; set; }  // DEOBSP
        public String ContactNom { get; set; }  // DECONT
        public String Option1 { get; set; }  // DEOPT1
        public String Option2 { get; set; }  // DEOPT2
        public String Option3 { get; set; }  // DEOPT3
        public String Option4 { get; set; }  // DEOPT4
        public String Option5 { get; set; }  // DEOPT5
        public String Option6 { get; set; }  // DEOPT6
        public String Option7 { get; set; }  // DEOPT7
        public String Option8 { get; set; }  // DEOPT8
        public String Option9 { get; set; }  // DEOPT9
        public String Option10 { get; set; }  // DEOP10
        public String Option11 { get; set; }  // DEOP11
        public String Option12 { get; set; }  // DEOP12
        public String Option13 { get; set; }  // DEOP13
        public String Option14 { get; set; }  // DEOP14
        public String Option15 { get; set; }  // DEOP15
        public String Option16 { get; set; }  // DEOP16
        public String Option17 { get; set; }  // DEOP17
        public String Option18 { get; set; }  // DEOP18
        public String Option19 { get; set; }  // DEOP19

        public Norme norme { get; set; }

        public Boolean productionStockee { get; set; }
        public Boolean quantiteJuste { get; set; }

        public String enseigne { get; set; }

        public String texteTechnique { get; set; }
        public TexteTechnique texteTechniqueDetail { get; set; }

        public List<LigneTexteLibre> TexteLibre { get; set; }

        public String enlevementAlivrer { get; set; }
        public String typePort { get; set; }
        public int nombreLieuxLivraison { get; set; }


        public int FractionLivraisons { get; set; }
        public int EspacementMois { get; set; }
        public String ModeFacturation { get; set; }
        public String DestinationFacturation { get; set; }  // DEFAC
        public Transporteur transporteur { get; set; }

        public int nombreElements { get; set; }

        public int etude { get; set; }

        public decimal TotalValeurSupport { get; set; }
        public decimal TotalPoidsSupport { get; set; }

        public decimal TotalValeurMatiere { get; set; }
        public decimal TotalValeurSousTraitance { get; set; }
        public decimal TotalValeurMachine { get; set; }
        public decimal TotalValeurMainOeuvre { get; set; }
        public decimal TotalValeurTransport { get; set; }

        public decimal MontantFraisManutention { get; set; }
        public decimal MontantFraisDossier { get; set; }

        public Decimal TauxFraisStockage { get; set; }
        public Decimal TauxFraisFinanciers { get; set; }
        public decimal MontantFraisStockage { get; set; }
        public decimal MontantFraisFinanciers { get; set; }

        public decimal MontantFraisRistourne { get; set; }
        public decimal MontantFraisEscompte { get; set; }
        public decimal TauxFraisCR { get; set; }
        public decimal MontantFraisCR { get; set; }

        public decimal MontantFraisGeneraux { get; set; }
        public decimal MontantFraisCommerciaux { get; set; }
        public decimal MontantHonoraires { get; set; }
        public decimal TauxHonoraires { get; set; }

        public decimal TauxFraisGeneraux { get; set; }  // DETFG
        public decimal TauxManutention { get; set; }    // DRTFMA

        public decimal MontantBase100
        {
            get
            {
                return Decimal.Round(TotalValeurMachine + TotalValeurMainOeuvre + TotalValeurMatiere + TotalValeurSousTraitance 
                    + TotalValeurSupport + TotalValeurTransport + MontantHonoraires + MontantFraisManutention, 2);
            }
        }

        public decimal MontantMajorationsMethodeB
        {
            get
            {
                return MontantFraisManutention + MontantFraisEscompte + MontantFraisRistourne + MontantFraisCR + MontantFraisFinanciers + MontantFraisStockage;
            }
        }

        public decimal MontantTotalPRMethodeB
        {
            get
            {
                return MontantBase100 + MontantMajorationsMethodeB + MontantFraisGeneraux + MontantFraisCommerciaux;
            }
        }


        public decimal PVMini { get; set; }
        public decimal PVConseille { get; set; }
        public decimal PVRemisEnUV { get; set; }
        public decimal PVRemisQuantite
        {
            get
            {
                if (uniteVente ==null) return 0.0M;
                if (PVRemisEnUV == 0) return 0.0M;
                if (uniteVente.Autout) return PVRemisEnUV;
                if (uniteVente.DiviseurQuantite > 0) return PVRemisEnUV*Quantite / uniteVente.DiviseurQuantite;

                return PVRemisEnUV;
            }
        }
        public UniteVente uniteVente { get; set; }




        public bool IsNew { get; set; }

        public List<ElementDevis> Elements { get; set; }

        public ElementDevis Prepresse { get; set; }
        public ElementDevis Faconnage { get; set; }

        public Devis()
        {
            this.Elements = new List<ElementDevis>();
            this.TexteLibre = new List<LigneTexteLibre>();
        }

        public ElementDevis[] GetElementsAsArray()
        {
            return Elements.ToArray();
        }

        public List<ligneRecap> GetTableauRecap()
        {
            List<LigneFabrication> detail = new List<LigneFabrication>();
            //TotalValeurSupport = 0.0M;
            //TotalPoidsSupport = 0.0M;

            foreach (var elm in this.Elements)
            {

                foreach (var etape in elm.EtapesProcess)
                {
                    foreach (var rubc in etape.RubriquesChoisies)
                    {
                        detail.AddRange(rubc.LignesFabrication.Where(e => e.Resultat > 0));
                    }
                }
                detail.AddRange(elm.LignesImpression.Where(e => e.Resultat > 0));

            }

            foreach (var etape in Prepresse.EtapesProcess)
            {
                foreach (var rubc in etape.RubriquesChoisies)
                {
                    detail.AddRange(rubc.LignesFabrication.Where(e => e.Resultat > 0));
                }
            }

            foreach (var etape in Faconnage.EtapesProcess)
            {
                foreach (var rubc in etape.RubriquesChoisies)
                {
                    detail.AddRange(rubc.LignesFabrication.Where(e => e.Resultat > 0));
                }
            }

            var recap = detail.GroupBy(l => l.CodeTri)
                          .Select(lg =>
                                new ligneRecap
                                {
                                    CodeTri = lg.Key,
                                    TotalValeur = lg.Sum(w => w.Valeur),
                                    TotalValeurMP = lg.Sum(w => w.ValeurMP),
                                    TotalHeures=lg.Where(j=>j is LigneFabricationOperation).Sum(w=>w.Resultat),
                                    TotalFraisFixes = lg.Where(j => j.ResultatMP==0).Sum(w => w.Valeur),
                                    TotalPoids=0.0M
                                }).OrderBy(e => e.CodeTri);
            var result=recap.ToList();
            result.Insert(0, new ligneRecap  { CodeTri = "Supports",
                TotalValeur = Math.Round(TotalValeurSupport, 2), TotalValeurMP = 0.0M, TotalHeures = 0.0M, TotalFraisFixes = 0.0M, TotalPoids = Math.Round(TotalPoidsSupport, 2) });
            result.Add(new ligneRecap { CodeTri = "BASE 100", TotalValeur = this.MontantBase100, TotalValeurMP = 0.0M, TotalHeures = 0.0M, TotalFraisFixes = 0.0M, TotalPoids = 0.0M });
            result.Add(new ligneRecap { CodeTri = "Majorations", TotalValeur = this.MontantMajorationsMethodeB, TotalValeurMP = 0.0M, TotalHeures = 0.0M, TotalFraisFixes = 0.0M, TotalPoids = 0.0M });
            result.Add(new ligneRecap { CodeTri = "Frais Généraux", TotalValeur = this.MontantFraisGeneraux, TotalValeurMP = 0.0M, TotalHeures = 0.0M, TotalFraisFixes = 0.0M, TotalPoids = 0.0M });
            result.Add(new ligneRecap { CodeTri = "Frais Commerciaux", TotalValeur = this.MontantFraisCommerciaux, TotalValeurMP = 0.0M, TotalHeures = 0.0M, TotalFraisFixes = 0.0M, TotalPoids = 0.0M });
            result.Add(new ligneRecap { CodeTri = "PR Total", TotalValeur = this.MontantTotalPRMethodeB, TotalValeurMP = 0.0M, TotalHeures = 0.0M, TotalFraisFixes = 0.0M, TotalPoids = 0.0M });
            result.Add(new ligneRecap { CodeTri = "Prix Mini", TotalValeur = Math.Round(this.PVMini,2), TotalValeurMP = 0.0M, TotalHeures = 0.0M, TotalFraisFixes = 0.0M, TotalPoids = 0.0M });
            result.Add(new ligneRecap { CodeTri = "Prix Conseillé", TotalValeur = Math.Round(this.PVConseille, 2), TotalValeurMP = 0.0M, TotalHeures = 0.0M, TotalFraisFixes = 0.0M, TotalPoids = 0.0M });
            return result; ;
        }

        public static Devis Clone(Devis _dev)
        {
            Devis cpy = new Devis();
            cpy.Id = _dev.Id;
            cpy.Version = _dev.Version;
            cpy.Hypothese = _dev.Hypothese;
            cpy.Designation = _dev.Designation;
            cpy.Designation2 = _dev.Designation2;
            cpy.DateCreation = _dev.DateCreation;
            cpy.DateDemande = _dev.DateDemande;
            cpy.CollectifClient = _dev.CollectifClient;
            cpy.ClientId = _dev.ClientId;
            cpy.ProspectId = _dev.ProspectId;
            cpy.tiers = _dev.tiers;
            cpy.contact = _dev.contact;
            cpy.representant = _dev.representant;
            cpy.fabricant = _dev.fabricant;
            cpy.Categorie = _dev.Categorie;
            cpy.souscategorie = _dev.souscategorie;
            cpy.typeProduit = _dev.typeProduit;
            cpy.Quantite = _dev.Quantite;
            cpy.GacheVariable = _dev.GacheVariable;
            cpy.formatFini = new FormatFini(_dev.formatFini.Largeur, _dev.formatFini.Hauteur, _dev.formatFini.Cote3);
            cpy.formatOuvert = _dev.formatOuvert != null ? new FormatBase(_dev.formatOuvert.Largeur, _dev.formatOuvert.Hauteur) : null;
            cpy.sensEnroulement = _dev.sensEnroulement;
            cpy.EmballagePar = _dev.EmballagePar;
            cpy.refImprime = _dev.refImprime;
            cpy.norme = _dev.norme;
            cpy.productionStockee = _dev.productionStockee;
            cpy.quantiteJuste = _dev.quantiteJuste;
            cpy.enseigne = _dev.enseigne;
            cpy.texteTechnique = _dev.texteTechnique;
            cpy.texteTechniqueDetail = _dev.texteTechniqueDetail;
            cpy.enlevementAlivrer = _dev.enlevementAlivrer;
            cpy.typePort = _dev.typePort;
            cpy.nombreLieuxLivraison = _dev.nombreLieuxLivraison;
            cpy.FractionLivraisons = _dev.FractionLivraisons;
            cpy.EspacementMois = _dev.EspacementMois;
            cpy.ModeFacturation = _dev.ModeFacturation;
            cpy.DestinationFacturation = _dev.DestinationFacturation;
            cpy.transporteur = _dev.transporteur;
            cpy.nombreElements = _dev.nombreElements;
            cpy.etude = _dev.etude;

            // Propriétés D5000F ajoutées
            cpy.TypeDevis = _dev.TypeDevis;
            cpy.InitialeAgentTech = _dev.InitialeAgentTech;
            cpy.CodeHauteur = _dev.CodeHauteur;
            cpy.NombreCahiers = _dev.NombreCahiers;
            cpy.NombreCarbones = _dev.NombreCarbones;
            cpy.NbCahierAssembler = _dev.NbCahierAssembler;
            cpy.LiasseParCarnet = _dev.LiasseParCarnet;
            cpy.NbPli = _dev.NbPli;
            cpy.TypePli = _dev.TypePli;
            cpy.CarollGauche = _dev.CarollGauche;
            cpy.CarollDroite = _dev.CarollDroite;
            cpy.FraisDossier = _dev.FraisDossier;
            cpy.OffreAEditer = _dev.OffreAEditer;
            cpy.QteSupOuMille = _dev.QteSupOuMille;
            cpy.TauxCommRep = _dev.TauxCommRep;
            cpy.NomHonoraire = _dev.NomHonoraire;
            cpy.DossierPrecedent = _dev.DossierPrecedent;
            cpy.DelaiDemandeSemaines = _dev.DelaiDemandeSemaines;
            cpy.DelaiDemandeJours = _dev.DelaiDemandeJours;
            cpy.DelaiConfirmeSemaines = _dev.DelaiConfirmeSemaines;
            cpy.DelaiConfirmeJours = _dev.DelaiConfirmeJours;
            cpy.DateProposition = _dev.DateProposition;
            cpy.CheminFaconnage = _dev.CheminFaconnage;
            cpy.CheminPrepresse = _dev.CheminPrepresse;
            cpy.ObservationPrix = _dev.ObservationPrix;
            cpy.ContactNom = _dev.ContactNom;

            // Options
            cpy.Option1 = _dev.Option1; cpy.Option2 = _dev.Option2; cpy.Option3 = _dev.Option3;
            cpy.Option4 = _dev.Option4; cpy.Option5 = _dev.Option5; cpy.Option6 = _dev.Option6;
            cpy.Option7 = _dev.Option7; cpy.Option8 = _dev.Option8; cpy.Option9 = _dev.Option9;
            cpy.Option10 = _dev.Option10; cpy.Option11 = _dev.Option11; cpy.Option12 = _dev.Option12;
            cpy.Option13 = _dev.Option13; cpy.Option14 = _dev.Option14; cpy.Option15 = _dev.Option15;
            cpy.Option16 = _dev.Option16; cpy.Option17 = _dev.Option17; cpy.Option18 = _dev.Option18;
            cpy.Option19 = _dev.Option19;

            // Valorisation
            cpy.TotalValeurSupport = _dev.TotalValeurSupport;
            cpy.TotalPoidsSupport = _dev.TotalPoidsSupport;
            cpy.TotalValeurMatiere = _dev.TotalValeurMatiere;
            cpy.TotalValeurSousTraitance = _dev.TotalValeurSousTraitance;
            cpy.TotalValeurMachine = _dev.TotalValeurMachine;
            cpy.TotalValeurMainOeuvre = _dev.TotalValeurMainOeuvre;
            cpy.TotalValeurTransport = _dev.TotalValeurTransport;
            cpy.MontantFraisManutention = _dev.MontantFraisManutention;
            cpy.MontantFraisDossier = _dev.MontantFraisDossier;
            cpy.TauxFraisStockage = _dev.TauxFraisStockage;
            cpy.TauxFraisFinanciers = _dev.TauxFraisFinanciers;
            cpy.MontantFraisStockage = _dev.MontantFraisStockage;
            cpy.MontantFraisFinanciers = _dev.MontantFraisFinanciers;
            cpy.MontantFraisRistourne = _dev.MontantFraisRistourne;
            cpy.MontantFraisEscompte = _dev.MontantFraisEscompte;
            cpy.TauxFraisCR = _dev.TauxFraisCR;
            cpy.MontantFraisCR = _dev.MontantFraisCR;
            cpy.MontantFraisGeneraux = _dev.MontantFraisGeneraux;
            cpy.MontantFraisCommerciaux = _dev.MontantFraisCommerciaux;
            cpy.MontantHonoraires = _dev.MontantHonoraires;
            cpy.TauxHonoraires = _dev.TauxHonoraires;
            cpy.TauxFraisGeneraux = _dev.TauxFraisGeneraux;
            cpy.TauxManutention = _dev.TauxManutention;
            cpy.PVMini = _dev.PVMini;
            cpy.PVConseille = _dev.PVConseille;
            cpy.PVRemisEnUV = _dev.PVRemisEnUV;
            cpy.uniteVente = _dev.uniteVente;

            // Deep-clone éléments (passer cpy pour que les éléments référencent le clone, pas la source)
            cpy.Prepresse = ElementDevis.Clone(_dev.Prepresse, cpy);
            cpy.Faconnage = ElementDevis.Clone(_dev.Faconnage, cpy);

            foreach (ElementDevis elt in _dev.Elements)
            {
                cpy.Elements.Add(ElementDevis.Clone(elt, cpy));
            }

            return cpy;
        }

    }

    public class TexteTechnique
    {
        public String Ligne1 { get; set; }  // DETXT1
        public String Ligne2 { get; set; }  // DETXT2
        public String Ligne3 { get; set; }  // DETXT3
        public String Ligne4 { get; set; }  // DETXT4
        public String Ligne5 { get; set; }  // DETXT5
        public String Ligne6 { get; set; }  // DETXT6
        public String TexteLong { get; set; }  // DETXT

        public TexteTechnique()
        {
            Ligne1 = String.Empty;
            Ligne2 = String.Empty;
            Ligne3 = String.Empty;
            Ligne4 = String.Empty;
            Ligne5 = String.Empty;
            Ligne6 = String.Empty;
            TexteLong = String.Empty;
        }

        public override string ToString()
        {
            return String.Concat(Ligne1, "\n", Ligne2, "\n", Ligne3, "\n", Ligne4, "\n", Ligne5, "\n", Ligne6).Trim();
        }
    }

    public class LigneLivraison
    {
        public Devis devis { get; private set; }
        public TypeAdresseDevis TypeAdresse { get; set; }

        public String RaisonSociale { get; set; }

        public Adresse adresse { get; set; }

        public int QuantiteALivrer { get; set; }

        public Transporteur transporteur { get; set; }

        public decimal montantTransport { get; set; }

        public decimal getPoidsALivrer()
        {
            return 0.0M; //TODO regle de trois (poids total/quantité devisée)*QuantiteAlivrer 
        }


    }


    




    public static class LigneFabricationFactory
    {
        public static LigneFabrication CreateLigneFabrication(ElementDevis _elem, String _type)
        {

            switch (_type)
            {
                case "O":
                    return new LigneFabricationOperation(_elem);
                case "M":
                    return new LigneFabricationMatiere(_elem);
                case "S":
                    return new LigneFabricationSousTraitance(_elem);
                default:
                    return null;
            }

            
        }

        public static LigneFabrication CreateLigneFabrication(ElementDevis _elem, ModeleLigneRubrique _modele)
        {
            
            
            if (_modele is ModeleLigneRubriqueOperation)
            {
                ModeleLigneRubriqueOperation modeleO = (ModeleLigneRubriqueOperation)_modele;
                LigneFabricationOperation ligne = new LigneFabricationOperation(_elem);
                ligne.operationMachine = modeleO.operationMachine;

                ligne.modele = _modele;
                ligne.regle = _modele.regle;

                ligne.Sequence = ligne.operationMachine.Sequence;

                AffecteBoites(ligne);

                return ligne;
            
                
            }

            if (_modele is ModeleLigneRubriqueMatiere)
            {
                ModeleLigneRubriqueMatiere modeleM = (ModeleLigneRubriqueMatiere)_modele;
                LigneFabricationMatiere ligne = new LigneFabricationMatiere(_elem);
                ligne.matiere = modeleM.matiere;

                ligne.ReponseMatiere = modeleM.ReponseMatiere;

                if (ligne.matiere != null)
                {
                    ligne.PrixAchat = ligne.matiere.PrixAchat;
                    ligne.Coefficient = ligne.matiere.Coefficient;
                }

                ligne.modele = _modele;
                ligne.regle = _modele.regle;

                AffecteBoites(ligne);

                return ligne;
            
                
            }

            if (_modele is ModeleLigneRubriqueSousTraitance)
            {
                ModeleLigneRubriqueSousTraitance modeleS = (ModeleLigneRubriqueSousTraitance)_modele;
                LigneFabricationSousTraitance ligne = new LigneFabricationSousTraitance(_elem);
                ligne.operationSousTraitance = modeleS.operationSousTraitance;

                ligne.modele = _modele;
                ligne.regle = _modele.regle;

                AffecteBoites(ligne);

                return ligne;
            
            }


            return null;            
            
        }

        private static void AffecteBoites(LigneFabrication ligne)
        {
          if (ligne != null)
            {

                // Alimentation du Nombre avec la réponse à la question
                if (ligne.modele.regle.UtilisationNombre)
                {
                    ligne.Nombre = ligne.modele.Nombre;
                    ligne.ReponseNombre = ligne.modele.ReponseNombre;

                }
                // Alimentation du Temps unitaire
                if (ligne.modele.regle.UtilisationTemps)
                {
                    ligne.TempsUnitaire = ligne.modele.TempsUnitaire;
                    ligne.ReponseTemps = ligne.modele.ReponseTemps;
                }
                // Alimentation de la cadence
                if (ligne.modele.regle.UtilisationCadence)
                {
                    ligne.Cadence = ligne.modele.Cadence;
                    ligne.ReponseCadence = ligne.modele.ReponseCadence;
                }
                // Alimentation de la quantite
                if (ligne.modele.regle.UtilisationQuantite)
                {
                    ligne.Quantite = ligne.modele.Quantite;
                    ligne.ReponseQuantite = ligne.modele.ReponseQuantite;
                }
                // Alimentation du diviseur
                if (ligne.modele.regle.UtilisationDiviseur)
                {
                    ligne.Diviseur = ligne.modele.Diviseur;
                    ligne.ReponseDiviseur = ligne.modele.ReponseDiviseur;
                }

                if ((ligne.regle.DependantQuantite) || (ligne.regle.DependantFormat)) ligne.Element.PropertyChanged += ligne.ElementDevis_PropertyChanged;
            }
    }

    }

}

