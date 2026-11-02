// Porté de Devis2017 (Net.Axilog.Model/ElementDevis.cs) le 02/11/2026 — moteur de devis, lot S1 (docs/compte-rendu-saisie-devis.md).
// Code repris tel quel, à l'espace de noms près ; les corrections métier sont signalées « CORRECTION ».
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Globalization;

namespace NetPrint.Modules.Ventes.Moteur.Modele
{
    public class ElementDevis : INotifyPropertyChanged
    {
        public Devis devis { get; private set; }
        public int Id { get; set; }
        public String Designation { get; set; }
        public String typeElement { get; set; }
        public SupportDevis support { get; set; }
        private MachineImpression miMachineImpression;
        public MachineImpression machineImpression
        {
            get { return miMachineImpression; }
            set
            {
                if (value != miMachineImpression)
                {
                    // Call OnPropertyChanged whenever the property is updated
                    miMachineImpression = value;
                    this.ImpressionAConstituer = true;
                    this.CadenceARecalculer = true;
                    foreach (var etape in this.EtapesProcess)
                    {
                        foreach (RubriqueChoisie rubc in etape.RubriquesChoisies.Where(rubc => rubc.LieMachine))
                        {
                            rubc.DetailAConstituer = true;
                        }
                    }
                    OnPropertyChanged("machineImpression");
                }
            }
        }
        private FormatFini ffFormatFini;
        public FormatFini formatFini
        {
            get { return ffFormatFini; }
            set
            {
                if (value != ffFormatFini)
                {
                    // Call OnPropertyChanged whenever the property is updated
                    ffFormatFini = value;
                    OnPropertyChanged("FormatFini");
                }
            }
        }
        public FormatBase formatOuvert { get; set; }
        public FormatBase formatDeveloppe { get; set; }
        public FormatBase formatTirage { get; set; }
        private int iCouleursRecto;
        public int CouleursRecto
        {
            get { return iCouleursRecto; }
            set
            {
                if (value != iCouleursRecto)
                {
                    iCouleursRecto = value;
                    this.ImpressionAConstituer = true;
                    OnPropertyChanged("CouleursRecto");
                }
            }
        }
        private int iCouleursVerso;
        public int CouleursVerso
        {
            get { return iCouleursVerso; }
            set
            {
                if (value != iCouleursVerso)
                {
                    iCouleursVerso = value;
                    this.ImpressionAConstituer = true;
                    OnPropertyChanged("CouleursVerso");
                }
            }
        }
        private int iCouleursBascule;
        public int CouleursBascule
        {
            get { return iCouleursBascule; }
            set
            {
                if (value != iCouleursBascule)
                {
                    iCouleursBascule = value;
                    this.ImpressionAConstituer = true;
                    OnPropertyChanged("CouleursBascule");
                }
            }
        }
        public bool RectoVersoIdentique { get; set; }
        public int Poses { get; set; }
        public int Pages { get; set; }
        public int ElementsIdentiques { get; set; }
        private int iQuantite;
        public int Quantite {
            get { return iQuantite; }
            set
            {
                if (value != iQuantite)
                {
                    // Call OnPropertyChanged whenever the property is updated
                    iQuantite = value;
                    this.ImpressionAValoriser = true;
                    this.CadenceARecalculer = true;
                    OnPropertyChanged("Quantite");
                }
            }
        }
        public bool SansPapier { get; set; }
        private bool bSansImpression;
        public bool SansImpression
        {
            get { return bSansImpression; }
            set
            {
                if (value != bSansImpression)
                {
                    // Call OnPropertyChanged whenever the property is updated
                    bSansImpression = value;
                    OnPropertyChanged("SansImpression");

                    if (bSansImpression)
                    {
                        machineImpression = null;
                        
                    }
                }
            }
        }
        public decimal GacheVariable { get; set; }
        public int Refente { get; set; }

        public int codeOutil { get; set; }
        public int codeOutil1 { get; set; }
        public int codeOutil2 { get; set; }

