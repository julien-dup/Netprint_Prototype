// Porté de Devis2017 (Net.Axilog.Model/LigneFabrication.cs) le 02/11/2026 — moteur de devis, lot S1 (docs/compte-rendu-saisie-devis.md).
// Code repris tel quel, à l'espace de noms près ; les corrections métier sont signalées « CORRECTION ».
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel;

namespace NetPrint.Modules.Ventes.Moteur.Modele
{
    public class LigneFabrication : INotifyPropertyChanged
    {
        //public String TypeLigne { get; set; }
        public ElementDevis Element { get; protected set;}
        public ModeleLigneRubrique modele { get; set; }

        public RegleCalcul regle { get; set; }

        public String ModeleLib { get { return modele != null ? modele.ToString() : GetReference(); } }
        public String ReferenceLib { get { var r = GetReference(); var d = GetDesignationReference(); return string.IsNullOrEmpty(d) ? r : r + " - " + d; } }
        public String RegleLib { get { return regle != null ? regle.Id + " - " + regle.Nom : ""; } }
        private decimal dBasecalcul; 
        public decimal BaseCalcul
        {
            get { return dBasecalcul; }
            set
            {
                if (value != dBasecalcul)
                {
                    // Call OnPropertyChanged whenever the property is updated
                    dBasecalcul = value;
                    OnPropertyChanged("BaseCalcul");
                }
            }
        }
        public decimal BaseCalculMP { get; set; }
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


        private decimal dResultat;
        public decimal Resultat
        {
            get { return dResultat; }
            set
            {
                if (value != dResultat)
                {
                    // Call OnPropertyChanged whenever the property is updated
                    dResultat = value;
                    OnPropertyChanged("Resultat");
                }
            }
        }
        public decimal ResultatMP { get; set; }

        public int Sequence { get; set; }

        public virtual void CalculeResultat() { }
        public virtual String GetReference() { return String.Empty; }
        public virtual String GetDesignationReference() { return String.Empty; }
        public virtual String GetTypeLigne() { return String.Empty; }
        public virtual bool AvecGacheVariable() {return false;}

        public virtual String CodeTri { get { return String.Empty; } }

        public virtual decimal Valeur { get { return 0.0M; } }

        public virtual decimal ValeurMP { get { return 0.0M; } }

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
        /// <summary>Calcule la base de la ligne selon la regle de calcul de la ligne 
        /// </summary>
        private void CalculeBases()
        {

            decimal result = 1.0M;
            if (this.regle.ProportionnelElementsIdentiques) result = result * this.Element.ElementsIdentiques;

            //TODO tester avec gache fixe
            //TODO gaches auto 

            switch (this.regle.typeBase)
            {
                case TypeBase.Aucune:
                    break;
                case TypeBase.Exemplaires:
                    result = result * this.Element.GetQuantite();
                    break;
                case TypeBase.MetrageElement:
                    if (this.AvecGacheVariable())
                        result = result * (this.Element.MetrageUtile + this.Element.MetrageGacheVariable);
                    else
                        result = result * this.Element.MetrageUtile;
                    break;
                case TypeBase.ToursElement:
                    if (this.AvecGacheVariable())
                        result = result * (this.Element.ToursUtiles + this.Element.ToursGacheVariable);
                    else
                        result = result * this.Element.ToursUtiles;
                    break;
                case TypeBase.ToursTotal:
                    break;
                case TypeBase.MetrageEntete:
                    result = result * (this.Element.devis.Quantite * this.Element.devis.formatFini.GetHauteurM());
                    break;
                case TypeBase.Feuilles:
                    if (this.AvecGacheVariable())
                        result = result * (this.Element.FeuillesUtiles + this.Element.FeuillesGacheVariable);
                    else
                        result = result * this.Element.FeuillesUtiles;
                    break;
                case TypeBase.UC:
                    if (this.Element.devis.EmballagePar > 0) result = result * Math.Ceiling((decimal)this.Element.devis.Quantite / this.Element.devis.EmballagePar);
                    break;
                default:
                    break;

            }

            switch (this.regle.typeBaseSurface)
            {
                case TypeBaseSurfacePoids.Aucune:
                    break;
                case TypeBaseSurfacePoids.FormatOuvertUnitaire:
                    result = result * this.Element.formatOuvert.GetSurfaceM2();
                    break;
                case TypeBaseSurfacePoids.FormatPapierUnitaire:
                    result = result * this.Element.support.Format.GetSurfaceM2();
                    break;
                case TypeBaseSurfacePoids.FormatTirageUnitaire:
                    result = result * this.Element.formatTirage.GetSurfaceM2();
                    break;
                case TypeBaseSurfacePoids.FormatOuvertTotal:
                    result = result * this.Element.formatOuvert.GetSurfaceM2() * this.Element.ToursUtiles;
                    break;
                case TypeBaseSurfacePoids.FormatTirageTotal:
                    result = result * this.Element.formatTirage.GetSurfaceM2() * this.Element.ToursUtiles;
                    break;
                case TypeBaseSurfacePoids.FormatPapierTotal:
                    result = result * this.Element.support.Format.GetSurfaceM2() * this.Element.ToursUtiles;
                    break;
                default:
                    break;
            }

            switch (this.regle.typeBasePoids)
            {
                case TypeBaseSurfacePoids.Aucune:
                    break;
                case TypeBaseSurfacePoids.FormatOuvertUnitaire:
                    result = result * this.Element.formatOuvert.GetSurfaceM2() * this.Element.support.grammage / 1000.0M;
                    break;
                case TypeBaseSurfacePoids.FormatPapierUnitaire:
                    result = result * this.Element.support.Format.GetSurfaceM2() * this.Element.support.grammage / 1000.0M;
                    break;
                case TypeBaseSurfacePoids.FormatTirageUnitaire:
                    result = result * this.Element.formatTirage.GetSurfaceM2() * this.Element.support.grammage / 1000.0M;
                    break;
                case TypeBaseSurfacePoids.FormatOuvertTotal:
                    result = result * this.Element.formatOuvert.GetSurfaceM2() * this.Element.ToursUtiles * this.Element.support.grammage / 1000.0M;
                    break;
                case TypeBaseSurfacePoids.FormatTirageTotal:
                    result = result * this.Element.formatTirage.GetSurfaceM2() * this.Element.ToursUtiles * this.Element.support.grammage / 1000.0M;
                    break;
                case TypeBaseSurfacePoids.FormatPapierTotal:
                    result = result * this.Element.support.Format.GetSurfaceM2() * this.Element.ToursUtiles * this.Element.support.grammage / 1000.0M;
                    break;
                default:
                    break;
            }

            this.BaseCalcul = result;

            
        }

