# Éditions du pré-BL — mise en page à refaire en PDF natif

> *Sources :* `D:\dvp\sources AS400\NP11\EXP535P2.rpgle` (2 351 l., DL oct. 2022) + `EXP535P2.dds` (PRTF, 361 l., S. Coclet 2000, enrichi KMN/YBU) ; appel par `EXP530NA2.clp` / `EXPEMPNA2.clp` ;
> projet WinDev `D:\Mes Projets\PreBL` : `ETAT_FeuilleKimmel.wde`, `ETAT_FeuilleKimmel3.wde`, `ETAT_FeuilleCamion.wde`, `FEN_ChoixDepot.wdw`, `FEN_GestionPreBL.wdw` (code extrait par `code_wdw.py`, libellés et positions décodés par `wdblob.py strings`).
> *Complète :* [PreBL.md](PreBL.md) (R24, R28, R29, paramètres `PreBL.Ini`), [EXP540.md](EXP540.md) (R8, R12 poids/colis), [PreBL-tables-et-programmes.md](PreBL-tables-et-programmes.md). Ce qui y est déjà dit n'est pas répété.
> *Date d'analyse :* 2026-11-01. *(supposé)* = déduit, non vérifié sur un PDF réel.

| Édition | Technologie | Déclencheur | Format |
| --- | --- | --- | --- |
| Pré-BL (« Bon de livraison ») | RPG EXP535P2 + PRTF AFP → PDF `/home/pdf/temp/PREBLssssnnnnnn.pdf` | `FEN_GestionPreBL` « Imprimer » → EXP530NAV → EXP530NA2 ; envoi groupé → EXPEMPNA2 | A4 portrait, `PAGESIZE(11,8 8,2 *UOM)` en pouces, police par défaut 5687 (Times) 12 pt |
| Feuille de préparation | `ETAT_FeuilleKimmel` (WinDev) | menu « Feuille prépa » (`OPT_FeuilleKimmel`) ± `FEN_ChoixDepot` | A4 paysage *(supposé : largeur utile 277 mm)*, Arial, aperçu `iAperçu(i100)` |
| (variante non appelée) | `ETAT_FeuilleKimmel3` | aucun appel dans le code | idem |
| Feuille camion | `ETAT_FeuilleCamion` (WinDev) | bouton « Feuille camion » → sélection → « Suivant » | A4 paysage *(supposé)*, Arial |

---

## 1. Édition du pré-BL — EXP535P2

### 1.1 Appel et paramètres

EXP530NA2 réécrit `PAR1` (1-24) avant l'appel : `soc(4) + ' ' + '000000' + n° pré-BL(6) + '000000' + 'L'` ; les positions 27-29 (poids palette de `PreBL.Ini`) sont conservées. Conséquences :

- `PBFOND = 'L'` ⇒ **indicateur 35 toujours allumé** (l. 422-425) : fond de page = logo en surimpression (`OVERLAY(*LIBL/&NOMLOGO)`, `NOMLOGO = PRT003F.LONOM1` clé soc + `EXP509.HENS` + `'BL'`, l. 500-502). Le bloc « Expéditeur » (société `AA0007`) n'est **jamais** imprimé (conditionné `N35`). `LONOM3` (logo courriel) n'est pris que si `Pdf = 'O'` (pos. 26), jamais passé *(supposé : toujours blanc)*.
- `OPNQRYF` sur EXP509 (soc + n°) : un seul pré-BL par appel ; `PBLDEB/PBLFIN` ignorés.
- `pPoidsPal` (27-29) testé par `TESTN` (l. 955, 1454) : choisit la méthode de poids (voir 1.6).

### 1.2 Croquis (positions en pouces, ligne ↓ / colonne →)

