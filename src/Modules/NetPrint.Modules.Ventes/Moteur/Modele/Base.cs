// Porté de Devis2017 (Net.Axilog.Model/Base.cs) le 02/11/2026 — moteur de devis, lot S1 (docs/compte-rendu-saisie-devis.md).
// Code repris tel quel, à l'espace de noms près ; les corrections métier sont signalées « CORRECTION ».
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NetPrint.Modules.Ventes.Moteur.Modele
{

    public class TypeCivilite
    {
        public const string Monsieur = "Monsieur";
        public const string Madame = "Madame";
    }

     public class ZoneGeographique
    {
        public string code { get; set; }
        public string nom { get; set; }
   
    }

    //public enum TypeProduit
    //{
    //    Liasses = 1, Continu = 2, Blocs = 3, Brochures = 4, DiversPlat = 5, DiversAutres = 6
    //}

    public abstract class BaseTable
    {
        public string code { get; set; }
        public string nom { get; set; }

        public override string ToString()
        {
            return String.Concat(code , " ", nom);
        }
    }

    public class Adresse
    {
        public int NumeroAdresse { get; set; }
        public string Nom { get; set; }
        public string Rue1 { get; set; }
        public string Rue2 { get; set; }
        public string CodePostal { get; set; }
        public string Ville { get; set; }
        public Pays pays { get; set; }
        public ZoneGeographique zone {get; set;}
        //TODO ajout bareme transport
    }

    public class Pays
    {
        public string Id { get; set; }
        public string CodeISO { get; set; }
        public string Nom { get; set; }

        public override string ToString()
        {
            return String.Concat(CodeISO , " " , Nom);
        }
    }

    public class Contact
    {
        public TypeCivilite civilite { get; set; }
        public String Nom { get; set; }
        public String Telephone { get; set; }
        public String Email { get; set; }
        public override string ToString()
        {
            return String.Concat(civilite.ToString(), " ", Nom).Trim();
        }

        public Contact(String _nom)
        {
            this.Nom = _nom;
           
        }
    }

    public class ConditionsReglement
    {
        public int NombreDeJours {get; set;}
        public int JourEcheance {get; set;}
        public bool FinDeMois { get; set; }
        public int JourArrete {get; set;}

    }

    public abstract class Tiers
    {
        public String MotDirecteur { get; set; }
        public String RaisonSociale { get; set; }
        public String Ville { get; set; }
        public String Telephone { get; set; }
        public String Email { get; set; }

        public Decimal TauxRistourne { get; set; }
        public Decimal TauxEscompte { get; set; }

        public ConditionsReglement Reglement { get; set; }

        public List<Contact> Contacts;

    }

    public class Prospect : Tiers
    {
        public int Id { get; set; }
        public int CodeRepresentant { get; set; }
        public String InitFabricant { get; set; }
    }

    public class Client : Tiers
    {
        public int Id { get; set; }
        public int Collectif { get; set; }
        public String CMODTR { get; set; }
        public int CodeRepresentant { get; set; }
        public String InitFabricant { get; set; }
    }

    public class Fournisseur : Tiers
    {
        public int Id { get; set; }
    }

    public class Transporteur : Tiers
    {
        public int Id { get; set; }
    }

    public class Representant
    {
        public int Id { get; set; }
        public String nom { get; set; }
        public String nomExterne { get; set; }
        public String telephone { get; set; }
        public String email { get; set; }
        public decimal coefficient { get; set; }

        public Representant(int _id, String _nom)
        {
            this.Id = _id;
            this.nom = _nom;
            this.coefficient=1.0M;
        }

    }

    public class CategorieProduit
    {
        public String Id { get; set; }
        public String Designation { get; set; }
        public String produit { get; set; }
    }

    public class SousCategorieProduit
    {
        public String Id { get; set; }
        public String Designation { get; set; }
        public CategorieProduit categorie { get; set; }
    }

    public class UniteVente
    {
        public String Id { get; set; }
        public String Designation { get; set; }
        public bool Autout { get; set; }
        public int DiviseurQuantite { get; set; }
    }

    public class FormatBase
    {
        public const string UNITEMM = "M";
        public const string UNITEPOUCE = "P";

        public decimal Largeur { get; set; }
        public decimal Hauteur { get; set; }
        public String UniteHauteur { get; set; }

        public FormatBase()
        {
            this.Largeur = 0.0M;
            this.Hauteur = 0.0M;
            this.UniteHauteur = UNITEMM;
        }

        public FormatBase(decimal _largeur, decimal _hauteur)
        {
            this.Largeur = _largeur;
            this.Hauteur = _hauteur;
            this.UniteHauteur = UNITEMM;
        }

        public FormatBase(decimal _largeur, decimal _hauteur, String _unite)
        {
            this.Largeur = _largeur;
            this.Hauteur = _hauteur;
            this.UniteHauteur = _unite;
        }

        /// <summary>récupère la hauteur du format en millimetres
        /// </summary>
        public decimal GetHauteurMM()
        {
            if (this.UniteHauteur == UNITEPOUCE) return this.Hauteur/10.0M * 25.4M; else return this.Hauteur;
        }

        /// <summary>récupère la hauteur du format en metres . Utilisée dans les calculs de métrage linéaire
        /// </summary>
        public decimal GetHauteurM()
        {
            if (this.UniteHauteur == UNITEPOUCE) return (this.Hauteur / 10.0M * 25.4M)/1000.0M; else return this.Hauteur/1000.0M;
        }

        /// <summary>récupère la surface du format en metres carrés
        /// </summary>
        public decimal GetSurfaceM2()
        {
            return this.Largeur * this.GetHauteurMM() / 1000000.0M;
        }

        public override string ToString()
        {
            if (Hauteur==0) return Largeur.ToString();
            return String.Concat(Largeur.ToString().Trim(), "x", Hauteur.ToString().Trim());
        }
    }

    public class FormatFini : FormatBase
    {
        public decimal Cote3 { get; set; }

        public FormatFini(decimal _largeur, decimal _hauteur)
            : base(_largeur, _hauteur)
        {
            this.Cote3 = 0.0M;

        }

        public FormatFini(decimal _largeur, decimal _hauteur, decimal _cote3)
            : base(_largeur, _hauteur)
        {
            this.Cote3 = _cote3;

        }

        public FormatFini(decimal _largeur, decimal _hauteur, String _unite)
            : base(_largeur, _hauteur, _unite)
        {
            this.Cote3 = 0.0M;

        }


    }

    public class Epaisseur
    {
        const string UNITEMM = "MM";
        const string UNITE10MM = "/10";
        const string UNITEMICRON = "µ";

        public decimal Longueur { get; set; }
        public string Unite { get; set; }

        public decimal GetEpaisseurEnMM()
        {
            switch (Unite)
            {
                case UNITEMM: return Longueur;
                case UNITE10MM: return Longueur / 10.0M;
                case UNITEMICRON: return Longueur / 1000.0M;
                default: return Longueur;
            }
        }

        public decimal GetEpaisseurEn10MM()
        {
            switch (Unite)
            {
                case UNITEMM: return Longueur*10.0M;
                case UNITE10MM: return Longueur;
                case UNITEMICRON: return Longueur / 100.0M;
                default: return Longueur;
            }
        }

        public decimal GetEpaisseurEnMicron()
        {
            switch (Unite)
            {
                case UNITEMM: return Longueur * 1000.0M;
                case UNITE10MM: return Longueur * 100.0M;
                case UNITEMICRON: return Longueur ;
                default: return Longueur;
            }
        }

    }

}
