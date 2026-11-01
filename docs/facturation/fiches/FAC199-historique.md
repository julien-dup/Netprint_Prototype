# FAC199 — Alimentation de l'historique commercial (FAC070, FAC072)

> *Source :* `D:\dvp\sources AS400\NP11\FAC199.rpgle` (3 099 lignes, RPG IV format fixe, octets nuls retirés avant lecture).
> *Date d'analyse :* 2026-11-01. *Le source fait foi* ; la fiche générale est `retrodoc-saas\fiches\FAC199.md` (et `FAC201.md`, même logique).
> Les numéros de ligne sont ceux du source. Les longueurs de colonnes viennent de `cartographie-db2\colonnes.csv`.
> Notation : `tronc2(x)` = troncature à 2 décimales vers zéro (MULT / DIV / ADD sans `(H)` dans une zone à 2 décimales) ; jamais d'arrondi dans cette partie du programme.

## 1. Déclenchement et boucles

### 1.1 Condition d'écriture

FAC070 et FAC072 ne sont écrits **qu'en édition définitive facturante** : indicateur 11 (l. 708-714) allumé si
`PTYPE = ' '` (pas brouillon) **et** `FDEDT ≠ 'O'` (pas encore éditée) **et** `FDEDIT = 'O'` (à éditer).
`SPTOT8` (qui écrit l'historique) n'est appelé que sous l'indicateur 11 (l. 1058 `C 11 EXSR SPTOT8`).
FAC199 traite **une seule facture** par appel (`CHAIN FAC080` l. 609, puis `LR`) : aucun report de valeur d'une facture à l'autre n'est possible dans un même appel.

### 1.2 Imbrication

```
CHAIN FAC080 (PSOC, PNFACD, année 4 ch., PNMOIS, PNJOUR)          l. 609
SPDET9 : AA0007, STO005, FAC010, AA000Z (coefficients), FAC120, FAC020   l. 624-659
SPDET  (une fois pour la facture)                                  l. 690-1212
  remise à zéro CA, RISTOU ← AA0003 du client facturé              l. 693, 745-759
  boucle FAC090 (éléments, clé PSOC, RNFAC, FDANNE, FDMOIS, FDJOUR)  l. 865-1059
    SPDET8 : RAZ des cumuls de l'élément et du tableau CA           l. 663-686
    MOVE RCLE → ZCLE  (photo de l'élément courant)                  l. 872
    boucle tant que même élément (RL8 = ZL8)                        l. 874-972
      lecture des lignes FAC100 de l'élément → LIGNE pour chacune   l. 890-894
        (SpPres1 si FDPRES ≠ '2' ; SpPres2 sinon, ordre des BL)
      calcul COMMIS / HONORA de l'élément → MECOMM / MEHONO         l. 897-934
      READE FAC090 (élément suivant)                                l. 971
    SPab011 (textes)                                                l. 1057
    si 11 : SPTOT8  → FAC072 (HISNEG, 0..300 fois) puis FAC070 (HISTO, 1 fois)   l. 1058
```

Point clé : quand `SPTOT8` s'exécute, le `READE` de la l. 971 a **déjà chargé l'élément suivant** dans les zones FE* (DS `RCLE`). Tout ce qui concerne l'élément terminé doit donc être lu dans les zones **ZE*** (DS `ZCLE`, copie faite l. 872). HISTO et HISNEG utilisent bien ZE* pour les zones de l'élément ; `FETYP` n'est jamais utilisé dans SPTOT8. Les zones FD* (en-tête FAC080) sont communes à toute la facture.

### 1.3 Ce qui est écrit

| Fichier | Quand | Combien |
|---|---|---|
| **FAC070** | Chaque élément FAC090 de la facture, **quel que soit son type** (1, 2, 3, 4…), même sans ligne | 1 ligne par élément (pas par exemplaire), l. 2258, écrite **après** les FAC072 de l'élément |
| **FAC072** | Éléments de type `ZETYP` = 2 ou 3 uniquement (l. 1253-1254), un enregistrement par **poste du tableau CA** retenu | 0 à 300 par élément, l. 2449 |

### 1.4 Le tableau CA (DSCA) : un poste par ligne de vente, pas de cumul par article

`CA` : 300 postes de 112 caractères (l. 95). Chaque poste est lu / écrit au travers de la DS `DSCA` (l. 224-240, zones étendues = numériques étendus) :

| Sous-zone | Pos. | Format | Alimentée par (SPCA l. 1685-1711) | Sert à |
|---|---|---|---|---|
| TNELT | 1-2 | 2,0 | FGNEL | (non utilisée pour l'historique) |
| TART | 3-17 | 15 | FGART | HNART, clés SPF010 / NEG100 |
| Tind | 18-19 | 2 | FGIND | HNIND, clés |
| TQTE | 20-27 | 8,0 | FGQTE | HNQTE |
| TMTHTF | 28-36 | 9,2 | FGMTNA (montant autre monnaie) | filtre seulement |
| CADOSS | 37-41 | 5,0 | RDOSS (= FEDOSS de l'élément, posé l. 667) | RDOSS de FAC072, clé NEG100 |
| CADCLE | 42 | 1 | CCDCLE (= FECLE, l. 668) | RDCLE de FAC072, clé NEG100 |
| CATXRE | 43-44 | **2,0** | 0, puis cumul des FGPER par SPRRR | HNTXRE |
| TDES | 45-84 | 40 | FGDES **si FGART ≠ blanc**, sinon blanc (l. 1688-1692) | HNDES |
| TTXCO | 85-88 | 4,2 | FGTCOL (% de commission négoce de la ligne) | HNTXRP |
| TMTHTE | 89-97 | 9,2 | FGMTN (montant HT euro) | HNTTHT |
| CALIGD | 98-100 | 3,0 | FGLIGD (n° de ligne dossier) | clé NEG100 |
| TBL / TBLEL / TBLLG | 101-112 | 6,0 / 3,0 / 3,0 | FGBL / FGBLEL / FGBLLG | HNBL / HNBLEL / HNBLLG |

Alimentation (LIGNE, l. 1546-1674), pour chaque ligne FAC100 de l'élément :
- `FAC020` est relu par (RSOC, FGCDL) ; **s'il est absent, la ligne est ignorée en entier** (`GOTO FLIGNE`, l. 1550-1551 : ni cumul, ni CA, ni impression).
- Si `FETYP` = 2 ou 3 (élément courant, l. 1650-1651) :
  - `LTYPE = 'V'` → **SPCA** : recherche du premier poste égal à `VIDE` (112 × '0') par `LOOKUP` (l. 1682-1683), et copie de la ligne dans ce poste. **Un poste par ligne de vente**, aucun regroupement par article ; ordre = ordre de lecture FAC100 (clé de FAC100, ou ordre BL de FAC100L3 en présentation 2).
  - `LTYPE = 'R'` **et** `FGPER ≠ 0` → **SPRRR** (voir 1.5).
- Le tableau est remis à `*ALL'0'` à chaque élément (SPDET8 l. 682) et en début de facture (SPDET l. 693).

### 1.5 Remise en pourcentage (SPRRR, l. 1719-1733)

Pour **chacun des 300 postes** (occupés **et vides**) :
```
TAUX   = (100 - FGPER) / 100          // TAUX 4,2 ; FGPER est 2,0 entier → exact
TMTHTF = tronc2(TMTHTF × TAUX)
TMTHTE = tronc2(TMTHTE × TAUX)
CATXRE = CATXRE + FGPER               // zone 2,0 : cumul ADDITIF des %, pas composé
```
Conséquences :
- La remise ne touche que les lignes de vente **déjà lues** dans l'élément (placées avant la ligne R).
- Plusieurs remises : les montants sont composés (× 0,9 × 0,9) mais HNTXRE est additionné (10 + 10 = 20).
- **Piège majeur** : les postes vides reçoivent aussi `CATXRE += FGPER` ; ils ne sont plus égaux à `VIDE`. Après une ligne R à pourcentage, **le LOOKUP de SPCA ne trouve plus aucun poste libre** : toutes les lignes de vente suivantes du même élément sont **absentes de FAC072** (elles restent dans les totaux et dans FAC070). Ces postes vides « tamponnés » ne produisent pas de ligne (filtre l. 1259-1263, montants et quantité nuls).
- Débordement : CATXRE est sur 2 chiffres ; un cumul ≥ 100 est tronqué (110 → 10 ; 100 → 00, ce qui « libère » à nouveau les postes vides).
- Une remise **en montant** (`R` avec FGPER = 0) n'est pas répartie sur les postes : elle baisse HFTTHT (FAC070) mais pas les HNTTHT (FAC072).

### 1.6 Sélection des postes écrits dans FAC072 (SPTOT8, l. 1253-1348)

```
si ZETYP = 3 ou 2
  pour I = 1 à 300
    DSCA ← CA(I)
    si TMTHTF ≠ 0 ou TMTHTE ≠ 0 ou TQTE > 0          (l. 1259-1263 : lignes gratuites à qté > 0 incluses)
      si TART ≠ '999999999999999'                     (article codé)
        CHAIN SPF010L2 (PSOC, TART, Tind) ; CHAIN SPF022 (PSOC, TART, Tind, RGEN, RCLI) sans indicateur
        si SPF010 trouvé                               (l. 1271)
          CHAIN NEG100 (PSOC, CADOSS, CADCLE, CALIGD, TART, Tind) → si trouvé : DNQTEF ± TQTE, UPDATE
          [calcul MARGE / PRNEG : code mort, voir § 5]
          HISNEG → WRITE FAC072
        sinon : RIEN (pas de FAC072)
      sinon (article « non codé » tout à 9)
        HISNEG → WRITE FAC072                          (l. 1339)
```
- Article à blanc (ligne de vente sans code article) : CHAIN SPF010 sur une clé à blanc → normalement non trouvé → **pas de FAC072**.
- Article codé absent de SPF010L2 (fichier des articles négoce) → **pas de FAC072**, et DNQTEF non mis à jour.
- Ligne à quantité négative et montant nul → ignorée.
- Les `Z-SUB` de TMTHTF / TMTHTE / MARGE « si avoir » (l. 1330-1334, 1341-1344) sont faits **après** HISNEG et sur DSCA **qui n'est jamais recopiée dans CA** : sans effet (code mort).

## 2. FAC070 — une ligne par élément (HISTO, l. 2103-2258)

Toutes les zones sont posées dans HISTO juste avant `WRITE FAC070F1` (l. 2258), sauf mention.

| Colonne (format) | Valeur exacte | Provenance / conditions | Lignes |
|---|---|---|---|
| RSOC (4,0) | Société de la facture | Non affectée dans HISTO : zone partagée avec FAC090 (DS RCLE pos. 1-4) ; contient la société de l'élément suivant lu par READE, identique (même clé) | 169, 971 |
| RDOSS (5,0) | `ZEDOSS` | FAC090.FEDOSS de l'élément | 2105 |
| RDCLE (1) | `ZECLE` | FAC090.FECLE | 2106 |
| RNFAC (6,0) | N° facture | Non affectée : zone RNFAC de la DS RCLE (FAC090), même facture | 170 |
| HFCAT (2) | `ZECAT` | FAC090.FECAT | 2107 |
| HFSCAT (2) | `ZESCAT` | FAC090.FESCAT | 2108 |
| HFDTFA / HFDTFM / HFDTFJ | `ZEANNE` / `ZEMOIS` / `ZEJOUR` | Date de facture portée par FAC090 (clé) | 2109-2111 |
| HFFA (1) | `FDTYPE` | FAC080 : 'F' facture, 'A' avoir | 2113 |
| HFENS (1) | `FDENS` | FAC080 (enseigne) | 2114 |
| HFTYVE (2) | `FDENS` + `FDVEN` | MOVEL FDENS puis MOVE FDVEN (1 car. chacun) : **enseigne en 1re position, code ligne de vente en 2e** | 2115-2116 |
| HFREPR (3,0) | `FDREP` | Représentant de l'en-tête FAC080 (pas celui du dossier) | 2117 |
| HFQTE (8,0) | `ZEQTE` | FAC090.FEQTE (qté facturée de référence) ; non signée pour un avoir | 2118 |
| HFTXRP (4,2) | `ZETXC` | FAC090.FETXC, pour tous les types | 2119 |
| HFMTRP (7,2) | `MECOMM` | Voir 2.2. Types 2/3 : Σ HNMTRP des FAC072 écrits ; autres types : COMMIS. **Débordement** si ≥ 100 000 (zone 7,2 alimentée depuis 10,2) | 2120 |
| HFNMHO (12) | `ZENOM` | FAC090.FENOM | 2121 |
| HFTXHO (4,2) | `ZETXO` | FAC090.FETXO | 2122 |
| HFMTHO (9,2) | `MEHONO` | = HONORA de l'élément (2.2) | 2123 |
| HFTTHT (9,2) | si `FDESC ≠ 0` : `tronc2(Z156 × MEHTE)` avec `Z156 = 1 − FDESC/100` (15,6, exact) ; sinon `MEHTE` | **HT net d'escompte** de l'élément. MEHTE = Σ FGMTN des lignes V − Σ FGMTN des lignes R (2.1) | 2125-2141 |
| HFTTPT (9,2) | `MEPORE` | Σ FGMTN des lignes `LTYPE='P'` (port taxable), sans escompte | 2144 |
| HFTTPE (9,2) | `MEEXOE` | Σ FGMTN des lignes `LTYPE='E'` (port exonéré), sans escompte | 2145 |
| HFPRFM (4,2) | 0 | Toujours zéro | 2151 |
| HFTTFM (9,2) | 0 | Toujours zéro | 2152 |
| HFPRFG (4,2) | 0, puis `CKFG1` si `CFTYPE = 'B'` | AA000Z (clé société). **Tous types d'élément** (le bloc est hors du test ZETYP = 2) | 2154, 2183-2184 |
| HFTTFG (9,2) | 0, puis si `CFTYPE = 'B'` : `tronc2(tronc2(MEHTE × CKFG1) / 100)` | Base MEHTE **brute** (avant escompte) ; tous types | 2155, 2187, 2191 |
| HFTTFS (9,2) | 0, puis si `ZETYP = 2` : `tronc2(tronc2(MEHTE × CFRSTO) / 100)` | AA000Z.CFRSTO (frais de stockage %) | 2157, 2161, 2175-2179 |
| HFTTFF (9,2) | 0, puis si `ZETYP = 2` **et** `CCFACT ∉ {'T','2','3'}` : `tronc2(tronc2(MEHTE × CFRFIN) / 100)` | AA000Z.CFRFIN (frais financiers %) ; AB001.CCFACT du dossier (voir § 5 pour l'enregistrement AB001 réellement en mémoire) | 2156, 2163-2172 |
| HFTTCR (9,2) | Type 3 : 0. Autres types : selon NBRE (2.3) `tronc2(tronc2(MEHTE × CKMAx) / 100)`, 0 si NBRE < 45 | AA000Z.CKMA3/4/5 | 2194-2196, 2202-2233 |
| HFTTRI (9,2) | Type 3 : 0. Autres types : `tronc2(tronc2(MEHTE × RISTOU) / 100)` | RISTOU = AA0003.CRIST du **client facturé** (FDGENF, FDCLIF), 0 si client absent. **Indépendant de NBRE** | 2197, 2236-2240, 745-759 |
| HFGEN (6,0) / HFCLI (7,0) | `FDGEND` / `FDCLID` | Client donneur d'ordre (FAC080) | 2244-2245 |
| HFPRPF (9,2) | 0 | Toujours zéro (posé l. 2158 et l. 2195) | 2158, 2195 |
| HFORTC (3) | `ZEORTE` | FAC090.FEORTE | 2250 |
| HFNACA (1) | `ZETYP` en caractère ('1'…'4') | FAC090.FETYP | 2251 |
| HFFDEF (1) | 'O' si `ZEFAA = 'D'`, sinon 'N' | FAC090.FEFAA | 2253-2257 |
| HFTAG (1) | **jamais affectée** → blanc | — | — |
| HFNUM (3,0) | `ZENUM` | FAC090.FENUM | 2112 |
| HFGENF (6,0) / HFCLIF (7,0) | `FDGENF` / `FDCLIF` | Client facturé (FAC080) | 2247-2248 |

Après l'écriture : si `ZEFAA = 'D'` et dossier renseigné, appel `TRTMSG` (notification) avec PAR13 (dossier, 'FACT', date, n° facture, `ZEQTE`) (l. 2262-2353). Aucune écriture AB0019 (code en commentaire).

### 2.1 Cumuls de l'élément (remis à zéro dans SPDET8, l. 671-680, à chaque élément)

Dans LIGNE (l. 1559-1637), selon `LTYPE` de FAC020 (clé RSOC + FGCDL) :

| LTYPE | MEHTE (euro, FGMTN) | MEPORE | MEEXOE | (MEHTF… = mêmes cumuls sur FGMTNA) |
|---|---|---|---|---|
| V (vente) | + FGMTN | | | |
| R (remise) | − FGMTN (FGMTN stocké positif ; il est négativé après coup pour l'impression, l. 1662-1665) | | | |
| P (port taxable) | | + FGMTN | | |
| E (port exonéré) | | | + FGMTN | |
| autres (T…) | — | — | — | |

Toutes les zones `ME*` sont en 10,2. Les montants FAC070 sont **toujours pris en euro** (FGMTN), quelle que soit la devise `FDCDEV`.

### 2.2 Commission et honoraires de l'élément (l. 897-934, élément courant FE*)

```
COMMIS = 0
si FETXC ≠ 0 et FETYP ≠ 3 et FETYP ≠ 2 :
    k      = 1 + tronc2(−FDESC / 100)            // COMMIS est 10,2 : FDESC = 1,5 → −0,015 → −0,01 → k = 0,99 (et non 0,985)
    base   = MEHTE si FDCDEV = 'EUR', sinon MEHTF
    COMMIS = tronc2( tronc2( tronc2(k × base) × FETXC ) / 100 )
si FETXO ≠ 0 :
    HONORA = même formule avec FETXO (k tronqué de la même façon), tous types d'élément
sinon :
    HONORA = FEMTO                                // montant d'honoraire saisi sur l'élément
MECOMM += COMMIS ; MEHONO += HONORA
```
Puis, pour les types 2/3, chaque HISNEG ajoute `HNMTRP` à MECOMM (l. 2451). Comme COMMIS = 0 pour ces types, **HFMTRP = Σ HNMTRP des FAC072 réellement écrits** ; pour les autres types, HFMTRP = COMMIS.

Attention : la commission des types 2/3 est calculée sur le HT **net d'escompte exact** (Z156 en 15,6), celle des autres types sur un coefficient d'escompte **tronqué à 2 décimales** (incohérence d'origine, à reproduire si l'on veut l'identique).

### 2.3 Nombre de jours et majoration pour conditions de règlement (l. 2202-2233)

```
si FDNECH ≠ 0 : NBRE = FDNECH                        (FAC080, « nb jours échéance »)
sinon        : CALL PRTDIFD(DATECH, DATFAC, TNBRE, RET) ; NBRE = TNBRE
HFTTCR = 0
NBRE ≥ 120          → HFTTCR = MEHTE × CKMA5
NBRE ≥ 90           → HFTTCR = MEHTE × CKMA4
NBRE ≥ 60 ou ≥ 45   → HFTTCR = MEHTE × CKMA3          (donc NBRE ≥ 45)
HFTTCR = tronc2(HFTTCR / 100)
```
- `FDNLE` (« le » jour) et `FDART` (arrêté au) sont ignorés : seul FDNECH compte.
- **PRTDIFD** (CL `PRTDIFD.clp`) : `Pgm Parm(&Date1 &Date2 &Diff &Err)`, dates attendues en **CHAR(6) AAMMJJ**, `&Diff` = `jour julien(Date2) − jour julien(Date1)` (années 19xx si AA > 34). FAC199 lui passe `DATECH` et `DATFAC`, deux zones **8,0 étendues AAAAMMJJ** :
  1. le CL ne lit que les 6 premiers octets, « AAAAMM » est lu comme « AAMMJJ » : pour 2026, mois = 26 → `CVTDAT` en erreur → `MONMSG CPF0000` → `&Err = '9'`, **&Diff non modifié** → `TNBRE` reste à 0 (valeur initiale) ;
  2. même si les dates étaient valides, le résultat serait `DATFAC − DATECH` (négatif pour une échéance future).
  3. `DATECH` = échéance **lue dans FAC080 avant traitement** (renommage `FDECHA→WDECHA`… l. 401-404, DS l. 242-246) ; elle vaut 0 si l'échéance n'était pas saisie (l'échéance calculée par GETECHEAN va dans une autre DS, `ECHEANCE`, l. 248-252).

  **Comportement réel aujourd'hui : quand FDNECH = 0, NBRE = 0 et HFTTCR = 0.**
- Coefficients `CKMA3`, `CKMA4`, `CKMA5` : AA000Z (4,2), lus par société dans SPDET9 (l. 639-644, remis à 0 avant le CHAIN).
- La ristourne (HFTTRI) ne dépend **pas** de NBRE : c'est `MEHTE × CRIST / 100` pour tous les types sauf 3.

## 3. FAC072 — une ligne par poste CA retenu (HISNEG, l. 2380-2451)

| Colonne (format) | Valeur exacte | Provenance / conditions | Lignes |
|---|---|---|---|
| RSOC (4,0) | Société | Non affectée (zone de la DS RCLE / FAC090) | 169 |
| RDOSS (5,0) | `CADOSS` | FEDOSS de l'élément au moment de SPCA | 2382 |
| RDCLE (1) | `CADCLE` | FECLE de l'élément | 2383 |
| RNFAC (6,0) | N° facture | Non affectée (DS RCLE) | 170 |
| HNART (15) | `TART` | FAC100.FGART | 2384 |
| HNDES (40) | `TDES` | FAC100.FGDES **si FGART non blanc**, sinon blanc. En pratique : désignation renseignée pour tout article écrit (codé ou « tout à 9 ») | 2386, 1688-1692 |
| HNDTFA / HNDTFM / HNDTFJ | `ZEANNE` / `ZEMOIS` / `ZEJOUR` | Date facture (FAC090) | 2387-2389 |
| HNFA (1) | `FDTYPE` | FAC080 | 2391 |
| HNENS (1) | `FDENS` | FAC080 | 2392 |
| HNTYVE (2) | `FDENS` + `FDVEN` | comme HFTYVE | 2393-2394 |
| HNREPR (3,0) | `FDREP` | FAC080 | 2395 |
| HNQTE (8,0) | `TQTE` | FAC100.FGQTE de la ligne (non signé pour un avoir) | 2396 |
| HNTXRP (4,2) | `TTXCO` | FAC100.FGTCOL (% de commission négoce de la ligne) | 2397 |
| HNMTRP (7,2) | `tronc2( (HNTXRP × HNTTHT) / 100 )` via `z114` 11,4 (produit exact) | Calculé sur HNTTHT **net d'escompte et de remise %** ; débordement si ≥ 100 000 | 2420-2421 |
| HNTXRE (4,2) | `CATXRE` | Σ des FGPER (entiers) des lignes R à % lues **après** la ligne dans l'élément ; toujours entier ; 0 si aucune | 2398 |
| HNTTHT (9,2) | si `FDESC ≠ 0` : `tronc2(Z156 × TMTHTE)` avec `Z156 = 1 − FDESC/100` (15,6) ; sinon `TMTHTE` | TMTHTE = FGMTN de la ligne, éventuellement réduit par SPRRR (tronqué à chaque remise) | 2400-2418 |
| HNPUS (9,2) | Article codé : `DNPUS` ; article tout à 9 : `HNTTHT` (**montant total, pas un prix unitaire**) | NEG100 lu par (PSOC, CADOSS, CADCLE, CALIGD, TART, Tind) l. 1274 ; voir report § 5 | 2423-2441 |
| HNPUT (9,2) | Article codé : `DNPUT` ; article tout à 9 : `HNTTHT` | idem | 2435, 2440 |
| HNGEN (6,0) / HNCLI (7,0) | `FDGEND` / `FDCLID` | Donneur d'ordre (pas le client facturé) | 2443-2444 |
| HNPRPF (9,2) | 0 | Article codé : 0 explicite ; article tout à 9 : non affectée, donc 0 (seule valeur jamais écrite). **Le prix de revient (MARGE) n'est jamais stocké** | 2424 |
| HNIND (2) | `Tind` | FAC100.FGIND | 2385 |
| HNELM (2,0) | `ZENUM` | N° d'élément FAC090 | 2390 |
| HNBL (6,0) | `TBL` | FAC100.FGBL | 2445 |
| HNBLEL (3,0) | `TBLEL` | FAC100.FGBLEL | 2446 |
| HNBLLG (3,0) | `TBLLG` | FAC100.FGBLLG | 2447 |

Après chaque écriture : `MECOMM += HNMTRP` (l. 2451) → HFMTRP de FAC070.

## 4. Paramètres et tables lues (avec clés)

| Table (format) | Clé | Lu où | Zones utiles à l'historique |
|---|---|---|---|
| FAC080 (FAC080F1) | PSOC, PNFACD, année 4 ch., PNMOIS, PNJOUR | l. 609 | FDTYPE, FDENS, FDVEN, FDREP, FDESC (2,1), FDNECH, FDGEND/FDCLID, FDGENF/FDCLIF, FDCDEV, FDEDT, FDEDIT, FDPRES ; échéance → DATECH |
| FAC090L7 (FAC090F1) | PSOC, RNFAC, FDANNE, FDMOIS, FDJOUR (READE) | l. 865-971 | FEDOSS, FECLE, FECAT, FESCAT, FETYP, FENUM, FEQTE, FETXC, FETXO, FENOM, FEMTO, FEFAA, FEORTE (via ZCLE) |
| FAC100 (FAC100F1) / FAC100L3 (préfixe P2_) | PSOC, RNFAC, FEANNE, FEMOIS, FEJOUR, FENUM | l. 1391-1456 | FGCDL, FGQTE, FGDES, FGPER (2,0), FGART, FGIND, FGMTN, FGMTNA, FGTCOL, FGLIGD, FGBL, FGBLEL, FGBLLG |
| FAC020 | RSOC, FGCDL | l. 1550 | LTYPE (V, R, P, E…) |
| AA000Z | PSOC | SPDET9 l. 644 | CKMA3/4/5 (4,2), CFRSTO (4,2), CFRFIN (4,2), CKFG1 (4,2), CFTYPE (1, « type coef ») |
| AA0003 | PSOC, FDGENF, FDCLIF (client facturé) | l. 757 | CRIST → RISTOU (4,2) |
| AB001 | PSOC, ZEDOSS, ZECLE (l. 1231) puis PSOC, CADOSS, CADCLE (l. 1291) | SPTOT8 | CCFACT (frais financiers), CCTYPC (code mort) ; mise à jour CCNOFA/CCFCT*, CCQTEP |
| SPF010L2 | PSOC, TART, Tind | l. 1269 | existence seulement : conditionne l'écriture FAC072 |
| SPF022 | PSOC, TART, Tind, RGEN, RCLI | l. 1270 | rien pour l'historique (pas d'indicateur) |
| NEG100 | PSOC, CADOSS, CADCLE, CALIGD, TART, Tind | l. 1274 | DNPUS, DNPUT → HNPUS / HNPUT ; mise à jour DNQTEF ± TQTE |
| AA0008L1 (AA0083F1) | PSOC, 'UV', DNUV | l. 1288 | TNUM : code mort (MARGE) |
| STO005 | PSOC | l. 632 | PSTSNE / PSTSP1-3 → PRTSTO : code mort |
| AK00L2 (AK00F) | PSOC, PRTSTO, DNART, DNIND | l. 1305 | PRPUMP : code mort |
| PRTDIFD (CL) | — | l. 2203 | voir 2.3 (inopérant) |

## 5. Anomalies, reports de valeurs, zones mortes

1. **Lignes de vente perdues après une remise %** (§ 1.5) : SPRRR modifie aussi les postes vides (CATXRE), plus aucun poste libre ensuite. Les lignes V lues après la 1re ligne R à pourcentage d'un élément n'ont pas de FAC072, alors qu'elles sont dans HFTTHT. Σ HNTTHT ≠ HFTTHT dès qu'il y a une remise (en % placée avant des ventes, ou en montant).
2. **HNTXRE additif** alors que la remise est composée ; zone 2,0 qui déborde au-delà de 99.
3. **Pas de signe pour les avoirs** dans FAC070 / FAC072 : les montants et quantités gardent le signe stocké dans FAC090 / FAC100, l'avoir n'est repérable que par HFFA / HNFA = 'A'. Les `Z-SUB` « si avoir » (l. 1330-1344) sont du code mort (après HISNEG, sur DSCA non recopiée). La fiche `FAC201.md` (R16, « montants négatifs pour un avoir ») est à corriger : FAC201 a la même structure (HISNEG l. 1368/1379 avant les Z-SUB l. 1372-1383).
4. **Report de HNPUS / HNPUT** : si la ligne NEG100 n'est pas trouvée (l. 1274, indicateur 33), DNPUS / DNPUT gardent les valeurs du **dernier NEG100 lu** (poste précédent du tableau, ou dernière ligne de vente lue par SPAPL / SPNORM pendant la lecture des lignes). FAC072 reçoit alors les prix d'un autre article.
5. **HNPUS / HNPUT d'un article « tout à 9 » = montant HT total de la ligne**, pas un prix unitaire.
6. **CCFACT potentiellement d'un autre dossier** : si AB001 n'est pas trouvé (dossier de l'élément absent), le CHAIN échoué laisse en mémoire l'AB001 précédemment lu (élément précédent) → HFTTFF calculé avec le CCFACT d'un autre dossier. Même chose pour CCTYPC (code mort).
7. **UPDATE AB001 sans lecture** : l. 1233 le `IF` est conditionné par `N30` ; si le CHAIN l. 1231 échoue, l'IF est sauté mais l'`UPDATE AB001F1` l. 1248 est exécuté → erreur d'exécution (mise à jour sans lecture préalable). Survient si un élément porte un dossier inexistant.
8. **PRTDIFD inopérant** (§ 2.3) : dates 8 chiffres passées à un CL qui attend 6 caractères AAMMJJ, ordre des dates inversé. Sans FDNECH, pas de majoration (HFTTCR = 0).
9. **Escompte tronqué** dans COMMIS / HONORA (k = 1 + tronc2(−FDESC/100)) alors que HFTTHT / HNTTHT utilisent un coefficient exact.
10. **Bases incohérentes** : HFTTHT et les commissions des types 2/3 sont nets d'escompte ; HFTTFS, HFTTFF, HFTTFG, HFTTCR, HFTTRI sont calculés sur MEHTE **brut** ; HFTTPT / HFTTPE (ports) jamais escomptés. FAC201 calcule ces frais sur HFTTHT (net) : divergence déjà signalée dans `FAC199.md`.
11. **Zones toujours à zéro / blanc** : HFPRFM, HFTTFM, HFPRPF, HNPRPF, HFTAG. Pour le type 3 : HFTTCR, HFTTRI. HFTTFS / HFTTFF seulement pour le type 2.
12. **Code mort** : MARGE, PRNEG, TNUM (y compris l'exclusion « Le tout » 999999999), PRTSTO, PRPUMP, lectures AA0083 / STO005 / AK00F (l. 1283-1318). Le prix de revient n'arrive dans aucune colonne.
13. **Présentation 2 (FDPRES = '2')** : SpPres2 ne recopie pas `P2_FGMTNA` dans FGMTNA (l. 1429-1448) ; TMTHTF peut donc contenir un montant d'une autre ligne. Sans effet sur les montants FAC070 / FAC072 (euro), mais peut faire passer le filtre l. 1259 pour une ligne à montant euro nul et quantité ≤ 0.
14. **Débordements silencieux** : HFMTRP / HNMTRP (7,2) dès 100 000 ; HFTTHT (9,2) alimenté depuis MEHTE (10,2) dès 10 000 000 ; produits intermédiaires MEHTE × taux en 9,2 (dès MEHTE × taux ≥ 10 000 000).
15. Articles codés absents de SPF010L2 et lignes de vente sans code article : pas de FAC072 (filtre silencieux).

## 6. Questions ouvertes

1. FAC100 / FAC090 d'un **avoir** stockent-ils des montants et quantités positifs (signe porté par FDTYPE) ? Si oui, l'historique d'un avoir est positif avec HFFA = 'A' : les consommateurs (statistiques, CA) doivent appliquer le signe. À vérifier sur un avoir existant de NP11F.
2. Faut-il reproduire la perte des lignes de vente postérieures à une remise % (anomalie 1) ou écrire toutes les lignes ? Idem pour HNTXRE additif.
3. CFTYPE = 'B' (« frais généraux méthode B ») : quelles sociétés l'ont ? HFPRFG / HFTTFG sont aussi posés sur les éléments de type 1, 3, 4 : voulu ?
4. CCFACT ∉ {T, 2, 3} pour les frais financiers : signification exacte des valeurs ('1' = facturation à la livraison ?) à confirmer dans le glossaire.
5. Majoration conditions de règlement : faut-il corriger PRTDIFD (jours entre facture et échéance quand FDNECH = 0) ou conserver « pas de majoration » ?
6. Qui lit FAC070 / FAC072 (statistiques commerciales, commissions représentants) ? Vérifier dans `carto_tables.csv` avant de choisir de reproduire ou corriger les anomalies (débordements, HNPUS des articles tout à 9).
7. Le prix de revient (HFPRPF / HNPRPF) est toujours à zéro : un autre programme le complète-t-il plus tard (batch de marge) ?