        // Propriétés manquantes D60001
        public String FeuilleOuBobine { get; set; }  // DDFOUB
        public String BlancCoulCarbone { get; set; }  // DDBCK
        public String Coloris { get; set; }  // DDCOUL
        public String Provenance { get; set; }  // DDPROV
        public int CodePapier { get; set; }  // DDPAPI
        public FormatBase FormatPapier { get; set; }  // DDLRFP+DDHAFP
        public String TypePli { get; set; }  // DDPLI
        public String Depassant { get; set; }  // DDDOUG
        public String FondPerdu { get; set; }  // DDFOND
        public String SensPoses { get; set; }  // DDLOUH
        public int NbPoseLargeur { get; set; }  // DDPOSL
        public int NbPoseHauteur { get; set; }  // DDPOSH
        public int Fournisseur { get; set; }  // DDFRN
        public String UniteAchat { get; set; }  // DDUA
        public decimal PrixAchat { get; set; }  // DDPA
        public String Commentaire { get; set; }  // DDCOMM
        public int NbFeuilles { get; set; }  // DDNBFT
        public int Epaisseur { get; set; }  // DDEPAIS
        public int Grammage { get; set; }  // DDGRAM
        public String Sorte { get; set; }  // DDSORT
        public String SousSorte { get; set; }  // DDSSOR
        public String Option1 { get; set; }  // DDOPT1
        public String Option2 { get; set; }  // DDOPT2
        public String Option3 { get; set; }  // DDOPT3
        public String Option4 { get; set; }  // DDOPT4
        public String Option5 { get; set; }  // DDOPT5
        public String Option6 { get; set; }  // DDOPT6
        public String Option7 { get; set; }  // DDOPT7
        public String Option8 { get; set; }  // DDOPT8
        public String Option9 { get; set; }  // DDOPT9
        public String Option10 { get; set; }  // DDOP10
        public String Option11 { get; set; }  // DDOP11
        public String Option12 { get; set; }  // DDOP12
        public String Option13 { get; set; }  // DDOP13
        public String Option14 { get; set; }  // DDOP14
        public String Option15 { get; set; }  // DDOP15
        public String Option16 { get; set; }  // DDOP16
        public String Option17 { get; set; }  // DDOP17
        public String Option18 { get; set; }  // DDOP18
        public String Option19 { get; set; }  // DDOP19


        public List<EtapeProcess> EtapesProcess;
        public List<LigneFabrication> LignesImpression;

        public RubriqueImpression rubriqueImpression { get; set; }

        public bool ImpressionAConstituer { get; set; }
        public bool ImpressionAValoriser { get; set; }
        public bool CadenceARecalculer { get; set; }

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

        public int NombreDePassagesImpression
        {
            get
            {
                if (SansImpression) return 0;

                if (CouleursRecto + CouleursVerso == 0) return 1;

                // TODO Traiter les machines de type continu
                if (machineImpression.Type == TypeMachineImpression.plat)
                {
                    if (machineImpression.RectoVersoEnUnPassage)
                    {
                        if (CouleursRecto + CouleursVerso <= machineImpression.NombreDeGroupes) return 1;

                    }
                    else
                    {
                        if (CouleursBascule > 0)
                        {
                            Decimal result = Math.Max(CouleursRecto, CouleursVerso) / machineImpression.NombreDeGroupes;
                            return (int)Math.Ceiling(result);
                        }
                        else
                        {
                            int passR = Convert.ToInt32(Decimal.Ceiling(Decimal.Divide(CouleursRecto , machineImpression.NombreDeGroupes)));
                            int passV = Convert.ToInt32(Decimal.Ceiling(Decimal.Divide(CouleursVerso , machineImpression.NombreDeGroupes)));
                            return passR + passV;
                        }
                    }
                }

                return 1;
            }
        }

        public int ToursGacheFixe { get; set; }
        
