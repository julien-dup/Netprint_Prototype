// Porté de Devis2017 (Net.Axilog.Model/SupportConvertionQuantite.cs) le 02/11/2026 — moteur de devis, lot S1 (docs/compte-rendu-saisie-devis.md).
// Code repris tel quel, à l'espace de noms près ; les corrections métier sont signalées « CORRECTION ».
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace NetPrint.Modules.Ventes.Moteur.Modele
{
    static class SupportConvertionQuantite
    {
        /* Unité de départ=Kilo */
        public static decimal KGM2(FormatBase _fmt, int _gram, decimal _qteo)
        {
            //if (_fmt.Hauteur > 0) return _qteo / (_fmt.GetSurfaceM2() * (_gram / 1000.0M));
            //else
                return (_qteo * 1000.0M) / _gram;
        }

        public static decimal KGUN(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return _qteo / (_fmt.GetSurfaceM2() * (_gram / 1000.0M));
        }

        public static decimal KGML(FormatBase _fmt, int _gram, decimal _qteo)
        {
           return ((_qteo * 1000.0M) / _gram)/(_fmt.Largeur/1000.0M);
        }

        public static decimal KGCM(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return 100.0M*KGM2(_fmt, _gram, _qteo);
        }

        public static decimal KGCK(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return _qteo/100.0M;
        }

        public static decimal KGTO(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return _qteo / 1000.0M;
        }

        public static decimal KGRA(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return KGUN(_fmt, _gram, _qteo) / 500.0M;
        }

        public static decimal KGLM(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return KGUN(_fmt, _gram, _qteo) / 1000.0M;
        }

        /* Unité de départ=Metre Carré */
        public static decimal M2KG(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return (_qteo * _gram) / 1000.0M;
        }

        public static decimal M2UN(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return _qteo / _fmt.GetSurfaceM2();
        }

        public static decimal M2ML(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return _qteo / (_fmt.Largeur/1000.0M);
        }

        public static decimal M2CM(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return _qteo / 100.0M;
        }

        public static decimal M2CK(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return M2KG(_fmt, _gram, _qteo) / 100.0M;
        }

        public static decimal M2TO(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return M2KG(_fmt, _gram, _qteo) / 1000.0M;
        }

        public static decimal M2RA(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return M2UN(_fmt, _gram, _qteo) / 500.0M;
        }

        public static decimal M2LM(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return M2UN(_fmt, _gram, _qteo) / 1000.0M;
        }

        /* Unité de départ=La feuille */
        public static decimal UNKG(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return _qteo * _fmt.GetSurfaceM2() * (_gram / 1000.0M);
        }

        public static decimal UNM2(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return _qteo * _fmt.GetSurfaceM2();
        }

        public static decimal UNCM(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return UNM2(_fmt, _gram, _qteo) / 100.0M;
        }

        public static decimal UNCK(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return UNKG(_fmt, _gram, _qteo) / 100.0M;
        }

        public static decimal UNTO(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return UNKG(_fmt, _gram, _qteo) / 1000.0M;
        }

        public static decimal UNRA(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return _qteo/ 500.0M;
        }

        public static decimal UNLM(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return _qteo / 1000.0M;
        }

        /* Unité de départ=Metre lineaire */
        public static decimal MLKG(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return _qteo * (_fmt.Largeur/1000.0M)* (_gram/1000.0M);
        }

        public static decimal MLM2(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return _qteo * (_fmt.Largeur/ 1000.0M);
        }

        public static decimal MLCM(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return MLM2(_fmt, _gram, _qteo) / 100.0M;
        }

        public static decimal MLCK(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return MLKG(_fmt, _gram, _qteo) / 100.0M;
        }

        public static decimal MLTO(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return MLKG(_fmt, _gram, _qteo) / 1000.0M;
        }

        /* Unité de départ=Cent Metres carrés */
        public static decimal CMKG(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return M2KG(_fmt, _gram, _qteo) * 100.0M;
        }

        public static decimal CMM2(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return _qteo * 100.0M;
        }

        public static decimal CMUN(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return M2UN(_fmt, _gram, _qteo) * 100.0M;
        }

        public static decimal CMML(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return M2ML(_fmt, _gram, _qteo) * 100.0M;
        }

        public static decimal CMCK(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return M2CK(_fmt, _gram, _qteo) * 100.0M;
        }

        public static decimal CMTO(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return M2TO(_fmt, _gram, _qteo) * 100.0M;
        }

        public static decimal CMRA(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return M2RA(_fmt, _gram, _qteo) * 100.0M;
        }

        public static decimal CMLM(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return M2LM(_fmt, _gram, _qteo) * 100.0M;
        }

        /* Unité de départ=Cent kilos */
        public static decimal CKKG(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return _qteo * 100.0M;
        }

        public static decimal CKTO(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return _qteo / 10.0M;
        }

        public static decimal CKM2(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return KGM2(_fmt, _gram, _qteo) * 100.0M;
        }

        public static decimal CKUN(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return KGUN(_fmt, _gram, _qteo) * 100.0M;
        }

        public static decimal CKML(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return KGML(_fmt, _gram, _qteo) * 100.0M;
        }

        public static decimal CKCM(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return KGCM(_fmt, _gram, _qteo) * 100.0M;
        }

        public static decimal CKRA(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return KGRA(_fmt, _gram, _qteo) * 100.0M;
        }

        public static decimal CKLM(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return KGLM(_fmt, _gram, _qteo) * 100.0M;
        }

        
        /* Unité de départ=Rame */
        public static decimal RAKG(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return UNKG(_fmt, _gram, _qteo) * 500.0M;
        }

        public static decimal RAM2(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return UNM2(_fmt, _gram, _qteo) * 500.0M;
        }

        public static decimal RACM(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return UNCM(_fmt, _gram, _qteo) * 500.0M;
        }

        public static decimal RAUN(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return  _qteo * 500.0M;
        }

        public static decimal RALM(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return _qteo / 2.0M;
        }

        public static decimal RACK(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return UNCK(_fmt, _gram, _qteo) * 500.0M;
        }

        public static decimal RATO(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return UNTO(_fmt, _gram, _qteo) * 500.0M;
        }

        /* Unité de départ=Le Mille de feuille */
        public static decimal LMKG(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return UNKG(_fmt, _gram, _qteo) * 1000.0M;
        }

        public static decimal LMM2(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return UNM2(_fmt, _gram, _qteo) * 1000.0M;
        }

        public static decimal LMCM(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return UNCM(_fmt, _gram, _qteo) * 1000.0M;
        }

        public static decimal LMUN(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return _qteo * 1000.0M;
        }

        public static decimal LMRA(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return _qteo * 2.0M;
        }


        public static decimal LMCK(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return UNCK(_fmt, _gram, _qteo) * 1000.0M;
        }

        public static decimal LMTO(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return UNTO(_fmt, _gram, _qteo) * 1000.0M;
        }

        /* Unité de départ=La Tonne */
        public static decimal TOKG(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return _qteo * 1000.0M;
        }

        public static decimal TOCK(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return _qteo * 10.0M;
        }

        public static decimal TOM2(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return KGM2(_fmt, _gram, _qteo) * 1000.0M;
        }

        public static decimal TOUN(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return KGUN(_fmt, _gram, _qteo) * 1000.0M;
        }

        public static decimal TOML(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return KGML(_fmt, _gram, _qteo) * 1000.0M;
        }

        public static decimal TOCM(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return KGCM(_fmt, _gram, _qteo) * 1000.0M;
        }
        public static decimal TORA(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return KGRA(_fmt, _gram, _qteo) * 1000.0M;
        }

        public static decimal TOLM(FormatBase _fmt, int _gram, decimal _qteo)
        {
            return KGLM(_fmt, _gram, _qteo) * 1000.0M;
        }

        
    }
}
