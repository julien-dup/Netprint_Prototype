namespace NetPrint.Modules.Facturation.Services;

// En-tête de la facture (FAC080) utile à l'historique ; Escompte : FDESC (%) ; NombreJours : FDNECH
// Devise : FDCDEV (hors EUR, commission et honoraires des éléments autres que négoce sont calculés sur le montant en autre monnaie)
internal sealed record EnteteHistorique(int Societe, int Numero, DateOnly Date, string Type, string Enseigne, string CodeVente, int Representant,
    decimal Escompte, int NombreJours, int GeneralDonneur, int ClientDonneur, int GeneralFacture, int ClientFacture, string Devise = "EUR");

// Paramètres (AA000Z de la société, AA0003.CRIST du client facturé) : majorations CKMA3/4/5, frais de stockage, financiers et
// généraux (CKFG1 si CFTYPE = B), ristourne
internal sealed record ParametresHistorique(decimal Majoration45, decimal Majoration90, decimal Majoration120, decimal FraisStockage,
    decimal FraisFinanciers, decimal FraisGeneraux, string TypeCoefficient, decimal Ristourne);

// Ligne FAC100 ; TypeLigne : FAC020.LTYPE (vide si le code est absent de FAC020 : ligne ignorée, comme le RPG) ; ArticleNegoce :
// l'article existe dans SPF010 ; Prix : NEG100 (DNPUS, DNPUT) de la ligne de commande, null si absente
internal sealed record LigneHistorique(string TypeLigne, decimal Quantite, string Designation, int Remise, string Article, string Indice,
    decimal Montant, decimal MontantAutre, decimal TauxCommission, int Bl, int ElementBl, int LigneBl, bool ArticleNegoce, (decimal Pus, decimal Put)? Prix);

// Élément FAC090 ; Facturation : FEFAA (D définitif) ; ModeFacturation : AB001.CCFACT du dossier
internal sealed record ElementHistorique(int Numero, int Dossier, string Cle, string Categorie, string SousCategorie, int Type, decimal Quantite,
    decimal TauxCommission, decimal TauxHonoraire, string NomHonoraire, decimal MontantHonoraire, string Facturation, string OrigineTechnique,
    string ModeFacturation, IReadOnlyList<LigneHistorique> Lignes);

// =====================================================================
// Historique commercial de l'édition définitive (FAC199 HISTO / HISNEG, fiche docs/facturation/fiches/FAC199-historique.md) :
// FAC070 = une ligne par élément ; FAC072 = une ligne par ligne de vente des éléments de négoce (types 2 et 3) dont l'article est
// connu de SPF010 ou « tout à 9 ». Reprise à l'identique (troncatures, bases brutes ou nettes d'escompte, montants positifs pour
// un avoir, repéré par le type A), y compris deux comportements du RPG gardés pour que les statistiques restent comparables :
// après une remise en % d'un élément, ses lignes de vente suivantes n'ont pas de FAC072, et le taux de remise des lignes est la
// somme des remises (pas le taux composé) ; sans nombre de jours d'échéance (FDNECH), pas de majoration (PRTDIFD inopérant).
// Corrigé : prix d'une ligne de commande introuvable à 0 (le RPG reprenait ceux de la ligne lue avant) ; frais financiers sans
// commande connue calculés (le RPG reprenait le mode de facturation de l'élément précédent).
// Chaque ligne est un dictionnaire colonne → valeur (écriture et comparaison avec les lignes du RPG).
// =====================================================================
internal static class HistoriqueCommercial
{
    private const string ToutA9 = "999999999999999";

    // Troncature à 2 décimales vers zéro (MULT / DIV sans (H) du RPG)
    private static decimal T2(decimal x) => Math.Truncate(x * 100m) / 100m;