        public int GetQuantite()
        {
            if (this.Quantite > 0) return this.Quantite;
            else
                return this.devis.Quantite;
        }

        public FormatFini GetFormatFini()
        {
            if (this.formatFini.Largeur > 0) return this.formatFini;
            else
                return this.devis.formatFini;
        }

        public decimal GetTauxGacheVariable()
        {
            if (this.GacheVariable > 0) return this.GacheVariable;
            else
                return this.devis.GacheVariable;
        }

        public String GetLibelleColoris()
        {
            String lib = this.CouleursRecto + "R°";
            if (this.CouleursVerso > 0) lib = lib + " " + this.CouleursVerso + "V°";
            if (this.CouleursBascule>0) lib = lib + " " + this.CouleursBascule + "BAS";
            return lib;
            
        }

        public int ToursUtiles
        {
            get
            {

                decimal tours = this.GetQuantite() * this.ElementsIdentiques;
                if (this.Poses > 1) tours = Math.Ceiling(tours / this.Poses);
                return Decimal.ToInt32(tours);
            }

        }

        public int GetToursUtilesMP()
        {
            decimal tours = 1000.0M * this.ElementsIdentiques;
            if (this.Poses > 1) tours = Math.Ceiling(tours / this.Poses);
            return Decimal.ToInt32(tours);

        }

        public decimal MetrageUtile
        {
            get
            {

                return ToursUtiles * this.formatTirage.GetHauteurM();
            }
            
        }

        public decimal GetMetrageUtileMP()
        {
            return GetToursUtilesMP() * this.formatTirage.GetHauteurM();
            
        }

        public int ToursGacheVariable
        {
            get
            {
                return Decimal.ToInt32(Math.Ceiling(this.ToursUtiles * this.GetTauxGacheVariable() / 100.0M));
            }

        }

        public int GetToursGacheVariableMP()
        {
            return Decimal.ToInt32(Math.Ceiling(this.GetToursUtilesMP() * this.GetTauxGacheVariable() / 100.0M));

        }

        public decimal MetrageGacheVariable
        {
            get
            {
                return ToursGacheVariable * this.formatTirage.GetHauteurM();
            }
            
        }

        public decimal GetMetrageGacheVariableMP()
        {
            return GetToursUtilesMP() * this.formatTirage.GetHauteurM();
            
        }

        public decimal GetMetrageGacheFixe()
        {
            return ToursGacheFixe * this.formatTirage.GetHauteurM();

        }

        public decimal FeuillesUtiles
        {
            get
            {
                if (this.Refente > 1) return ToursUtiles / this.Refente;
                else return ToursUtiles;
            }
        }

        public decimal GetFeuillesUtilesMP()
        {
            if (this.Refente > 1) return GetToursUtilesMP() / this.Refente;
            else return GetToursUtilesMP();
        }

        public decimal FeuillesGacheVariable
        {
            get
            {

                if (this.Refente > 1) return ToursGacheVariable / this.Refente;
                else return ToursGacheVariable;
            }
        }

        public decimal GetFeuillesGacheVariableMP()
        {
            if (this.Refente > 1) return GetToursGacheVariableMP() / this.Refente;
            else return GetToursGacheVariableMP();
        }

        public decimal GetFeuillesGacheFixe()
        {
            if (this.Refente > 1) return ToursGacheFixe / this.Refente;
            else return ToursGacheFixe;
        }

        public int ToursTotal
        {
            get
            {
                return ToursUtiles + ToursGacheFixe + ToursGacheVariable;
            }
        }
        public decimal FeuillesTotal
        {
            get
            { 
                return FeuillesUtiles + GetFeuillesGacheFixe() + FeuillesGacheVariable; 
            }
        }
        public decimal MetrageTotal
        {
            get
            {
                return MetrageUtile + GetMetrageGacheFixe() + MetrageGacheVariable;
            }
        }