        /// <summary>Calcule la base Mille Plus de la ligne selon la regle de calcul de la ligne 
        /// </summary>
        private void CalculeBasesMP()
        {

            decimal result = 1.0M;
            if (this.regle.ProportionnelElementsIdentiques) result = result * this.Element.ElementsIdentiques;


            switch (this.regle.typeBase)
            {
                case TypeBase.Aucune:
                    break;
                case TypeBase.Exemplaires:
                    result = 1000.0M;
                    break;
                case TypeBase.MetrageElement:
                    if (this.AvecGacheVariable())
                        result = result * (this.Element.GetMetrageUtileMP() + this.Element.GetMetrageGacheVariableMP());
                    else
                        result = result * this.Element.GetMetrageUtileMP();
                    break;
                case TypeBase.ToursElement:
                    if (this.AvecGacheVariable())
                        result = result * (this.Element.GetToursUtilesMP() + this.Element.GetToursGacheVariableMP());
                    else
                        result = result * this.Element.GetToursUtilesMP();
                    break;
                case TypeBase.ToursTotal:
                    break;
                case TypeBase.MetrageEntete:
                    result = result * this.Element.devis.formatFini.GetHauteurM() * 1000.0M;
                    break;
                case TypeBase.Feuilles:
                    if (this.AvecGacheVariable())
                        result = result * (this.Element.GetFeuillesUtilesMP() + this.Element.GetFeuillesGacheVariableMP());
                    else
                        result = result * this.Element.GetFeuillesUtilesMP();
                    break;
                case TypeBase.UC:
                    if (this.Element.devis.EmballagePar > 0) result = result * Math.Ceiling((decimal)1000.0M / this.Element.devis.EmballagePar);
                    break;
                default:
                    break;

            }

            switch (this.regle.typeBaseSurface)
            {
                case TypeBaseSurfacePoids.Aucune:
                    break;
                case TypeBaseSurfacePoids.FormatOuvertUnitaire:
                    result = result * this.Element.formatOuvert.GetSurfaceM2();
                    break;
                case TypeBaseSurfacePoids.FormatPapierUnitaire:
                    result = result * this.Element.support.Format.GetSurfaceM2();
                    break;
                case TypeBaseSurfacePoids.FormatTirageUnitaire:
                    result = result * this.Element.formatTirage.GetSurfaceM2();
                    break;
                case TypeBaseSurfacePoids.FormatOuvertTotal:
                    result = result * this.Element.formatOuvert.GetSurfaceM2() * this.Element.GetToursUtilesMP();
                    break;
                case TypeBaseSurfacePoids.FormatTirageTotal:
                    result = result * this.Element.formatTirage.GetSurfaceM2() * this.Element.GetToursUtilesMP();
                    break;
                case TypeBaseSurfacePoids.FormatPapierTotal:
                    result = result * this.Element.support.Format.GetSurfaceM2() * this.Element.GetToursUtilesMP();
                    break;
                default:
                    break;
            }

            switch (this.regle.typeBasePoids)
            {
                case TypeBaseSurfacePoids.Aucune:
                    break;
                case TypeBaseSurfacePoids.FormatOuvertUnitaire:
                    result = result * this.Element.formatOuvert.GetSurfaceM2() * this.Element.support.grammage / 1000.0M;
                    break;
                case TypeBaseSurfacePoids.FormatPapierUnitaire:
                    result = result * this.Element.support.Format.GetSurfaceM2() * this.Element.support.grammage / 1000.0M;
                    break;
                case TypeBaseSurfacePoids.FormatTirageUnitaire:
                    result = result * this.Element.formatTirage.GetSurfaceM2() * this.Element.support.grammage / 1000.0M;
                    break;
                case TypeBaseSurfacePoids.FormatOuvertTotal:
                    result = result * this.Element.formatOuvert.GetSurfaceM2() * this.Element.GetToursUtilesMP() * this.Element.support.grammage / 1000.0M;
                    break;
                case TypeBaseSurfacePoids.FormatTirageTotal:
                    result = result * this.Element.formatTirage.GetSurfaceM2() * this.Element.GetToursUtilesMP() * this.Element.support.grammage / 1000.0M;
                    break;
                case TypeBaseSurfacePoids.FormatPapierTotal:
                    result = result * this.Element.support.Format.GetSurfaceM2() * this.Element.GetToursUtilesMP() * this.Element.support.grammage / 1000.0M;
                    break;
                default:
                    break;
            }

            this.BaseCalculMP = result;

            
        }