```
 0,65 ┌──────────────────────────────┐  Bon de livraison   NNNNNN(18pt)  Le jj/mm/aa   Page n
      │ LOGO (overlay PRT003F)       │
 0,90 │  ou, client revendeur :      │  Ref fournisseur : CREFFO              (ind. 21)
      │  bloc L26 = client comme     │
 1,30 │  expéditeur (RS, rue1, rue2, │  [Rep. : HREP INOMEX]                  (ind. 18, jamais)
      │  CP ville, Tél, Fax, E-mail) │
 1,60 └──────────────────────────────┘ ┌──────────────── 4,2 → 8,0 ─────────────┐
 1,75  [Expéditeur : … jamais (N35)]     │ Destinataire :                         │
 2,15                                    │ ALRS (gras)                            │
 2,46                                    │ ALRUE1 / 2,74 ALRUE2                   │
 3,02                                    │ CP VILLE / 3,18 PAYS                   │
 3,22                                    └────────────────────────────────────────┘
 3,37                                      HOBS / 3,55 HOBS2 / 3,73 HOBS3   (sous le cadre)
 3,50  Type de port : libellé liste 31
 3,68  Transporteur : MNOM / 3,86 CP ville transporteur / 4,04 Téléphone : MTELEP
 4,10                                      Notre N° CEE : SRFEXP
 4,22  Type livraison : MESSAGERIE…        4,25 Votre N° CEE : CRFEXP
 4,40 ┌─────────┬────────┬──────────────────────────────────┬──────┬────────┬──────┐ (cadre 0,31 → 8,00)
 4,55 │Référence│Quantité│Désignation/N° LOT en code à barres│ Code │ Colis  │Poids │
 4,60 ├─────────┴────────┴──────────────────────────────────┴──────┴────────┴──────┤
 4,85 │ XREF    │ XQTE   │ XDES (50 c., 9 pt)                │LIBETA│LIGNE1-4│XPDST │ +0,20 par ligne
  …   │         │        │   [code-barres lot à 4,60]        │      │        │      │ 25 lignes max
 9,95 └─────────┴────────┴──────────────────────────────────┴──────┴────────┴──────┘
 10,10                                                         A suivre...  (pages non finales)
 10,20 COLISAGE :
 10,37                                     RECEPTION PAR :
 10,50 Poids Total     : HPDSTT Kg
 10,60                                     DATE :                 SIGNATURE :
 10,67 Nombre de Colis : HNCOLT Colis
 10,84 Soit            : HNPALT Palettes   OBSERVATIONS :
```

Colonnes du tableau (DDS l. 22-31, 270-298) : séparateurs verticaux à 1,50 / 2,29 / 6,08 / 6,62 / 7,55. Polices détail : FGID 2304 *(supposé Helvetica)* 9 pt (référence 7 pt si ind. 09, colis 8 pt) ; en-têtes 5687 *(Times)* 11 pt, raison sociale 5707 *(Times gras, supposé)*.

### 1.3 En-tête (format EXP53501, l. 530-721)

| Zone | Source | Condition / remarque |
| --- | --- | --- |
| Titre | constante « Bon de livraison » (`LB(1)`) | « Retour n° » si ind. 19 : jamais (éteint l. 532) |
| N° | `EXP509.HNOBL` (18 pt) | n° pré-BL = n° de BL réservé |
| « Le » | `EXP509.HDEXJJ/MM/AA` (date de départ) en jj/mm/aa | blanc si date nulle (l. 653-657) |
| Page | compteur `WPAGE` | pas de « n / N » |
| Ref fournisseur | `AA0003.CREFFO` | ind. 21 si non blanc (l. 644) |
| Rep. | `HREP` / `B000F.INOMEX` | ind. 18 si `HREP ≠ 0` ; `HREP` n'existe pas dans EXP509 ⇒ **jamais imprimé** *(supposé)* |
| Bloc revendeur (L26) | `AA0003.CNOM, CRUE1, CRUE2, CPOSTA CVILLE, CTELEP, CTELEC, CEMAIL` | si `AA0003.CREVEN = 'O'` : ind. 26 allumé, 35 éteint ⇒ **pas de logo**, le client apparaît comme expéditeur (BL neutre) ; e-mail si non blanc (ind. 10) |
| Destinataire | `A500F.ALRS` (gras), `ALRUE1`, `ALRUE2`, `ALPOST ALVILL`, pays = `AA0008` table `PY` (`TLIB1`) sur `ALPAY` | clé soc + `HGENE` + `HCLIE` + `HNADRE` |
| Observations | `EXP509.HOBS`, `HOBS2`, `HOBS3` | toujours |
| Type de port | liste fixe 31 (`DDVLLSL0`, `Y2CNTX`) sur `EXP509.HTYPP` | |
| Transporteur | `B200F.MNOM`, `MPOSTA MVILLE`, `MTELEP` (clé `RSOC` + `HTRAN`) | libellé « Téléphone » forcé |
| Type livraison | `HTYPL` codé en dur : M MESSAGERIE, E EXPRESS, A Affrètement, I International, P Multi-pal | |
| Notre N° CEE | `AA0007.SRFEXP` | effacé si blanc ou si client revendeur (l. 544-550, 593-595) |
| Votre N° CEE | `AA0003.CRFEXP` | seulement si `CFREX = 'C'` et non blanc (l. 611-618, 634-641) |

### 1.4 Corps — deux présentations selon `AA0003.CENVBL`

`CENVBL` (« Envoi Double BL », valeurs forcées à `'1'` si ni 1 ni 2, l. 597-599) choisit la mise en page (l. 841, 1409-1411) :