        public decimal PoidsLivre
        {
            get
            {
                if (SansPapier) return 0.0M;
                if (support.type == TypeSupport.Feuille)
                {
                    return Support.ConvertQuantite(support, "UN", "KG", FeuillesUtiles);
                }
                else
                {
                    return Support.ConvertQuantite(support, "ML", "KG", MetrageUtile);
                }
            }
        }

        public decimal PoidsAchete
        {
            get
            {
                if (SansPapier) return 0.0M;
                if (support.Provenance == "C") return 0.0M;

                //TODO Traiter la gache fixe
                //TODO Traiter l'arrondi à l'unité de conditionnement
                //TODO Ajouter le % du type de produit
                if (support.type == TypeSupport.Feuille)
                {
                    switch (support.Provenance)
                    {
                        case "F": return Support.ConvertQuantite(support, "UN", "KG", FeuillesTotal);
                        default: return Support.ConvertQuantite(support, "UN", "KG", FeuillesTotal);
                    }

                }
                else
                {
                    return Support.ConvertQuantite(support, "ML", "KG", MetrageTotal);
                }
            }
        }

        public decimal ValeurSupport
        {
            get
            {
                if (SansPapier) return 0.0M;
                if (support.Provenance == "C") return 0.0M;

                if (support.PrixAchat == 0.0M) return 0.0M;
                if (support.UniteAchat==String.Empty) return 0.0M;

                //TODO Traiter la gache fixe
                //TODO Traiter l'arrondi à l'unité de conditionnement
                //TODO Ajouter le % du type de produit
                if (support.type == TypeSupport.Feuille)
                {
                    switch (support.Provenance)
                    {
                        case "F": return Support.ConvertQuantite(support, "UN", support.UniteAchat, FeuillesTotal)*support.PrixAchat;
                        default: return Support.ConvertQuantite(support, "UN", support.UniteAchat, FeuillesTotal)*support.PrixAchat;
                    }

                }
                else
                {
                    return Support.ConvertQuantite(support, "ML", support.UniteAchat, MetrageTotal)*support.PrixAchat;
                }
            }
        }



        

        public List<LigneTexteLibre> TexteLibre { get; set; }

        public ElementDevis()
        {
            this.EtapesProcess = new List<EtapeProcess>();
            this.LignesImpression = new List<LigneFabrication>();
            this.TexteLibre = new List<LigneTexteLibre>();

            //this.PropertyChanged += ElementDevis_PropertyChanged;
        }

        void ElementDevis_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            //if (e.PropertyName == "Quantite")
            //{
           //     (sender as ElementDevis).ImpressionAValoriser = true;
           // }
            //if (e.PropertyName == "machineImpression")
            //{
            //    (sender as ElementDevis).ImpressionAConstituer = true;
            //    foreach( RubriqueChoisie rubc in (sender as ElementDevis).RubriquesChoisies.Where(rubc=>rubc.LieMachine))
            //    {
            //        rubc.DetailAConstituer = true;
            //    }
            //}
        }

        public ElementDevis(Devis _devis)
            : this()
        {
            this.devis = _devis;

        }

        // Constructeur pour prepresse et façonnage
        public ElementDevis(Devis _devis, int _id, String _nom)
            : this()
        {
            this.devis = _devis;
            this.Designation = _nom;
            this.Id = _id;
            this.formatFini = new FormatFini(0.0M, 0.0M, 0.0M);
            this.formatOuvert = new FormatBase(0.0M, 0.0M);
            this.formatDeveloppe = new FormatBase(0.0M, 0.0M);
            this.formatTirage = new FormatBase(0.0M, 0.0M);
            this.support = new SupportDevis { grammage = 0, Format = new FormatBase(0.0M, 0.0M), coloris=String.Empty, ConditionnePar=0, PrixAchat=0, Provenance=String.Empty, UniteAchat=String.Empty, epaisseur = new Epaisseur { Longueur = 0, Unite = String.Empty } };
            this.support.sousSorte = new SousSorte { code = String.Empty, natureSupport = 0, nom = String.Empty, sorte = new Sorte { code = String.Empty, nom = String.Empty } };
            this.machineImpression=new MachineImpression{Id=""};

        }