        public void Calcule()
        {
            CalculeBases();
            CalculeBasesMP();
            CalculeResultat();
        }

        public void ElementDevis_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (sender is ElementDevis)
            {
                if ((e.PropertyName == "Quantite") || (e.PropertyName == "FormatFini"))
                {
                    this.Calcule();
                }
            }
        }
    }

    public class LigneFabricationOperation : LigneFabrication
    {

        public LigneFabricationOperation(ElementDevis _elem)
        {
            this.Element = _elem;
        }

        public OperationMachine operationMachine { get; set; }

        //public decimal TauxHoraireMachine { get; set; }
        //public decimal TauxHoraireMO { get; set; }

        public override decimal Valeur
        {
            get { return Decimal.Round(this.Resultat * (this.operationMachine.TauxHoraireMachine + this.operationMachine.TauxHoraireMO),2); }
        }

        public override decimal ValeurMP
        {
            get { return Decimal.Round(this.ResultatMP * (this.operationMachine.TauxHoraireMachine + this.operationMachine.TauxHoraireMO), 2); }
        }

        public decimal ValeurMachine
        {
            get { return Decimal.Round(this.Resultat * (this.operationMachine.TauxHoraireMachine), 2); }
        }

        public decimal ValeurMainOeuvre
        {
            get { return Decimal.Round(this.Resultat * (this.operationMachine.TauxHoraireMO), 2); }
        }

        public override String GetReference() { return String.Concat(operationMachine.section.Id, operationMachine.Operation.ToString().PadLeft(3, '0')); }
        public override String GetDesignationReference() { return operationMachine.ToString(); }
        public override String GetTypeLigne() { return "O"; }
        public override bool AvecGacheVariable() { return operationMachine.AvecGacheVariable; }

        public override string CodeTri
        {
            get
            {
                return operationMachine.section.Atelier;
            }
            
        }

        public override void CalculeResultat()
        {

            Resultat = 1.0M;
            if (regle.UtilisationTemps)
            {
                if (TempsUnitaire == 0) TempsUnitaire = operationMachine.TempsStandard;
                Resultat = Resultat * TempsUnitaire;

            }

            if (regle.UtilisationNombre) Resultat = Resultat * Nombre;

            if (regle.UtilisationQuantite) Resultat = Resultat * Quantite;

            if (regle.UtilisationCadence)
            {
                if (Cadence == 0) Cadence = operationMachine.CadenceStandard;
                if (Cadence > 0) Resultat = Resultat / Cadence;
            }

            if ((regle.UtilisationDiviseur) && (Diviseur > 0)) Resultat = Resultat / Diviseur;

            ResultatMP = Decimal.Round(Resultat * BaseCalculMP, 3);
            Resultat = Decimal.Round(Resultat * BaseCalcul, 3);


            if ((Resultat > 0) && (operationMachine.TypeFrais == OperationMachine.FraisProportionnel)) Resultat = Math.Max(Resultat, operationMachine.TempsMini);

            if (operationMachine.TypeFrais == OperationMachine.FraisFixe) ResultatMP = 0;

        }
    }

    public class LigneFabricationMatiere : LigneFabrication
    {

        public LigneFabricationMatiere(ElementDevis _elem)
        {
            this.Element = _elem;
        }

        public Matiere matiere { get; set; }
        public int ReponseMatiere { get; set; }

        public decimal PrixAchat { get; set; }
        public decimal Coefficient { get; set; }


        public override decimal Valeur
        {
            get
            {
                return Decimal.Round(this.Resultat * (this.PrixAchat * this.Coefficient),2);
              
            }
        }

        public override decimal ValeurMP
        {
            get
            {
                return Decimal.Round(this.ResultatMP * (this.PrixAchat * this.Coefficient), 2);

            }
        }

        public override String GetReference() {
            if (matiere != null)
                return matiere.Id.ToString().PadLeft(6, '0');
            else
                return "999999";

        }
        public override String GetDesignationReference() {
            if (matiere != null)
                return matiere.ToString();
            else
                return String.Empty;
        }
        public override String GetTypeLigne() { return "M"; }
        public override bool AvecGacheVariable() {
            if (matiere != null)
                return matiere.AvecGacheVariable;
            else
                return true;
        }
        public override string CodeTri
        {
            get
            {
                return "Matières";
            }

        }
        public override void CalculeResultat()
        {

            Resultat = 1.0M;

            if (regle.UtilisationNombre) Resultat = Resultat * Nombre;

            if (regle.UtilisationQuantite) Resultat = Resultat * Quantite;

            if (regle.UtilisationCadence)
            {
                if (Cadence > 0) Resultat = Resultat / Cadence;
            }

            if ((regle.UtilisationDiviseur) && (Diviseur > 0)) Resultat = Resultat / Diviseur;

            ResultatMP = Decimal.Round(Resultat * BaseCalculMP, 2);
            Resultat = Decimal.Round(Resultat * BaseCalcul,2);


        }

    }

    public class LigneFabricationSousTraitance : LigneFabrication
    {

        public LigneFabricationSousTraitance(ElementDevis _elem)
        {
            this.Element = _elem;
        }

        public OperationSousTraitance operationSousTraitance { get; set; }

        public decimal PrixFixe { get; set; }
        public decimal PrixProp { get; set; }
        public decimal QuantiteProp { get; set; }

        public override decimal Valeur
        {
            get {

                if (this.PrixProp > 0)
                {
                    if (this.QuantiteProp > 0)
                        return Decimal.Round((this.Resultat * this.PrixProp / this.QuantiteProp) + this.PrixFixe,2);
                    else
                        return Decimal.Round((this.Resultat * this.PrixProp) + this.PrixFixe, 2);
                }
                else return Decimal.Round(this.Resultat * this.PrixFixe, 2);
            }
        }

        public override decimal ValeurMP
        {
            get
            {

                if (this.PrixProp > 0)
                {
                    if (this.QuantiteProp > 0)
                        return Decimal.Round((this.ResultatMP * this.PrixProp / this.QuantiteProp), 2) ;
                    else
                        return Decimal.Round((this.ResultatMP * this.PrixProp), 2) ;
                }
                else return Decimal.Round(this.ResultatMP * this.PrixFixe, 2);
            }
        }

        public override String GetReference() { return String.Concat(operationSousTraitance.fournisseur.Id.ToString().PadLeft(3, '0'), operationSousTraitance.Operation.ToString().PadLeft(3, '0')); }
        public override String GetDesignationReference() { return operationSousTraitance.ToString(); }
        public override String GetTypeLigne() { return "S"; }
        public override bool AvecGacheVariable() { return operationSousTraitance.AvecGacheVariable; }

        public override string CodeTri
        {
            get
            {
                return "Sous-Traitances";
            }

        }

        public override void CalculeResultat()
        {

            Resultat = 1.0M;
            if (regle.UtilisationTemps)
            {
                Resultat = Resultat * TempsUnitaire;

            }

            if (regle.UtilisationNombre) Resultat = Resultat * Nombre;

            if (regle.UtilisationQuantite) Resultat = Resultat * Quantite;

            if (regle.UtilisationCadence)
            {
                if (Cadence > 0) Resultat = Resultat / Cadence;
            }

            if ((regle.UtilisationDiviseur) && (Diviseur > 0)) Resultat = Resultat / Diviseur;

            ResultatMP = Decimal.Round(Resultat * BaseCalculMP, 2);
            Resultat = Decimal.Round(Resultat * BaseCalcul, 2);

        }
    }


}