    public static (List<Dictionary<string, object>> Fac070, List<Dictionary<string, object>> Fac072) Calculer(EnteteHistorique f,
        ParametresHistorique p, IReadOnlyList<ElementHistorique> elements)
    {
        var fac070 = new List<Dictionary<string, object>>();
        var fac072 = new List<Dictionary<string, object>>();
        var z156 = 1m - f.Escompte / 100m;                       // coefficient d'escompte exact (HFTTHT, HNTTHT)
        var kTronque = 1m + T2(-f.Escompte / 100m);              // coefficient tronqué (commission et honoraires des autres types)
        var typeVente = $"{(f.Enseigne + " ")[0]}{(f.CodeVente + " ")[0]}".TrimEnd();
        decimal Net(decimal ht) => f.Escompte != 0 ? T2(z156 * ht) : ht;
        Dictionary<string, object> Commun(ElementHistorique e) => new()
        {
            ["RSOC"] = f.Societe, ["RDOSS"] = e.Dossier, ["RDCLE"] = e.Cle, ["RNFAC"] = f.Numero,
        };

        foreach (var e in elements)
        {
            decimal ht = 0, htAutre = 0, portTaxable = 0, portExonere = 0;
            var postes = new List<Poste>();
            var bloque = 0;   // cumul des remises % de l'élément : non multiple de 100 ⇒ plus de poste libre (RPG)
            var negoce = e.Type is 2 or 3;
            foreach (var l in e.Lignes)
            {
                switch (l.TypeLigne)
                {
                    case "V": ht += l.Montant; htAutre += l.MontantAutre; break;
                    case "R": ht -= l.Montant; htAutre -= l.MontantAutre; break;
                    case "P": portTaxable += l.Montant; break;
                    case "E": portExonere += l.Montant; break;
                }
                if (!negoce) continue;
                if (l.TypeLigne == "V" && bloque % 100 == 0)
                    postes.Add(new Poste(l) { Ht = l.Montant, HtAutre = l.MontantAutre });
                else if (l.TypeLigne == "R" && l.Remise != 0)
                {
                    foreach (var x in postes)
                    {
                        var taux = (100 - l.Remise) / 100m;
                        (x.Ht, x.HtAutre, x.TauxRemise) = (T2(x.Ht * taux), T2(x.HtAutre * taux), (x.TauxRemise + l.Remise) % 100);
                    }
                    bloque += l.Remise;
                }
            }

            // Commission et honoraires de l'élément (types autres que 2 / 3 : sur l'escompte tronqué, comme le RPG)
            var baseCommission = f.Devise == "EUR" ? ht : htAutre;   // FAC199 : MEHTE si FDCDEV = EUR, sinon MEHTF
            decimal Sur(decimal taux) => T2(T2(T2(kTronque * baseCommission) * taux) / 100m);
            var commission = e.TauxCommission != 0 && !negoce ? Sur(e.TauxCommission) : 0;
            var honoraires = e.TauxHonoraire != 0 ? Sur(e.TauxHonoraire) : e.MontantHonoraire;

            foreach (var x in postes.Where(x => x.HtAutre != 0 || x.Ht != 0 || x.Ligne.Quantite > 0))
            {
                var toutA9 = x.Ligne.Article == ToutA9;
                if (!toutA9 && !x.Ligne.ArticleNegoce) continue;   // article inconnu de SPF010 (ou à blanc) : pas de FAC072
                var htNet = Net(x.Ht);
                var montantCommission = T2(x.Ligne.TauxCommission * htNet / 100m);
                commission += montantCommission;
                var (pus, put) = toutA9 ? (htNet, htNet) : x.Ligne.Prix ?? (0, 0);
                var r = Commun(e);
                r["HNART"] = x.Ligne.Article; r["HNDES"] = x.Ligne.Article.Trim() != "" ? x.Ligne.Designation : "";
                (r["HNDTFA"], r["HNDTFM"], r["HNDTFJ"]) = (f.Date.Year, f.Date.Month, f.Date.Day);
                r["HNFA"] = f.Type; r["HNENS"] = f.Enseigne; r["HNTYVE"] = typeVente; r["HNREPR"] = f.Representant;
                r["HNQTE"] = x.Ligne.Quantite; r["HNTXRP"] = x.Ligne.TauxCommission; r["HNMTRP"] = montantCommission;
                r["HNTXRE"] = (decimal)x.TauxRemise; r["HNTTHT"] = htNet; r["HNPUS"] = pus; r["HNPUT"] = put;
                r["HNGEN"] = f.GeneralDonneur; r["HNCLI"] = f.ClientDonneur; r["HNPRPF"] = 0m; r["HNIND"] = x.Ligne.Indice;
                r["HNELM"] = e.Numero; r["HNBL"] = x.Ligne.Bl; r["HNBLEL"] = x.Ligne.ElementBl; r["HNBLLG"] = x.Ligne.LigneBl;
                fac072.Add(r);
            }

            decimal Frais(decimal taux) => T2(T2(ht * taux) / 100m);
            var majoration = e.Type == 3 || f.NombreJours < 45 ? 0
                : Frais(f.NombreJours >= 120 ? p.Majoration120 : f.NombreJours >= 90 ? p.Majoration90 : p.Majoration45);
            var h = Commun(e);
            h["HFCAT"] = e.Categorie; h["HFSCAT"] = e.SousCategorie;
            (h["HFDTFA"], h["HFDTFM"], h["HFDTFJ"]) = (f.Date.Year, f.Date.Month, f.Date.Day);
            h["HFFA"] = f.Type; h["HFENS"] = f.Enseigne; h["HFTYVE"] = typeVente; h["HFREPR"] = f.Representant; h["HFQTE"] = e.Quantite;
            h["HFTXRP"] = e.TauxCommission; h["HFMTRP"] = commission; h["HFNMHO"] = e.NomHonoraire; h["HFTXHO"] = e.TauxHonoraire;
            h["HFMTHO"] = honoraires; h["HFTTHT"] = Net(ht); h["HFTTPT"] = portTaxable; h["HFTTPE"] = portExonere;
            h["HFPRFM"] = 0m; h["HFTTFM"] = 0m;
            h["HFPRFG"] = p.TypeCoefficient == "B" ? p.FraisGeneraux : 0m;
            h["HFTTFG"] = p.TypeCoefficient == "B" ? Frais(p.FraisGeneraux) : 0m;
            h["HFTTFS"] = e.Type == 2 ? Frais(p.FraisStockage) : 0m;
            h["HFTTFF"] = e.Type == 2 && e.ModeFacturation is not ("T" or "2" or "3") ? Frais(p.FraisFinanciers) : 0m;
            h["HFTTCR"] = majoration;
            h["HFTTRI"] = e.Type == 3 ? 0m : Frais(p.Ristourne);
            h["HFGEN"] = f.GeneralDonneur; h["HFCLI"] = f.ClientDonneur; h["HFPRPF"] = 0m; h["HFORTC"] = e.OrigineTechnique;
            h["HFNACA"] = e.Type.ToString(); h["HFFDEF"] = e.Facturation == "D" ? "O" : "N"; h["HFTAG"] = "";
            h["HFNUM"] = e.Numero; h["HFGENF"] = f.GeneralFacture; h["HFCLIF"] = f.ClientFacture;
            fac070.Add(h);
        }
        return (fac070, fac072);
    }

    private sealed class Poste(LigneHistorique ligne)
    {
        public LigneHistorique Ligne { get; } = ligne;
        public decimal Ht { get; set; }
        public decimal HtAutre { get; set; }
        public int TauxRemise { get; set; }
    }
}