        public List<LigneFabrication> GetDetailElement()
        {
            List<LigneFabrication> detail = new List<LigneFabrication>();
            foreach (var etape in this.EtapesProcess)
            {
                foreach (var rubc in etape.RubriquesChoisies)
                {
                    detail.AddRange(rubc.LignesFabrication.Where(e => e.Resultat > 0));
                }
            }
            detail.AddRange(this.LignesImpression.Where(e => e.Resultat > 0));

            //return detail.OrderBy(e=>e.Sequence).ToList();
            //return new BindListWithRemoving<LigneFabrication>(detail.OrderBy(e => e.Sequence).ToList());
            return detail.OrderBy(e => e.Sequence).ToList();
            
            //tblList=(ThreadedBindingList<LigneFabrication>) detail.OrderBy(e => e.Sequence);

            
        }

        

        public static ElementDevis Clone(ElementDevis _elt, Devis _targetDevis = null)
        {
            ElementDevis cpy = new ElementDevis();
            cpy.devis = _targetDevis ?? _elt.devis;
            cpy.Id = _elt.Id;
            cpy.Designation = _elt.Designation;
            cpy.typeElement = _elt.typeElement;
            cpy.support = _elt.support;
            cpy.machineImpression = _elt.machineImpression;
            cpy.formatFini = _elt.formatFini != null ? new FormatFini(_elt.formatFini.Largeur, _elt.formatFini.Hauteur, _elt.formatFini.Cote3) : null;
            cpy.formatOuvert = _elt.formatOuvert != null ? new FormatBase(_elt.formatOuvert.Largeur, _elt.formatOuvert.Hauteur) : null;
            cpy.formatDeveloppe = _elt.formatDeveloppe != null ? new FormatBase(_elt.formatDeveloppe.Largeur, _elt.formatDeveloppe.Hauteur) : null;
            cpy.formatTirage = _elt.formatTirage != null ? new FormatBase(_elt.formatTirage.Largeur, _elt.formatTirage.Hauteur) : null;
            cpy.CouleursRecto = _elt.CouleursRecto;
            cpy.CouleursVerso = _elt.CouleursVerso;
            cpy.CouleursBascule = _elt.CouleursBascule;
            cpy.RectoVersoIdentique = _elt.RectoVersoIdentique;
            cpy.Poses = _elt.Poses;
            cpy.Pages = _elt.Pages;
            cpy.ElementsIdentiques = _elt.ElementsIdentiques;
            cpy.Quantite = _elt.Quantite;
            cpy.SansPapier = _elt.SansPapier;
            cpy.SansImpression = _elt.SansImpression;
            cpy.GacheVariable = _elt.GacheVariable;
            cpy.Refente = _elt.Refente;
            cpy.codeOutil = _elt.codeOutil;
            cpy.codeOutil1 = _elt.codeOutil1;
            cpy.codeOutil2 = _elt.codeOutil2;
            cpy.ToursGacheFixe = _elt.ToursGacheFixe;
            cpy.rubriqueImpression = _elt.rubriqueImpression;

            // Propriétés D60001 ajoutées
            cpy.FeuilleOuBobine = _elt.FeuilleOuBobine;
            cpy.BlancCoulCarbone = _elt.BlancCoulCarbone;
            cpy.Coloris = _elt.Coloris;
            cpy.Provenance = _elt.Provenance;
            cpy.CodePapier = _elt.CodePapier;
            cpy.FormatPapier = _elt.FormatPapier != null ? new FormatBase(_elt.FormatPapier.Largeur, _elt.FormatPapier.Hauteur) : null;
            cpy.TypePli = _elt.TypePli;
            cpy.Depassant = _elt.Depassant;
            cpy.FondPerdu = _elt.FondPerdu;
            cpy.SensPoses = _elt.SensPoses;
            cpy.NbPoseLargeur = _elt.NbPoseLargeur;
            cpy.NbPoseHauteur = _elt.NbPoseHauteur;
            cpy.Fournisseur = _elt.Fournisseur;
            cpy.UniteAchat = _elt.UniteAchat;
            cpy.PrixAchat = _elt.PrixAchat;
            cpy.Commentaire = _elt.Commentaire;
            cpy.NbFeuilles = _elt.NbFeuilles;
            cpy.Epaisseur = _elt.Epaisseur;
            cpy.Grammage = _elt.Grammage;
            cpy.Sorte = _elt.Sorte;
            cpy.SousSorte = _elt.SousSorte;

            // Options
            cpy.Option1 = _elt.Option1; cpy.Option2 = _elt.Option2; cpy.Option3 = _elt.Option3;
            cpy.Option4 = _elt.Option4; cpy.Option5 = _elt.Option5; cpy.Option6 = _elt.Option6;
            cpy.Option7 = _elt.Option7; cpy.Option8 = _elt.Option8; cpy.Option9 = _elt.Option9;
            cpy.Option10 = _elt.Option10; cpy.Option11 = _elt.Option11; cpy.Option12 = _elt.Option12;
            cpy.Option13 = _elt.Option13; cpy.Option14 = _elt.Option14; cpy.Option15 = _elt.Option15;
            cpy.Option16 = _elt.Option16; cpy.Option17 = _elt.Option17; cpy.Option18 = _elt.Option18;
            cpy.Option19 = _elt.Option19;

            // Deep-clone lignes d'impression
            foreach (LigneFabrication lig in _elt.LignesImpression)
            {
                LigneFabrication cpyLig = CloneLigne(cpy, lig);
                if (cpyLig != null) cpy.LignesImpression.Add(cpyLig);
            }

            // Deep-clone étapes process avec rubriques et lignes
            foreach (EtapeProcess ep in _elt.EtapesProcess)
            {
                EtapeProcess cpyEp = new EtapeProcess(cpy);
                cpyEp.Ordre = ep.Ordre;
                cpyEp.Etape = ep.Etape;

                foreach (RubriqueChoisie rubc in ep.RubriquesChoisies)
                {
                    RubriqueChoisie cpyRubc = new RubriqueChoisie(cpyEp);
                    cpyRubc.rubrique = rubc.rubrique;
                    cpyRubc.TauxGacheVariable = rubc.TauxGacheVariable;
                    cpyRubc.GacheFixe = rubc.GacheFixe;
                    cpyRubc.SequenceGache = rubc.SequenceGache;
                    cpyRubc.ReductionCadence = rubc.ReductionCadence;
                    cpyRubc.typeReductionCadence = rubc.typeReductionCadence;
                    cpyRubc.LieMachine = rubc.LieMachine;
                    cpyRubc.lienMachineUtilise = rubc.lienMachineUtilise;
                    Array.Copy(rubc.Reponses, cpyRubc.Reponses, 5);

                    foreach (LigneFabrication lig in rubc.LignesFabrication)
                    {
                        LigneFabrication cpyLig = CloneLigne(cpy, lig);
                        if (cpyLig != null) cpyRubc.LignesFabrication.Add(cpyLig);
                    }

                    cpyEp.RubriquesChoisies.Add(cpyRubc);
                }

                cpy.EtapesProcess.Add(cpyEp);
            }

            return cpy;
        }