**En tête du corps, une fois par pré-BL (les deux présentations)** :
1. si client non revendeur et `EXP005.FEOPT2 = 'O'` : bloc « Commande du client : » + `CNOM`, `CRUE1`, `CRUE2`, `CPOSTA CVILLE` (l. 735-771) ;
2. si `EXP509.HMENTI` non blanc : une ligne (précédée d'une ligne vide si `FEOPT2 = 'O'`) (l. 773-785).

#### a) Présentation habituelle (`CENVBL = '1'`) — une boucle sur EXP510 (`EXP510L1`, ordre de clé)

Pour chaque élément (ligne EXP510) :

| Ordre | Ligne imprimée | Source | Condition |
| --- | --- | --- | --- |
| 1 | Référence = n° de commande `nnnnn/c` ; désignation « Réf Cde : `CCREFC` du jj/mm/aa » ou « V/cde du … » | `EXP510.HNODOS/HCLEF`, `AB001.CCREFC`, `CCDENJ/M/A` | à chaque changement de dossier (l. 792-832), saut de 0,20 avant (ind. 83) |
| 2 | « Carton de N ex. » (ligne préalable) | liste 28 sur `ATYEMB` + `AEMBP` | article en UC (`SPF010/SPF026.AOPT3 = 'O'`) ; la quantité est alors **divisée par `AEMBP`** (l. 1143-1154) |
| 3 | **Ligne article** : Réf. = `DART/DIND` (ou `SPF010.ARFIMP` si `EXP005.FEDART = '2'`) ; Qté = `EXP510.DQTE` ; Désignation = `NEG100.DNDESA` ; Code = état `DETALI` (Acompte, Solde, Totalité, Suite) ; Poids = `XPDST` ; colonne Colis = 1ʳᵉ ligne de conditionnement | | article imprimable : `AOPT1 ≠ 'N'` (ind. 39, l. 933) |
| 4 | Mentions de norme `NOR010F.NOBL1..4` | clé soc + `AB001.CCENSE` + `NEG100.DNNORM` | si norme renseignée |
| 5 | « N/Ref: `AARTIC` » (si `FEDART = '2'`) sinon « V/Ref: `ARFIMP` », + « - `ANOMARC` » (n° de marché) — **en surbrillance** (ind. 48) | `SPF010` | V/Ref seulement si `ARFIMP` non blanc |
| 6 | Textes de la ligne de commande (19 × 40) | `NEG100T` | lignes non blanches |
| 7 | Désignations 1 à 3 | `EXP510.DOBS1..3` | non blanches |
| 8 | « Palette(s) `DLIBPAL` » par ligne de lot | `EXP516.DLIBPAL` | `EXP005.FEOPT4 ≠ 'O'` (R8 d'EXP540) |
| 9 | « N° d'appel : `DNAPPE` du jj/mm/aa » (ou « du … » sur une 2ᵉ ligne si code-barres lot actif) | `NEG100.DNAPPE`, `DNDTAC` | si n° d'appel |
| 10 | Lots (voir 1.5) | `EXP516` | `EXP005.FEOPT1 ≠ 'N'` |
| 11 | Lignes de conditionnement restantes (colonne Colis) | calcul | |
| 12 | Textes commande « à imprimer sur BL » (8 × 40) | `AB0011.CCTX` | `AB0011.CCIBL = 'O'` |

Les quatre lignes de **conditionnement** (colonne « Colis », 16 c.) s'impriment **à côté** des lignes suivantes de la même désignation (indicateurs 11-14, sous-programmes SPLIGN/SPCDT) :

| Cas | LIGNE1 | LIGNE2 | LIGNE3 | LIGNE4 |
| --- | --- | --- | --- | --- |
| colis complets seuls (`DENUC ≠ 0`, `DEEXMEC = 0`) | `DENUC` + type d'emballage (liste 28) | « de `emb.` ex. » | | |
| mécompte seul | « `DEEXMEC` ex. » | « en 1 `type` » | | |
| les deux | `DENUC type` | « de `emb.` ex. » | « et `DEEXMEC` ex. » | « en 1 `type` » |

`emb.` = `EXP510.DEEMBPE` si > 0, sinon `DEMBP1` (l. 947-951).

#### b) Présentation « PRINTOR » (`CENVBL = '2'`) — curseur SQL `Vuexp` (l. 1481-1553)

```sql
SELECT a.hnum, a.dart, a.dind, b.dndesa, a.dqte, a.hnumlc, a.detali, a.dobs1, a.dobs2,
       b.dnappe, b.dndtac, b.dnnorm, a.hnodos, a.hclef,
       SUM(c.dnuc), SUM(c.dexmec), SUM(c.dcolim)
FROM exp510 a
JOIN neg100 b ON (soc, dossier, clé, n° ligne, article, indice)
JOIN exp516 c ON a.rsoc = c.rsoc AND a.hnobl = c.dnbl AND a.hnum = c.dnum
WHERE a.rsoc = :psoc AND a.hnobl = :hnobl
GROUP BY <tout sauf les sommes>
ORDER BY dart, dind, dnappe, hnodos, hclef
```

- **Rupture article + indice** (SPDET9) : ligne d'en-tête **en surbrillance** : Réf. = `DART/DIND` (ou `ARFIMP` si `FEDART = '2'`), désignation « V/Ref: `ARFIMP` » (ou « N/Ref: `AARTIC` ») + « - `ANOMARC` ». Rupture n° d'appel (SPDET8) : vide.
- **Par élément** (SPDETX) : « Carton de N ex. » si `AOPT3 = 'O'` ; ligne Qté `DQTE` / désignation `NEG100.DNDESA` / état / poids ; `EXP510.DOBS1`, `DOBS2` ; « V/cde : `CCREFC` du jj/mm/aa - N/cde `nnnnn/c` » ; « Appel `DNAPPE` du jj/mm/aa » ; textes `AB0011` ; lots (si `FEOPT1 ≠ 'N'`) ; conditionnement calculé sur les **sommes des lots** (`DCOLIS`, `DEXMEC`, `DCOLIM`) avec `DEMBP1` ; espacement 0,10 en fin d'élément.
- **Fin d'article** (SPTOT9) : mentions de norme `NOR010F` (celles du **dernier** élément lu).
- Ne sont **pas** imprimés dans cette présentation : textes `NEG100T`, « Palette(s) … », `EXP510.DOBS3`.
- Les colonnes du FETCH sont **décalées volontairement** : `dndesa → :dobs1`, `a.dobs1 → :dobs2`, `a.dobs2 → :dobs3` (l. 1516-1517).

### 1.5 Lots et code-barres (SPLOT, l. 2136-2275)

- Lecture `EXP516` de l'élément ; regroupement des lignes **consécutives** de même `DNLOT` : « N° Lot : `DNLOT`  `Σ DNQTPL` ex. ».
- Si `AA0003.CDOP11 = 'O'` (« date sortie fab BL ») et lot numérique : « jj/mm/aa » de `DOS010.SFDSJJ/MM/AA` (clé soc + lot = n° dossier 5 c. + clé 1 c. + article).
- **Code-barres** si (`SPF010.ACABBL = 'O'` ou `AA0003.CDCABB = 'O'`) **et** `EXP005.FEOPT1 = 'O'` : format EXP53505, `BARCODE(CODE128 3 *NOHRI)`, module 0,015", 6 caractères (`DNLOT`), colonne 4,60, ligne `lig − 0,10` ; lot en clair à 4,90 (`lig + 0,50`) ; « Qté totale fab : `Σ AB0020.LAQTEC` » à 4,60 (`lig + 0,65`) (quantité commandée du dossier de fabrication = lot). Allume l'ind. 08 ⇒ titre de colonne « /N° LOT en code à barres » (EXP53508, 4,55 × 4,30) accolé à « Désignation ».
- Après un code-barres, `lig = ligd + 0,35` (lots intermédiaires) ou `ligd + 0,10` (dernier lot).

### 1.6 Pied (EXP53503, l. 1434-1475) et calcul colis / poids

1. Lignes de texte de pied `EXP512.HTTXT` (ordre de clé) imprimées **dans le corps**, avant le pied.
2. Totaux : `EXP509.HNCOLT`, `HNPALT`, `HPDSTT` s'ils sont non nuls (saisis/recalculés dans PreBL) ; sinon :
   - **Colis** (`HNCOLT = 0`) : `wcolisTot` = Σ par élément de `DENUC` + 1 si `DEEXMEC > 0` (l. 954-971) — **présentation 1 seulement**.
   - **Poids** (`HPDSTT = 0`) : `wpoidsTot` + `HNPALT` × poids d'une palette, arrondi (`Z-ADD(H)`) :
     - si `pPoidsPal` numérique et > 0 (l. 955-960, 1454-1458) : par élément `DENUC × APDSU` ; palette = `pPoidsPal` ;
     - sinon (l. 963-968, 1461) : par élément `DQTE × APDSEX / 1000 + DENUC` (+ 1 si mécompte) ; palette = `EXP005.FPDSP`.
   - **Palettes** : jamais recalculées ; ligne masquée si `HNPALT = 0` (ind. 46). Libellé « Soit : » (l. 458) et « Palette » au singulier si 1.
3. Ligne masquée si total nul : colis (45), palettes (46), poids (47).
4. Poids par ligne `XPDST` = `APDSU × DCOLIS + APDSU × DEXMEC / DEMBP1` (l. 1173-1176, 1653-1656).

`APDSU`, `ATYEMB`, `AEMBP` : `SPF010` écrasés par `SPF026` (conditionnement exceptionnel client, clé article + client + adresse) quand il existe (l. 931-932).

### 1.7 Sauts de page et fond de page (SPEDIT, l. 2026-2090)

- Nouvelle page si **26 lignes** imprimées (`NBRE ≥ 26`) ou `lig ≥ 9,85`, ou (bloc code-barres) `ligd ≥ 9,85` ; jamais déclenché par le pied (ind. 82).
- Au saut : `WPAGE + 1`, titre de colonne lot (si 08), **« A suivre... » écrit deux fois** au même endroit, `ENDPAGE`, overlay logo + en-tête complet réimprimés, `lig = 4,85`.
- Règle « ne pas séparer l'UC de son libellé » : si `NBRE = 25` avant LIGNE3, `NBRE + 1` pour forcer le saut (l. 902, 1387, 2002).
- Fond de page : seulement le logo (overlay AFP) et les cadres/traits du PRTF ; pas de formulaire pré-imprimé.

### 1.8 Anomalies d'impression à ne pas reproduire

1. **Titre « Bon de livraison »** sur un pré-BL (la préparation n'est pas un BL) — voir Q1.
2. **Poids ligne faux en présentation 1** : `DCOLIS` n'est alimenté que par le curseur (l. 253, 1519) ⇒ `XPDST` = poids des seuls ex. incomplets ; `DEXMEC` est celui de la **dernière ligne EXP516 lue** (pas `DEEXMEC`) ; test `wembp > 0` puis division par `DEMBP1` (l. 1174-1175) ⇒ **division par zéro** si seul `DEEMBPE` est renseigné.
3. **Présentation PRINTOR** : jointure interne sur `EXP516` ⇒ éléments sans lot (frais) **absents** ; total colis **non calculé** (0 si `HNCOLT = 0`) ; « en **1** carton » forcé alors que `Σ DCOLIM` peut dépasser 1 (l. 1683) ; normes du dernier élément seulement.
4. **« Bidouille » de conditionnement** (l. 1885-1898) : la dernière ligne colis est imprimée à `ligc − 0,30`, `ligc` n'étant **jamais remis à zéro** ⇒ superposition possible sur une ligne d'un élément précédent (ou à une position de la page précédente).
5. **Débordements** : désignation 50 c. en 9 pt proportionnelle, texte lot + date + code-barres à 4,60 sur la même ligne (risque de recouvrement) ; LIGNE1-4 de 16 c. à 6,65 contre le trait 7,55 ; référence en police par défaut (12 pt) quand l'ind. 09 est éteint (ligne n° de commande) ; observations `HOBS` imprimées **sous** le cadre destinataire ; pied à interlignes de 0,17" pour du 11 pt.
6. « A suivre... » écrit deux fois ; titre de colonne lot omis en bas des pages de fin de BL sauf la dernière ; totaux `wcolisTot/wpoidsTot` et indicateurs 08/16 **non réinitialisés** entre pré-BL (sans effet tant qu'un appel = un pré-BL).
7. Colonne intitulée « Code » qui contient l'**état de livraison** ; « Rep. » jamais alimenté ; libellés « Retour » morts.
8. Méthode de poids choisie par `PoidsPalette > 0` (paramètre **du poste**), alors que la feuille camion choisit par `CalcPalExpDepConf` ⇒ deux poids différents possibles pour le même pré-BL.

---

## 2. Feuille de préparation — ETAT_FeuilleKimmel (et Kimmel3)

### 2.1 Appel

`OPT_FeuilleKimmel` (`FEN_GestionPreBL`) sur la ligne sélectionnée (`TableSelect`, **un seul pré-BL**) :
- `CalcPalExpDepConf = 0` ⇒ `FEN_ChoixDepot(n°)` : saisie du dépôt (F4 `FEN_RecDepot`), contrôles « Le dépôt doit être renseigné. » / « Le dépôt n'existe pas. » (`B100F`, `DTAG ≠ '*'`), puis `ETAT_FeuilleKimmel(n°, dépôt, DLIB)` ;
- sinon ⇒ `ETAT_FeuilleKimmel(n°, 0, "DLIB")` (tous dépôts ; 3ᵉ paramètre inutilisé).

Aperçu à 100 % puis impression (`iAperçu(i100, "Edition feuille prépa")`).

### 2.2 Sélection (procédure de l'état)

```sql
SELECT dpsscc, dart, dind, arfimp, ades, emplacement, quantite_ex, dpqtep, dppick, cmodir, dpobs, palette_group,
       hdexaa, hdexmm, hdexjj, htran, mnom, hnadre, alrs, alrue1, alpost, alvill, c.dndep, j.dlib
FROM exp509 a
JOIN exp510 b (soc, n°) JOIN exp516 c (soc, n°, élément) JOIN exp517 d (soc, n°, élément, ligne)
JOIN prod_entite e ON d.rsoc = e.societe AND d.dpsscc = e.numero_sscc
JOIN spf010 f (article, indice)
LEFT JOIN aa0003 g (client) LEFT JOIN b200f h (transporteur, MTAG ≠ '*')
LEFT JOIN a500f i (adresse HNADRE) LEFT JOIN b100f j (dépôt, DTAG ≠ '*')
WHERE a.rsoc = <soc> AND a.hnobl = <n°> [AND c.dndep = <dépôt>]
ORDER BY c.dndep, emplacement, dpsscc
```

Seules les palettes **choisies** (`EXP517` + `PROD_ENTITE`) apparaissent : un pré-BL « sans palette » ou des lignes de frais ne donnent rien.

### 2.3 Croquis (mm, paysage)

```
y 0   SRS (société)                                                   Le JJ/MM/AAAA à HH:MM   n/N
y 5   Date : jj/mm/aaaa  ║▌▌▌▐▐▌║  Transporteur : 55 MNOM            Livraison : 55 ALRS
y 10                     ║code-  ║                                                ALRUE1
y 15  N° PREBL : 555555  ║barres ║  Dépôt : 555 DLIB                              CP VILLE
y 20 ┌────────┬───────┬─────────┬──────────────┬──────────┬────────┬─────┬───────┬───────┬──────────────┬────────┐
     │N° pal. │Article│Réf. imp.│Désignation   │Emplacem. │Quantité│Multi│Picking│Client │Observations  │N° multi│
     │ 0-18   │18-39  │ 39-60   │ 60-120       │ 120-140  │140-155 │155- │167-182│182-202│ 202-259      │259-277 │
y 28 ├────────┴───────┴─────────┴──────────────┴──────────┴────────┴─────┴───────┴───────┴──────────────┴────────┤
CORPS  SSCC   ART IND  ARFIMP    ADES           E1.E2.E3   reste    OUI   DPQTEP  CMODIR  DPOBS          GROUP
       (lignes suivantes du même SSCC : seule la colonne Picking est remplie)
FIN RUPTURE SSCC                                                         ║▌▌▐▌▐▌▌▐║ code-barres SSCC (x 202, h 9)
FIN RUPTURE DÉPÔT                     Total palettes : Nb
```

L'en-tête existe en double (`DEBUT_DOCUMENT` pour la 1ʳᵉ page, `HAUT_DE_PAGE` pour les suivantes, mêmes positions) ; le dépôt de l'en-tête est celui de la ligne courante.

### 2.4 Zones et calculs

| Colonne | Source | Règle |
| --- | --- | --- |
| Code-barres en-tête | n° pré-BL sur 6 chiffres | 40,9 × 14 mm ; type de symbologie non lisible dans le source *(supposé Code 128 ou 39)* |
| Date | `EXP509.HDEXJJ/MM/AAAA` (date de départ) | « Le … à … » = date / heure d'impression |
| Transporteur / Livraison / Dépôt | `HTRAN` + `B200F.MNOM` ; `HNADRE` + `A500F.ALRS, ALRUE1, ALPOST, ALVILL` ; `EXP516.DNDEP` + `B100F.DLIB` | `ALRUE2` non imprimé |
| N° palette | `EXP517.DPSSCC` | |
| Article / Réf. imprimée / Désignation | `DART DIND`, `SPF010.ARFIMP`, `SPF010.ADES` | |
| Emplacement | `PROD_ENTITE.EMPLACEMENT` découpé en 3 segments séparés par « . » selon `STO005.PSEMP1/2/3` | |
| Quantité | `PROD_ENTITE.QUANTITE_EX − Σ EXP517.DPQTEP` du même SSCC dans les **autres** pré-BL non basculés (`HENBL ≠ 'O'`) | requête par SSCC (N+1) |
| Multi réf. / N° multi | « OUI » si `PROD_ENTITE.PALETTE_GROUP` non blanc ; le groupe | |
| Picking | `EXP517.DPQTEP` si `DPPICK = 'O'` | par ligne |
| Client | `AA0003.CMODIR` (mot directeur) | |
| Observations | `EXP517.DPOBS` | |
| Code-barres SSCC | `DPSSCC` (bloc fin de rupture SSCC) | seulement si `PreBL.Ini [PARAM] ImprimerCodeBarreUiteFeuillePrepa = 1` ; sinon le bloc est masqué |
| Total palettes | compteur par dépôt : +1 par SSCC, **une seule fois par palette groupée** (`PALETTE_GROUP`) | remis à 0 à chaque dépôt |

Ruptures : 1 = `DNDEP`, 2 = `DPSSCC` (blocs `DEBUT_RUPTURE1/2` porteurs d'un libellé technique « Libellé 1/2 » *(supposé masqués ; saut de page au changement de dépôt supposé)*).

### 2.5 ETAT_FeuilleKimmel3 (non appelé)

Même requête et mêmes colonnes ; différences : quantité = `QUANTITE_EX` brute (pas de déduction des autres pré-BL), toutes les lignes du corps remplies, et en fin de rupture SSCC une **ligne récapitulative** de la palette (n°, article, …, picking cumulé `TotDPQTEP`) avec le code-barres **sous** la ligne (y 4,9). Version antérieure *(supposé)* ; à ignorer.

### 2.6 Anomalies à ne pas reproduire

- Requêtes construites par **concaténation** (société, n°, dépôt, SSCC) et requête de cumul par palette.
- Champs mono-ligne étroits : désignation 60 mm (`ADES`), observations 57 mm, client 20 mm ⇒ troncature.
- Dans Kimmel, contrôles `LIB_Quantite1`, `LIB_Picking1`, `LIB_Observations1` (vides) superposés au code-barres SSCC (x 202, même largeur).
- La quantité « reste » dépend de l'état des autres pré-BL et de `PROD_ENTITE` au moment de l'impression (Q5).
- Le choix « un dépôt / tous » dépend d'un paramètre du **poste** (A12 de PreBL).

---

## 3. Feuille camion — ETAT_FeuilleCamion

### 3.1 Appel

Bouton « Feuille camion » : affiche une colonne de cases à cocher (+ « Pour tous ») ; « Suivant » vérifie ≥ 1 pré-BL et **même client, date prévue, transporteur et n° d'adresse** (R28), puis `iAperçu(i100)` + `ETAT_FeuilleCamion` **sans paramètre** : l'état relit directement `FEN_GestionPreBL.TABLE_PreBL` / `COL_Choix` / `COL_NPREBL` pour bâtir la liste `IN (…)`.

### 3.2 Sélection (UNION ALL)

1. **Articles stockés** (`SPF010.ASTK = 'O'`) : `EXP509 ⟕ EXP510 ⟕ EXP516 ⟕ SPF010 ⟕ SPF026 (client + adresse) ⟕ AB001 ⟕ B200F ⟕ AA0003 ⟕ A500F ⟕ B100F ⟕ NEG100 ⟕ EXP517 ⟕ C000F (×2)`, regroupé par ligne de lot (`DLIBPAL`, `DNPAL`, `DNDEP`, `DNPALM`…) :
   - quantité = `Σ EXP517.DPQTEP` (palettes réellement choisies) ; picking = `Σ DPQTEP` où `DPPICK = 'O'` (lu mais non utilisé) ;
   - emballage par = `DEEMBPE` sinon `SPF026.AEMBP` sinon `SPF010.AEMBP` ; UC/palette = `DENPALE` sinon `SPF026.ANPAL` sinon `SPF010.ANPAL` ;
   - type de colis = `C000F.GLIBAB` de la matière UC (`SPF026.AMATUC` prioritaire sur `SPF010.AMATUC`).
2. **Frais** (`ASTK ≠ 'O'` et `SPF010.AOPT7 = 'O'`) : quantité `DQTE`, montant frais = `DNPUS` si `NEG100.TNUM` = 999999999 (« Le tout ») ou 0, sinon `DNPUS × DQTE / TNUM` ; jointure inutile `AA00083` (table `UV`).
3. Tri : n° pré-BL, n° commande, clé, ligne de commande, article, indice, dépôt.

Post-traitement en mémoire : une ligne de lot avec palette mixte (`DNPALM ≠ 0`) donne **deux lignes** si `DLIBPAL` contient « et » (palettes complètes, libellé tronqué avant « et » ; puis la palette mixte « N° `DNPALM` », 1 palette, quantité = incomplets), sinon seulement la ligne mixte. Incomplets = `DNQTPL − emb. × UC/pal × DNPAL`.

### 3.3 Croquis (mm, paysage)

```
y 0   SRS                                                             Le JJ/MM/AAAA à HH:MM   n/N
y 5   Date : jj/mm/aaaa   Transporteur : 55 MNOM          Livraison : 55 ALRS
y 10  Client : 411000 0000006 CNOM                                    ALRUE1
y 15                                                                  CP VILLE
y 20 ┌─────┬───────┬────────┬───────────┬──────┬────┬─────────┬─────┬──────┬───────┬─────┬──────┬─────┬──────┬──────┬───────┬─────┐
     │N°   │Article│Réf.imp.│Désignation│Dépôt │Payé│N° de    │Qté  │Nb pal│Poids  │Nb ex│Nb col│Nb ex│Type  │N° cde│N° cde │Mont.│PU EX
     │PREBL│ 11-31 │ 31-49  │  49-92    │92-112│112-│palettes │136- │148-  │théo.  │/pal │/pal  │/col.│colis │NP    │client │frais│263-
     │0-11 │       │        │           │      │120 │120-136  │148  │160   │160-174│174- │184-  │196- │206-  │220-  │232-247│247- │277
     ├─────┴───────┴────────┴───────────┴──────┴────┴─────────┴─────┴──────┴───────┴─────┴──────┴─────┴──────┴──────┴───────┴─────┴─────┤
CORPS  une ligne par ligne de lot (ou palette mixte), frais compris
FIN PRÉ-BL                                          Totaux :  ΣNbpal  ΣPoids  (Poids saisi : HPDSTT)               ΣFrais
FIN DOCUMENT  y 0..15 : NbPal  libellé dépôt   (4 dépôts au plus, triés par code)
              y n×5   : Cumul palettes                 Cumul poids
```

### 3.4 Zones et calculs

| Colonne | Source / règle |
| --- | --- |
| N° PREBL, Article, Réf. imprimée, Désignation | `HNOBL`, `DART DIND`, `ARFIMP`, `SPF010.ADES` |
| Dépôt | `B100F.DLIN` *(libellé court, supposé)* |
| Payé | `EXP509.HSTOCKP` (O/N) |
| N° de palettes | `EXP516.DLIBPAL` (tronqué avant « et ») ou « N° `DNPALM` » |
| Quantité | ligne normale : `Σ DPQTEP − incomplets` ; palette mixte : incomplets ; **frais : 0** (voir anomalies) |
| Nb palettes | `EXP516.DNPAL` (1 pour la ligne mixte) |
| Poids théorique | `CalcPalExpDepConf = 1` : `PoidsPalette(ini) × DNPAL + ⌈qté / emb.⌉ × APDSU` ; sinon `qté × APDSEX/1000 + EXP005.FPDSP × DNPAL + ⌈qté / emb.⌉` ; palette mixte : poids de palette compté seulement à la 1ʳᵉ occurrence du couple (dépôt, n° mixte) ; 0 si emballage nul |
| Nb ex / pal, Nb colis / pal, Nb ex / colis | `ANPAL × emb.`, `ANPAL`, emb. |
| Type colis | `C000F.GLIBAB` |
| N° cde NP / client | `HNODOS/HCLEF` ; `AB001.CCREFC` |
| Montant frais, PU EX | `MONTANTFRAIS`, `NEG100.DNPUS` — **seulement si** `PreBL.Ini [PARAM] FeuilleCamionValorisee = 1` |
| Totaux pré-BL | palettes (palette mixte comptée une fois par dépôt + n°), poids théorique, « (Poids saisi : `HPDSTT`) » si ≠ 0, total frais |
| Fin de document | palettes par dépôt (**4 premiers dépôts** affichés), cumul palettes, cumul poids (le poids saisi remplace le poids théorique du pré-BL quand il existe) |

Pas de code-barres sur la feuille camion.

### 3.5 Anomalies à ne pas reproduire

- **Total frais imprimé même si la feuille n'est pas valorisée** (`LIB_TotMontant` non conditionné) ; titres « Montant frais / PU EX » toujours présents *(supposé)*.
- **Lignes de frais à quantité 0** : `QTEINCOMP = DQTE − 0` puis quantité = `DQTE − QTEINCOMP`.
- Au-delà de 4 dépôts : détail tronqué mais cumul complet ; position du cumul recalculée (`n × 5`) sans contrôle de place.
- Quantité juste seulement si `DNPAL` = palettes **complètes** (Q6) ; formule « 1 kg par colis » héritée.
- Couplage direct de l'état à la fenêtre (lecture de la table de `FEN_GestionPreBL`) ; SQL concaténé.
- Colonnes étroites (désignation 43 mm, n° de palettes 16 mm pour 25 c., n° cde client 15 mm) ⇒ troncatures.
- Règle de poids différente de l'édition du pré-BL (A8 ci-dessus).

---

## 4. Pistes pour le PDF natif (QuestPDF)

- Un seul calcul de colis / poids (service partagé avec EXP540 R12 et la saisie) au lieu de trois variantes ; paramètres société, pas poste.
- Pré-BL : en-tête répété (`Header`), tableau à colonnes fixes avec lignes de conditionnement **dans la cellule** « Colis » (texte multi-ligne) au lieu d'indicateurs ; code-barres Code 128 du lot rendu dans la cellule désignation ; « A suivre » / « Page n / N » natifs ; pied sur la dernière page uniquement.
- Feuilles : requêtes paramétrées dans `Donnees/`, données passées à l'édition (pas de lecture d'écran), sélection de dépôt optionnelle côté écran, code-barres SSCC (GS1-128 *(supposé)*) et n° de pré-BL.

## 5. Questions ouvertes

1. Titre de l'édition : « Bon de livraison » (actuel) ou « Pré-BL / Bon de préparation » ? Le document part-il avec le camion ?
2. Quels clients ont `AA0003.CENVBL = '2'` (présentation PRINTOR) sur NP11F ? Faut-il garder deux présentations ?
3. Le logo est-il toujours défini dans `PRT003F` pour (soc, enseigne, `BL`) ? Sinon que produit l'overlay vide ?
4. Valeurs réelles des options `EXP005.FEOPT1/2/4`, `FEDART` et de `PreBL.Ini` (`Poidspalette`, `CalcPalExpDepConf`, `ImprimerCodeBarreUiteFeuillePrepa`, `FeuilleCamionValorisee`) chez les utilisateurs.
5. `PROD_ENTITE.QUANTITE_EX` est-elle déjà diminuée lors d'un picking ? (sinon la quantité « reste » de la feuille de préparation est-elle juste ?)
6. `EXP516.DNPAL` compte-t-il la palette incomplète / mixte ? (quantités et palettes de la feuille camion en dépendent.)
7. Symbologie des codes-barres WinDev (n° pré-BL, SSCC) : Code 128, GS1-128 ? Les scanners Kimmel lisent-ils le SSCC à 18 chiffres avec AI `00` ?
8. `ETAT_FeuilleKimmel3` et la variante `ETAT_FeuilleKimmelDepot` (PreBLSansPalette) : faut-il une feuille par lot / quantité pour les sociétés « sans palette » ?
9. Les totaux `HNCOLT/HNPALT/HPDSTT` de l'en-tête doivent-ils primer sur le calcul (comportement actuel) ?
