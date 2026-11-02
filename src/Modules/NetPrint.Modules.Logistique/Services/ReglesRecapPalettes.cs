using NetPrint.Modules.Logistique.Contrats;

namespace NetPrint.Modules.Logistique.Services;

// =====================================================================
// Récapitulatif palettes d'un pré-BL (FEN_RecapPalettes.Calcul, R21 à R23), sans base. Les palettes complètes sont numérotées
// à la suite, lot par lot, dans l'ordre dépôt, commande, article, lot (« N° 1 à 3 ») ; chaque palette mixte (lots incomplets
// regroupés sous un même n° choisi) reçoit ensuite le n° suivant (« N° 4 et n° 7 »). Un lot incomplet doit avoir un n° de
// palette mixte ; une palette mixte ne regroupe que des lots d'un même dépôt (réglage CalcPalExpDepConf = 0 du WinDev, par
// défaut) et 8 lots au plus. Le n° enregistré pour une palette mixte (EXP516.DNPALM) est son n° final, comme le WinDev.
// =====================================================================
internal static class ReglesRecapPalettes
{
    public const int LotsParPaletteMixte = 8;
    public const int PalettesMaxi = 99;   // EXP509.HNPALT : 2 chiffres

    public static RecapPalettesPreBl Calculer(int numero, IReadOnlyList<LigneRecapPreBl> lignes, IReadOnlyList<GroupePaletteMixte> groupes)
    {
        var choix = groupes.GroupBy(g => (g.Ligne, g.Lot)).ToDictionary(g => g.Key, g => g.Last().PaletteMixte);
        int Mixte(LigneRecapPreBl l) => l.Incomplete ? choix.GetValueOrDefault((l.Ligne, l.Lot), l.PaletteMixte) : 0;

        var erreur = "";
        if (lignes.FirstOrDefault(l => l.Incomplete && Mixte(l) <= 0) is { } sansMixte)
            erreur = $"Lot {sansMixte.NumeroLot} ({sansMixte.Article}, commande {sansMixte.Dossier}/{sansMixte.Cle}) : le n° de palette mixte doit être renseigné.";
        else if (lignes.Where(l => Mixte(l) > 0).GroupBy(Mixte).FirstOrDefault(g => g.Select(l => l.Depot).Distinct().Count() > 1) is { } deuxDepots)
            erreur = $"Palette mixte {deuxDepots.Key} : le regroupement de palettes doit être fait sur le même dépôt.";
        else if (lignes.Where(l => Mixte(l) > 0).GroupBy(Mixte).FirstOrDefault(g => g.Count() > LotsParPaletteMixte) is { } tropDeLots)
            erreur = $"Palette mixte {tropDeLots.Key} : {LotsParPaletteMixte} lots au plus.";
        if (erreur != "")
            return new RecapPalettesPreBl(numero, [.. lignes.Select(l => l with { PaletteMixte = Mixte(l), Libelle = "" })], 0, erreur);

        // Palettes complètes, à la suite
        var libelles = new string[lignes.Count];
        var fin = 0;
        for (var i = 0; i < lignes.Count; i++)
        {
            var debut = fin + 1;
            fin += lignes[i].PalettesCompletes;
            libelles[i] = lignes[i].PalettesCompletes == 0 ? "" : fin > debut ? $"N° {debut} à {fin}" : $"N° {debut}";
        }
        // Palettes mixtes : n° suivants, dans l'ordre de leur première ligne
        var numeros = new Dictionary<int, int>();
        var resultat = new List<LigneRecapPreBl>();
        for (var i = 0; i < lignes.Count; i++)
        {
            var mixte = Mixte(lignes[i]);
            var final = 0;
            if (mixte > 0)
            {
                if (!numeros.TryGetValue(mixte, out final)) numeros[mixte] = final = ++fin;
                libelles[i] = libelles[i] == "" ? $"N° {final}" : $"{libelles[i]} et n° {final}";
            }
            resultat.Add(lignes[i] with { PaletteMixte = final, Libelle = libelles[i] });
        }
        return fin > PalettesMaxi
            ? new RecapPalettesPreBl(numero, resultat, fin, $"{fin} palettes : un pré-BL en compte {PalettesMaxi} au plus.")
            : new RecapPalettesPreBl(numero, resultat, fin, "");
    }
}