        private static LigneFabrication CloneLigne(ElementDevis cpy, LigneFabrication lig)
        {
            LigneFabrication cpyLig = LigneFabricationFactory.CreateLigneFabrication(cpy, lig.GetTypeLigne());
            if (cpyLig == null) return null;

            cpyLig.regle = lig.regle;
            cpyLig.modele = lig.modele;
            cpyLig.Nombre = lig.Nombre;
            cpyLig.TempsUnitaire = lig.TempsUnitaire;
            cpyLig.Cadence = lig.Cadence;
            cpyLig.Quantite = lig.Quantite;
            cpyLig.Diviseur = lig.Diviseur;
            cpyLig.BaseCalcul = lig.BaseCalcul;
            cpyLig.BaseCalculMP = lig.BaseCalculMP;
            cpyLig.Resultat = lig.Resultat;
            cpyLig.ResultatMP = lig.ResultatMP;
            cpyLig.Sequence = lig.Sequence;
            cpyLig.Commentaire = lig.Commentaire;
            cpyLig.ReponseNombre = lig.ReponseNombre;
            cpyLig.ReponseTemps = lig.ReponseTemps;
            cpyLig.ReponseCadence = lig.ReponseCadence;
            cpyLig.ReponseQuantite = lig.ReponseQuantite;
            cpyLig.ReponseDiviseur = lig.ReponseDiviseur;

            // Propriétés spécifiques aux sous-classes
            if (lig is LigneFabricationOperation && cpyLig is LigneFabricationOperation)
            {
                ((LigneFabricationOperation)cpyLig).operationMachine = ((LigneFabricationOperation)lig).operationMachine;
            }
            else if (lig is LigneFabricationMatiere && cpyLig is LigneFabricationMatiere)
            {
                var src = (LigneFabricationMatiere)lig;
                var dst = (LigneFabricationMatiere)cpyLig;
                dst.matiere = src.matiere;
                dst.ReponseMatiere = src.ReponseMatiere;
                dst.PrixAchat = src.PrixAchat;
                dst.Coefficient = src.Coefficient;
            }
            else if (lig is LigneFabricationSousTraitance && cpyLig is LigneFabricationSousTraitance)
            {
                var src = (LigneFabricationSousTraitance)lig;
                var dst = (LigneFabricationSousTraitance)cpyLig;
                dst.operationSousTraitance = src.operationSousTraitance;
                dst.PrixFixe = src.PrixFixe;
                dst.PrixProp = src.PrixProp;
                dst.QuantiteProp = src.QuantiteProp;
            }

            return cpyLig;
        }

    }

    public class EtapeProcess
    {
        public ElementDevis element { get; private set; }
        public int Ordre { get; set; }
        public String Etape { get; set; }

        public List<RubriqueChoisie> RubriquesChoisies;

        public EtapeProcess()
        {
            this.RubriquesChoisies = new List<RubriqueChoisie>();
            //this.LignesImpression = new List<LigneFabrication>();
         
        }

        public EtapeProcess(ElementDevis _elem)
            : this()
        {
            this.element = _elem;

        }

        public RubriqueChoisie[] GetRubriquesChoisiesAsArray()
        {
            return RubriquesChoisies.ToArray();
        }

    }
    public class RubriqueChoisie
    {
        public Rubrique rubrique { get; set; }

        public EtapeProcess etape { get; private set; }

        public decimal[] Reponses { get; set; }

        public decimal TauxGacheVariable { get; set; }
        public int GacheFixe { get; set; }

        public int SequenceGache { get; set; }

        public int ReductionCadence { get; set; }
        public string typeReductionCadence { get; set; }

        public bool ReponsesAdonner { get; set; }
        public bool DetailAConstituer { get; set; }
        public bool ASupprimer { get; set; }
        public bool AValoriser { get; set; }
        public bool LieMachine { get; set; }

        public LienRubriqueMachine lienMachineUtilise { get; set; }

        public List<LigneFabrication> LignesFabrication;

        public RubriqueChoisie()
        {
            this.LignesFabrication = new List<LigneFabrication>();
            this.Reponses = new decimal[5];
        }

        public RubriqueChoisie(EtapeProcess _etape)
            : this()
        {
            this.etape = _etape;

        }

        public String getReponse(int idx)
        {
            return Reponses[idx].ToString("F", CultureInfo.InvariantCulture);
        }

    }

}
