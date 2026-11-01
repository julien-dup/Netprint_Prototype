# FAC402P — ce qu'écrit la génération définitive des factures automatiques

> **But** : réécrire FAC402P en C# en produisant **le même contenu** dans les tables. Cette fiche complète
> `D:\docs\Netprint\retrodoc-saas\fiches\FAC402P.md`, `FAC401.md`, `CHAINE-FACTURATION-AUTO.md` et `AXISRV.md` sans les recopier.
> **Sources lues** (01/11/2026) : `D:\dvp\sources AS400\NP11\` FAC402P.rpgle, FAC401.rpgle, FAC400CLP.clp, FAC412.rpgle,
> AXISRV.rpgle, TFAC400.pf, TFAC401.pf, TFAC401L1 à L7.lf, FAC402PR.dds, DDS des tables écrites ; libellés : `cartographie-db2\colonnes.csv`.
> **Numéros de ligne** : FAC402P sauf mention (« FAC401 l. », « AXISRV l. », « FAC412 l. »).
> Aucune requête sur la base n'a été faite : ce qui suit vient uniquement des sources.

---

## 0. En bref (ce qui compte pour l'implémentation)

1. **Une facture = un en-tête FAC080 + n éléments FAC090 + des lignes FAC100 + les textes de pied FAC081.** Les montants
   d'en-tête (FDMTN, FDTVA, ports, escompte) sont **laissés à 0** : ils sont calculés plus tard par FAC201 (édition définitive,
   déjà repris en C# dans `CalculFacture`).
2. **Le regroupement dépend du mode** (TFAC400.TMODEF). Voir § 2 : trois modes ne se comportent pas comme leur description
   (mode 5 jamais traité, mode 7 non découpé par article, mode 6 avec des lignes par BL).
3. **Numérotation** : GETFACTURE en définitif, compteur FAC040 (par société / année / mois) ou PRT320F.ENDNFA (par enseigne),
   lu avec verrou et incrémenté immédiatement, **sans transaction**.
4. **Facture ou avoir** : BL d'état R ⇒ avoir (FDTYPE = A), quantités FAC100 négatives.
5. **Marquage** : EXP010 (HBLFAC = O, n° et date de facture, n° d'élément), EXP011 et AB004 (facturé = O, n°, élément, date).
6. **« À contrôler »** (FDEDIT = A) : règle du § 6, avec des effets de bord (tous les avoirs ; seul le dernier élément compte
   en modes 4 à 6).

---

## 1. Entrées : TFAC400 / TFAC401 écrits par FAC401

### 1.1 Sélection amont (FAC400CLP, OPNQRYF)

Jointure EXP009L1 (en-tête BL) × EXP010L1 (élément de BL) × AB001 (dossier), clé RSOC / HNOBL / HNUM, sélection :
société = PAR1, enseigne EXP009.HENS = PAR1, date d'expédition (HDEXAA/MM/JJ) dans la période, n° de BL dans la plage,
client revendeur HGENE/HCLIE dans la plage, **HETABL = C ou R**, **HFACTU = O**, **HNOFAC = 0**
(FAC400CLP, `CompoRqs` et `QQQQRY01`). Les années de la période sur 2 chiffres : > 34 ⇒ 19xx, sinon 20xx.

### 1.2 Filtres et choix faits par FAC401 (par élément de BL lu)

| Étape | Règle exacte | Ligne |
|---|---|---|
| Hors facturation auto | EXP010.HFACTO ≠ O ⇒ écarté et listé sur FAC401PR | FAC401 l. 159-171 |
| Dossier absent | AB001 introuvable (RSOC, HNODOS, HCLEF) ⇒ écarté **sans** être listé | FAC401 l. 173, 176 |
| Mode du donneur d'ordre | AA0003(PSOC, HGENE, HCLIE).CDOPT2 < '1' ou > '8' ⇒ écarté et listé | FAC401 l. 179-192 |
| Client « des conditions » (WGEN/WCLI) | AB001FA absent, **ou** présent avec FACRC = '1' ⇒ donneur d'ordre (HGENE/HCLIE) ; sinon client facturé du BL (HGENF/HCLIF) | FAC401 l. 195-205 |
| Conditions de règlement | celles du client des conditions (AA0003 de WGEN/WCLI) : CMODE, CNBJOU, CJOULE, CESCOM, CARRET ; remplacées par AB001.CCMOD, CCECH, CCLE, CCESC, CCART si **AB001.CCMOD ≠ blanc** | FAC401 l. 213-231 |
| Fréquence | AA0003(**client des conditions**).CDOPT3 ≠ fréquence demandée ⇒ écarté **sans** être listé ; client des conditions introuvable ⇒ CDOPT3 forcé à '$' ⇒ écarté (cas : AB001FA avec FACRC ≠ 1 et BL sans client facturé) | FAC401 l. 213-215, 234-236 |

> Correction de `FAC401.md` R4 / R6 : le **client facturé** écrit (TGENF/TCLIF) est **toujours** le client facturé du BL
> (EXP010.HGENF/HCLIF, éventuellement 0) ; WGEN/WCLI ne sert qu'à choisir les conditions de règlement et la fréquence.

### 1.3 TFAC400 (un enregistrement par donneur d'ordre, clé unique RSOC/TGENO/TCLIO)

| Colonne | Valeur |
|---|---|
| RSOC | société (zone RSOC du dernier enregistrement lu = PAR1) |
| TGENO / TCLIO | EXP009.HGENE / HCLIE (client revendeur = donneur d'ordre) |
| TMODEF | AA0003(donneur d'ordre).CDOPT2 (mode de facturation automatique, '1' à '8') |

Écrit si absent, sinon réécrit (FAC401 l. 251-253).

### 1.4 TFAC401 (détail)

| Colonne | Valeur (FAC401 l. 207-262) |
|---|---|
| RSOC | société |
| TGENO / TCLIO | HGENE / HCLIE |
| TMODEF | CDOPT2 du donneur d'ordre |
| TGENF / TCLIF | EXP010.HGENF / HCLIF (0 possible) |
| TMODER, TNBECH, TLE, TESC, TART | conditions (§ 1.2). ⚠ TESC est un 2,1 : `Z-ADD CESCOM` (4,2) tronque (1,25 → 1,2 ; 10,00 → 0,0) |
| TFJ, TNOM, TRUE1, TRUE2, TPOSTE, TVILLE | adresse de livraison du BL : EXP009.HFJ, HRS, HRUE1, HRUE2, HCP, HVILLE |
| TDOSS / TDCLE | EXP010.HNODOS / HCLEF |
| TNOBL / TNUM | n° de BL / n° d'élément de BL |
| TREP | AB001.CCREP |
| TETABL | EXP010.HETABL (C ou R) |
| TARTIC, TINDIC, TNLIG | blanc / blanc / 0, **sauf** cas « par article » ci-dessous |

**Une ligne par élément de BL**, sauf si `CDOPT2 ≥ '7' et CCTYPC ≥ '3'` et `CDOPT2 ≠ '8'` (FAC401 l. 254-255) : alors une
ligne par ligne EXP015 du BL dont DART ou DCLIG est renseigné, avec TARTIC = DART, TINDIC = DIND, TNLIG = DNLIG
(l. 261-269). ⚠ Ici CDOPT2 est celui du **client des conditions** (relu l. 215), pas celui du donneur d'ordre qui a donné TMODEF.

### 1.5 Ordre de lecture dans FAC402P (logiques TFAC401L1 à L7, DDS lues)

Toutes commencent par RSOC, TGENO, TCLIO, TGENF, TCLIF ; FAC402P lit par `SETLL/READE` sur (PSOC, TGENO, TCLIO) (KFAC40 l. 675-678).

| Logique (mode) | Suite de la clé |
|---|---|
| L1 (mode 1) | TDOSS, TDCLE, TETABL, TNOBL, TNUM |
| L2 (mode 2) | TDOSS, TDCLE, TFJ, TNOM, TRUE1, TRUE2, TPOSTE, TVILLE, TETABL, TNOBL, TNUM |
| L3 (mode 3) | identique à L1 |
| L4 (mode 4) | TREP, TMODER, TNBECH, TLE, TART, TESC, TETABL, TNOBL, TDOSS, TDCLE, TNUM |
| L5 (modes 5 et 8) | TREP, TMODER, TNBECH, TLE, TART, TESC, TFJ, TNOM, TRUE1, TRUE2, TPOSTE, TVILLE, TETABL, TDOSS, TDCLE, TNOBL, TNUM |
| L6 (mode 6) | TREP, TMODER, TNBECH, TLE, TART, TESC, TETABL, TDOSS, TDCLE, TNOBL, TNUM |
| L7 (mode 7) | TDOSS, TDCLE, **TARTIC, TINDIC**, TETABL, TNOBL, TNUM, TNLIG |

Les clients donneurs d'ordre sont traités dans l'ordre de TFAC400 (clé RSOC, TGENO, TCLIO) (l. 800, 828).

---

## 2. Regroupement exact par mode (zones de rupture)

Aiguillage l. 809-826 : `COMP` met l'indicateur **à on si égal, à off sinon**. Les lignes 813 (`TMODEF COMP '5' 05`) puis
817 (`TMODEF COMP '8' 05`) se suivent : pour un client en **mode 5**, l'indicateur 05 est remis à off par la ligne 817.

> ⚠ **Mode 5 jamais traité** : aucun MAINx n'est exécuté, aucune facture, BL laissés non facturés, rien sur FAC402PR.
> Même code dans FAC412 (l. 630-634), donc aussi absent du provisoire. Seul le mode 8 passe par MAIN5.
> À confirmer sur les données : existe-t-il des clients AA0003.CDOPT2 = '5' ?

Notation : « clé facture » = zones dont le changement crée une nouvelle facture (NEWFAC) ; « clé élément » = nouvel
élément FAC090 dans la même facture. Toutes les clés commencent par RSOC, TGENO, TCLIO, TGENF, TCLIF.

| Mode | Clé facture (suite) | Clé élément | Lignes FAC100 | Réf. |
|---|---|---|---|---|
| 1 | TDOSS, *TCLE (jamais alimenté, toujours blanc)*, TETABL, TNOBL, TNUM | = facture (1 élément) | ligne(s) de l'élément de BL | RCLE l. 236-247, MAIN1 l. 835-861 |
| 2 | TDOSS, TDCLE, TFJ, TNOM, TRUE1, TRUE2, TPOSTE, TVILLE, TETABL | = facture (1 élément, tous les BL) | fabrication : **une seule ligne**, quantité = somme des HQTELI ; négoce : lignes de chaque BL | RCLE2 l. 262-277, MAIN2 l. 950-987 |
| 3 | TDOSS, TDCLE, TETABL | = facture (1 élément) | comme le mode 2 | RCLE3 l. 296-305 |
| 4 | TREP, TMODER, TNBECH, TLE, TART, TESC, TETABL, TNOBL | + TDOSS, TDCLE, TNUM : **un élément par élément de BL** | lignes de l'élément de BL | RCLE4 l. 318-336 (RL94 = 1-51, RL84 = 1-60) |
| 5 | (jamais exécuté, voir ci-dessus) | | | |
| 8 | TREP, TMODER, TNBECH, TLE, TART, TESC, TFJ, TNOM, TRUE1, TRUE2, TPOSTE, TVILLE, TETABL | + TDOSS, TDCLE, TNOBL, TNUM : un élément par élément de BL | lignes de l'élément de BL | RCLE5 l. 358-382 (RL95 = 1-207, RL85 = 1-222) |
| 6 | TREP, TMODER, TNBECH, TLE, TART, TESC, TETABL | + TDOSS, TDCLE : **un élément par dossier** | 3e niveau RL76 (+ TNOBL, TNUM) : lignes, compléments, EXP011 et remise **par élément de BL** | RCLE6 l. 410-429 |
| 7 | TDOSS, TDCLE, *TART7, TIND7 (jamais alimentés, toujours blancs)*, TETABL | = facture (1 élément) | comme le mode 2 ; en négoce « par article », une ligne EXP015 par enregistrement TFAC401 (clé KEXP107 avec TNLIG) | RCLE7 l. 452-463, MAIN7 l. 1784-1821 |

Conséquences à connaître :
- **Mode 7 ≠ « par article »** : TART7/TIND7 ne sont copiés nulle part (seuls RSOC7…TDCLE7, TETABL7 le sont, l. 1788-1795) ;
  une facture par dossier et état de BL. Comme L7 trie l'article **avant** TETABL, un dossier ayant dans le même lot des BL
  C et R sur plusieurs articles alterne C/R/C… ⇒ **plusieurs factures et avoirs pour le même dossier**.
- **Mode 7, négoce, CCTYPC < '3'** (TNLIG = 0 écrit par FAC401) : SPEXP015 lit EXP015L1 sur (RSOC, BL, élément, **0**)
  (l. 3380-3384) ⇒ normalement aucune ligne (non établi : aucune ligne EXP015 n° 0 attendue).
- Mode 8 : l'adresse d'envoi FAC080 = adresse de livraison (TFJ…TVILLE) du groupe (l. 1468-1474).
- Modes 4, 6, 8 : au-delà de **99 éléments**, la facture est écrite et une nouvelle est ouverte (l. 1291-1296, 1490-1495,
  1676-1681), voir anomalies § 7.

---

## 3. Numérotation et dates

### 3.1 Année de facture
`WAN` / `YAN` = PAR1.PFACAA (2 chiffres) complété en 20AA si < 34, sinon 19AA (l. 742-748, 2627-2632). Mois et jour =
PFACMM / PFACJJ. Toutes les dates « facture » écrites = WAN / PFACMM / PFACJJ.

### 3.2 GETFACTURE (AXISRV l. 485-618), appelée par NEWFAC (l. 2622-2657)

Appel : `getfacture(PSOC : PDENS : YAN : YMOI : 'D')` avec YMOI = 0 si FAC010.PTYPEN = 1, PFACMM si PTYPEN = 2, 0 sinon.

1. AA0007(société) introuvable ⇒ −1 ; mode vide ⇒ −1 ; enseigne vide **et** année 0 ⇒ −1 ; FAC010 introuvable ⇒ −1
   (l. 518-543).
2. **Type 3** = PTYPEN = 3 **et** AA0007.SOP03 = 'O' (l. 545-546) : enseigne obligatoire (−1 sinon) ; `CHAIN` **avec verrou**
   PRT320F (RSOC, RENSEI = enseigne) ; absent ⇒ −1 ; n° = ENDNFA, ou 1 si ENDNFA = 0 ; puis **ENDNFA = n° + 1** et UPDATE
   (l. 555-570, 601-605). ENDNFA est donc le **prochain** n° (libellé « dernier n° » trompeur).
3. **Types 1 et 2** : `CHAIN` **avec verrou** FAC040 (RSOC, NAN = année, NMOIS = 0 ou mois) ; **absent ⇒ −1, le compteur
   n'est pas créé** ; n° = NFACT, ou 1 si 0 ; puis **NFACT = n° + 1** et UPDATE (l. 574-593, 607-608).
4. PTYPEN = 3 sans SOP03 = 'O', ou PTYPEN hors 1-3 : n° = 0, puis UPDATE FAC040 sans lecture préalable (l. 607-608) :
   résultat **non établi** (erreur d'exécution probable).
5. Fichiers ouverts et fermés à chaque appel ; pas de contrôle de validation (commit).

FAC402P **ne teste pas** le retour : −1 ou 0 deviendrait RNFAC (puis doublon de clé FAC080 ⇒ arrêt du programme, WRITE sans
indicateur d'erreur l. 2793). Bornes pour l'édition : si n° > 0 et PDIFAC ≠ N, le premier n° va dans PFACD, les suivants
dans PFACF (l. 2645-2654).

### 3.3 Échéance : GETECHEAN (AXISRV l. 620-719), appelée en SQL (l. 2679-2683)

`getechean(FDANNE, FDMOIS, FDJOUR, FDNLE, FDNECH, FDART)` ⇒ DATECH, dont les sous-zones sont FDECHA / FDECHM / FDECHJ
(DS l. 502-506). Algorithme exact :

1. Date de départ = date de facture (via un format *YMD : années 1940-2039 seulement, l. 652).
2. Nombre de jours FDNECH : 30/60/90/120/150/180 ⇒ 1 à 6 mois ; 45 ⇒ 1 mois + 15 jours ; 10 ou 20 ⇒ 10 ou 20 jours ;
   **toute autre valeur ⇒ rien** (l. 658-678).
3. Si 0 < FDNLE < 31 ⇒ + 1 mois (l. 680-682). Ajout des mois (fin de mois tronquée, ADDDUR) puis des jours (l. 683-688).
4. Si FDNLE > 0 ⇒ jour = FDNLE (l. 693-694).
5. Si FDART ≠ 0 **et** jour de facture > FDART ⇒ mois + 1 (l. 696-698) ; mois > 12 ⇒ −12 et année + 1 (l. 700-702).
6. Jour plafonné au nombre de jours du mois ; février = 29 si année divisible par 4 (l. 704-715).

Calculée aussi pour les **avoirs** (pas de cas particulier dans FAC402P). Les ajustements 4 à 6 **modifient bien** la valeur
renvoyée (DATECH est la DS qui contient FDECHA/M/J) : la mention « à vérifier » de `FAC402P.md` R5 est levée.
`CalculFacture.Echeance` (C#) suit ce calcul, sauf : avoir ⇒ date de facture (règle FAC201), et l'ordre « arrêté » / « le »
(sans effet sauf cas de fin de mois).

---

## 4. Tables écrites, colonne par colonne

Abréviations : *AB001* = dossier de l'élément (RSOC, RDOSS = TDOSS, CCDCLE = TDCLE) ; *AB001FA* = même clé ;
*client dossier* = AA0003(PSOC, AB001.RGEN, AB001.RCLI) (l. 3242-3244). SPAB001 (l. 3217-3259) remet d'abord à blanc/0 :
CCTYPV, CCUNIT, CCDES1, CCDES2, CCPXVE, CTXRM, CDCVD, CDOPT7, FAORTE, FAATT, FAEDTB, FATPC, FACPT, FANAL et l'adresse FA*.
Les autres zones AB001 / AA0003 ne sont **pas** remises à zéro : si le CHAIN échoue, elles gardent la valeur du dossier
précédent.

### 4.1 FAC080 — en-tête (WRITE l. 2793 ; initialisation SPINI080 l. 2801-2879)

| Colonne | Valeur | Ligne |
|---|---|---|
| RSOC | société | — |
| RNFAC | n° GETFACTURE | 2663 |
| FDTYPE | 'A' si BL d'état R (indicateur 16), sinon 'F' | 2664-2665 |
| FDANNE / FDMOIS / FDJOUR | WAN / PFACMM / PFACJJ | 2666-2668 |
| FDREF | AB001.CCREFC (**dernier dossier** de la facture) | 2669 |
| FDCNNE / FDCOIS / FDCOUR | AB001.CCDENA / CCDENM / CCDENJ (date d'entrée de commande, dernier dossier) | 2670-2672 |
| FDGEND / FDCLID | TGENO / TCLIO (donneur d'ordre) | SPDET9x |
| FDGENF / FDCLIF | TGENF / TCLIF (client facturé du BL, 0 possible) | SPDET9x |
| FDRGLT | TMODER — ⚠ valeur de l'enregistrement TFAC401 **qui a provoqué la rupture** (voir § 7) | 2673 |
| FDRELE | AA0003(client dossier).CRELVE | 2674 |
| FDECHA / FDECHM / FDECHJ | GETECHEAN (§ 3.3) | 2679-2683 |
| FDNECH / FDNLE / FDART / FDESC | TNBECH / TLE / TART / TESC du premier enregistrement du groupe | SPDET9x (ex. 886-889) |
| FDCPT | AB001FA.FACPT (acompte) du **dernier dossier** ; 0 si AB001FA absent | 2686 |
| FDEDIT | 'A' ou 'O' (§ 6) | 2768-2791 |
| FDPLEL | FENUM = nombre d'éléments (1 en modes 1, 2, 3, 7) | 2687 |
| FDFJ, FDNOM, FDRUE1, FDRUE2, FDPOST, FDVILL, FDPAYS | adresse d'envoi, règle ci-dessous | 2689-2719 |
| FDENS | PAR1.PDENS (enseigne) | 2721 |
| FDREP | TREP (AB001.CCREP du premier enregistrement) | SPDET9x |
| FDCOMP, FDJALP, FDJALM, FDTRAT, FDEDT, FDBROU, FDTAGR | 'N' | 2723-2743, 2756 |
| FDCODA/M/J, FDJLPA/M/J, FDJLMA/M/J, FDTRTA/M/J, FDEDA/M/J | 0 | 2724-2742 |
| FDVEN | SAVLIG (code du **premier** type de ligne 'V' rencontré dans la facture), sinon CODVENT courant | 2745-2749 |
| FDMTN, FDPOT, FDPOE, FDMES, FDTVA | **0** (calculés par FAC201) | 2851, 2751-2754 |
| FDATT | AB001FA.FAATT (dernier dossier) | 2755 |
| FDINAT | blanc | 2757 |
| FDMON | 'E' | 2758 |
| FDCDEV / FDCOURD | 'EUR' / 1 | 2759-2760 |
| FDPROF | profil utilisateur (programme PRTRTVUS) | 768, 2761 |
| FDAMTN, FDAPOT, FDAPOE, FDAMES, FDATVA, FDACPT | 0 | 2762-2767 |
| FDPRES | AA0003(client dossier).CDOPT7 (présentation BL sur facture) | 2675 |
| FDFACO, FDFACA, FDTAGE, FDNONE, FDDATEE, FDHEURE, FDMANAU, FDMETHE, FDTYPEE, FDRFAD, FDRFAF, FDIDFACTX, FDSTAT, FDMENTIONR, FDFACOMPTE, FDFACRECTI, FDBIENSERV, FDPDPRED, FDFACSTAT | **non référencées** par FAC402P ⇒ blanc / 0 | — |

**Adresse d'envoi** (l. 2689-2719) — appliquée seulement si (FDCLIF > 0 et FDCLID = FDCLIF) ou FDCLIF = 0 :
- mode 8 : rien à faire, l'adresse de livraison a été posée à l'ouverture de la facture (FDPAYS reste blanc) ;
- sinon, si l'adresse AB001FA (FAFJ…FAVILL) n'est pas vide : FDFJ…FDVILL = FAFJ, FANOM, FARUE1, FARUE2, FAPOST, FAVILL
  (FDPAYS reste blanc) ;
- sinon, si AB001FA.FANAL ≠ 0 et A500F (PSOC, AB001.RGEN, AB001.RCLI, FANAL) existe : ALFJ, ALRS, ALRUE1, ALRUE2, ALPOST,
  ALVILL, **ALPAY → FDPAYS** ;
- sinon : **adresse laissée à blanc**. Correction de `FAC402P.md` R13 : il n'y a **pas** de repli sur l'adresse du client
  dans FAC402P (l'édition s'en charge probablement, non vérifié ici). Client facturé ≠ donneur d'ordre ⇒ blanc aussi (sauf mode 8).

### 4.2 FAC081 — textes de pied (SPFAC081 l. 2882-2894)

Pour chaque ligne CLITXF (PSOC, KGEN, KCLI) : RSOC, RNFAC, FDANNE, FDMOIS, FDJOUR (= ceux de FAC080), **FDTLIG = CTNLIG**,
**FDTXT = CTTXT**. WRITE avec indicateur d'erreur (doublon ignoré). KGEN/KCLI (SPAB001 l. 3250-3257) : AB001FA.FATPC = blanc
ou '1' (ou AB001FA absent) ⇒ donneur d'ordre (TGENO/TCLIO), sinon client facturé (TGENF/TCLIF) ; pris sur le **dernier
dossier** de la facture. Écrit après FAC080, une fois par facture (sauf découpage > 99 éléments, § 7).

### 4.3 FAC090 — élément (SPFAC090 l. 2897-2949 ; SPINI090 l. 2953-2980)

| Colonne | Valeur |
|---|---|
| RSOC, RNFAC | société, n° de facture |
| FEANNE / FEMOIS / FEJOUR | WAN / PFACMM / PFACJJ |
| FENUM | 1 (modes 1, 2, 3, 7) ; rang de l'élément 1 à 99 (modes 4, 6, 8) |
| FEDOSS / FECLE | dossier / clé de l'élément |
| FECAT, FESCAT, FEDEV, FEMOD | AB001.CCAT, CCSCAT, CCDEV, CCMODL |
| FETXC | AB001.CCTXCO si CCTYPC = '1', sinon 0 |
| FENOM / FETXO / FEMTO | blanc / 0 / 0 |
| FEQTE | somme des FGQTE (signées) des lignes « dossier de fabrication » de l'élément (ni AB004 ni EXP011) ; 0 en négoce (l. 3064-3067) |
| FEFAA | 'D' |
| FEDTB | AB001FA.FAEDTB |
| FETYP | CCTYPC '1' ⇒ 1, '4' ⇒ 2, '3' ⇒ 3, autre (dont '2') ⇒ 0 (l. 3245-3247, 2914-2916) |
| FECTC | blanc dans le source (MOVEL *BLANKS) ; colonne aujourd'hui numérique 2,0 ⇒ 0 |
| FECA | somme des FGMTN des lignes dont le type FAC020 n'est ni E ni P (code trouvé dans FAC020), moins les remises (l. 3070-3075, 3178) |
| FEVEN | somme des FGMTN de toutes les lignes (E et P compris), moins les remises (l. 3069, 3179) |
| FEGEND / FECLID | TGENO / TCLIO |
| FEEDT | 'N' |
| FEORTE | AB001FA.FAORTE |
| FECAA / FEVENA | 0 |

Écrit à la fin de l'élément, **après** ses lignes FAC100, et **avant** FAC080 (modes 1, 2, 3, 7 : l. 940-942).

### 4.4 FAC100 — lignes (SPFAC100 l. 2984-3135, SPLIGTEX l. 3139-3152, SPLIGREM l. 3156-3182, SPINI100 l. 3186-3213)

Commun à toutes les lignes : RSOC ; RNFAC ; FGANNE/FGMOIS/FGJOUR = WAN/PFACMM/PFACJJ ; **FGNEL = FENUM** ;
**FGNLG = compteur + 1** (remis à 0 à chaque facture en modes 1, 2, 3, 7, à chaque élément en modes 4, 6, 8 ; les lignes de
texte consomment un n°) ; **FGCTC = blanc** (jamais référencé) ; **FGNBR = 0**.

| Colonne | A. Dossier (CCTYPC = '1') | B. Négoce (EXP015 + NEG100) | C. Complément AB004 | D. EXP011 « en plus » | E. Remise | F. Texte |
|---|---|---|---|---|---|---|
| FGCDL | AB001.CCTYPV, sinon AA0003(client dossier).CDCVD | NEG100.DNCLIG | F4CL | HPCL | 1er code FAC020 de type R (l. 775-787) ou F4CL | '9' |
| FGQTE | EXP010.HQTELI (somme selon le mode, § 5) | EXP015.DQTE | F4QTE | HPQTE | 0 | 0 |
| FGDES | AB001.CCDES1 | EXP015.DDES | F4DES | HPDES | FAC020.LLIB (25 car.) ou F4DES | voir § 5 |
| FGPU | AB001.CCPXVE | NEG100.DNPUS | F4PU | HPPU | 0 | 0 |
| FGPER | 0 | 0 | 0 | 0 | AB001.CTXRM ou F4REM | 0 |
| FGUN | AB001.CCUNIT | NEG100.DNUV | F4UT | HPUT | blanc | blanc |
| FGART / FGIND | blanc | NEG100.DNART / DNIND | blanc | HPART / HPIND | blanc | blanc |
| FGMTN | calcul ci-dessous | idem | idem | idem | FECA × taux / 100, ou F4PU si F4REM = 0 ; **positif** | 0 |
| FGTCOL | 0 | NEG100.DNTXCO | 0 | HPTXCO | 0 | 0 |
| FGLBL | 'N' | 'O' | 'N' | 'O' si négoce, 'N' si dossier | blanc | blanc |
| FGMON | AB001.CCMON | AB001.CCMON | AB001.CCMON | AB001.CCMON | blanc | blanc |
| FGPUA / FGMTNA | conversion franc/euro (copy EUROFRANC4, source absent : **non établi**) ; 0 si CCMON ni F ni E | idem | idem | idem | 0 | 0 |
| FGLIGD | 0 | NEG100.DNNLIG | 0 | 0 | 0 | 0 |
| FGBL / FGBLEL | n° de BL / élément (§ 5) | n° de BL / élément | **0 / 0** (ZFIC vidée l. 3274) | **0 / 0** (ZFIC vidée l. 3344) | 0 / 0 | 0 / 0 |
| FGBLLG | 0 | EXP015.DNLIG | 0 | 0 | 0 | 0 |

**Signe** : FGQTE est négativé si avoir (l. 2995), pour A, B, C, D. AB004 n'est jamais traité pour un avoir (`N16`).

**Montant FGMTN** (l. 3004-3017) :
- unité ≠ blanc et ≠ 'LT' : lecture AA0008L1 (société, table 'UV', argument = unité) ; `z = FGQTE / TNUM` **tronqué à
  5 décimales**, puis `FGMTN = z × FGPU` **arrondi au centime** (demi au-dessus, en valeur absolue) ;
- unité blanche ou 'LT' : **FGMTN = FGPU** (sans la quantité, et **positif même pour un avoir**) ;
- ⚠ « Le tout » (TNUM = 999 999 999) : z = 0 pour toute quantité < 10 000 ⇒ **FGMTN = 0** ;
- ⚠ unité absente de la table UV : TNUM garde la valeur de la lecture précédente (ou 0 ⇒ division par zéro, arrêt).

**Remise E** (l. 3170-3179) : `EVAL` sans arrondi ⇒ FECA × taux / 100 **tronqué** au centime ; le montant est écrit positif
puis **retranché** de FECA et FEVEN. FECA de référence = CA de l'élément accumulé **jusque-là** (lignes AB004/EXP011 comprises,
remises précédentes déjà déduites).

---

### 4.5 EXP010 — marquage du BL (SPEXP010 l. 3316-3328)

CHAIN avec verrou (RSOC, n° BL, n° élément) puis, si trouvé : **HBLFAC = 'O'**, **HNOFAC = n° de facture**,
**HDAFAA / HDAFAM / HDAFAJ = WAN / PFACMM / PFACJJ**, **HNOELT = FENUM**. Rien d'autre (HNOFAC 6 chiffres, sans l'année).
Moment : modes 2 à 8 pendant la lecture de chaque élément (avant l'écriture de FAC080) ; mode 1 à la fin de l'élément (l. 927).

### 4.6 EXP011 — éléments « à facturer en plus » (SPEXP011 l. 3331-3373)

Lecture EXP011 (RSOC, WNOBL, WNUM) ; lignes avec HPFACT = 'O' ignorées. Pour chaque autre ligne : ligne FAC100 D (ou texte
si HPCL = '9', FGDES = HPDES), puis **HPFACT = 'O'**, **HPNFAC = n°**, **HPNELT = FENUM**, **HPDFAA/M/J = WAN / PFACMM / PFACJJ**,
UPDATE. Traité **aussi pour les avoirs** (quantités négatives) et en fabrication comme en négoce.

### 4.7 AB004 — compléments de facturation (SPAB004 l. 3263-3312)

Seulement **dossier CCTYPC = '1' et pas un avoir**. Lecture AB004 (RSOC, RDOSS, F4DCLE) ; lignes F4FACT = 'O' ignorées.
Pour chaque autre ligne : F4CL = '9' ⇒ ligne de texte ; sinon FAC020(F4CL).LTYPE ≠ 'R' ⇒ ligne C ; type 'R' ⇒ ligne de remise
(taux F4REM, ou montant F4PU si F4REM = 0). Puis **F4FACT = 'O'**, **F4NFAC = n°**, **F4NELT = FENUM**,
**F4DFAA/M/J = WAN / PFACMM / PFACJJ**, UPDATE. Le premier élément facturé d'un dossier prend **toutes** les lignes en attente.

### 4.8 Compteurs (via GETFACTURE, § 3.2)

| Table | Colonne mise à jour | Clé |
|---|---|---|
| FAC040 (types 1 et 2) | NFACT = n° attribué + 1 | RSOC, NAN = année 4 chiffres, NMOIS = 0 (type 1) ou mois (type 2) |
| PRT320F (type 3 avec SOP03 = 'O') | ENDNFA = n° attribué + 1 | RSOC, RENSEI = enseigne |

Aucune autre table n'est écrite (TFAC400/TFAC401 sont en lecture, en QTEMP ; FAC402PR est un spool).

---

## 5. Construction des lignes d'un élément, par mode

**Dossier de fabrication (AB001.CCTYPC = '1', indicateur 11)** — valeurs posées à chaque enregistrement lu
(SPDETx, ex. l. 1034-1039) : CODVENT = CCTYPV (si blanc : CDCVD), UNITE = CCUNIT, DESIG1 = CCDES1, DESIG2 = CCDES2,
PXVENT = CCPXVE ; quantité = EXP010.HQTELI. Une ligne A est suivie **systématiquement** d'une ligne de texte '9' portant
CCDES2, **même si CCDES2 est blanc** (l. 3129-3132).

**Négoce (CCTYPC ≠ '1')** — SPEXP015 (l. 3377-3431) lit les lignes EXP015 de l'élément de BL (mode 7 : la seule ligne TNLIG).
Pour chaque ligne : NEG100 (RSOC, RDOSS, CCDCLE, EXP015.DNUMLC) ; si NEG100 existe, DNCLIG ≠ '9' et (DART ≠ blanc ou
DCLIG ≠ blanc) ⇒ ligne B ; sinon ligne de texte avec DDES (si non blanc). Ensuite, hors mode 7 : DDES2 puis DDES3 en lignes de
texte s'ils sont renseignés.

| Mode | Ordre d'écriture dans l'élément |
|---|---|
| 1 | dossier : A + texte, puis AB004 (si facture) ; négoce : lignes EXP015 ; puis EXP011 de l'élément, puis remise CTXRM (négoce) ; FAC090, FAC080, FAC081 |
| 2, 3, 7 | négoce : lignes EXP015 de chaque BL au fil de la lecture ; dossier : **une seule** ligne A en fin de groupe, quantité = somme des HQTELI, FGBL/FGBLEL = **premier** élément de BL du groupe ; puis AB004, EXP011 (**dernier** élément de BL seulement), remise ; FAC090, FAC080, FAC081 |
| 4, 8 | par élément de BL : comme le mode 1 ; FAC090 à la fin de chaque élément ; FAC080 + FAC081 à la fin de la facture |
| 6 | par dossier : pour chaque élément de BL, A + texte (dossier) ou lignes EXP015 (négoce), AB004, EXP011 de ce BL, **remise CTXRM par BL** ; FAC090 à la fin du dossier ; FAC080 + FAC081 à la fin de la facture |

Ligne de remise CTXRM : uniquement négoce avec AB001.CTXRM > 0 (indicateur 17, l. 3248), aussi pour un avoir ; code et
libellé = premier type de ligne 'R' de FAC020 dans l'ordre des codes (l. 775-787) ; blanc s'il n'y en a pas.

---

## 6. « À contrôler » (FDEDIT) et anomalies de FAC402PR

### 6.1 Indicateurs posés pendant les lignes (SPFAC100 et SPDETx)

| Ind. | Signification | Posé quand | Ligne |
|---|---|---|---|
| 27 | Code vente | FGCDL blanc (l. 3077) ; ou CCTYPV blanc dans SPDETx — en modes 1, 2, 3, 4, 7 il **reste allumé même si CDCVD le remplace** (en modes 6 et 8 il est recalculé) ; en négoce, recalculé (on/off) à chaque ligne EXP015 (l. 3396) | 907, 3396 |
| 08 | Unité | négoce sans article (DART blanc) et type ≠ E/P et FGUN blanc ; ou dossier, ou négoce avec article non gratuit (DNOPT2 ≠ O), et FGUN blanc | 3098-3117 |
| 09 | P.U. | mêmes cas que la 2e branche ci-dessus et FGPU = 0, si NEG100.DNOPT5 ≠ 'O' | 3119-3124 |
| 23 | Prix à définir | idem avec DNOPT5 = 'O' (⚠ en fabrication, DNOPT5 est celui de la dernière ligne NEG100 lue) | 3120-3121 |
| 10 | Code vente différent | type FAC020 = 'V' et FGCDL ≠ SAVLIG non blanc (⚠ LTYPE de la lecture précédente si le code est absent de FAC020) | 3081-3085 |
| 22 | Vente à 0 | FEVEN de l'élément = 0 **et** cumul WMTN des éléments de la facture = 0 | 2927-2930 |

Remise à off : début de facture en modes 1, 2, 3, 7 (27, 08, 09, 10, 22, 23, 14) ; **début de chaque élément** en modes 4, 6,
8 (27, 08, 09, 10, 22, 23 ; 14 seulement en début de facture).

### 6.2 Règle FDEDIT (SPFAC080 l. 2768-2791)

```
si l'un des indicateurs 27, 08, 09, 10, 22, 23 est allumé AU MOMENT D'ÉCRIRE FAC080 → indicateur 14
FDEDIT = 'A' si 14 ou FDCPT > WMTN ; sinon 'O'
```
WMTN = somme des FEVEN des éléments de la facture (l. 2928). Effets exacts :
- modes 4, 6, 8 : seuls les indicateurs du **dernier élément** comptent ;
- **avoir** : WMTN < 0 ⇒ FDCPT (≥ 0) > WMTN ⇒ **tout avoir est « à contrôler »** ;
- facture dont l'acompte du dernier dossier dépasse le total HT des ventes ⇒ 'A'.

### 6.3 Observation FAC402PR (une ligne par élément, l. 2926-2946)

Si l'un de 27, 08, 09, 10, 22, 23 : `'Manque'` (si ni 10 ni 22) puis, dans l'ordre, `'   Code vente'` (27), `'   Unité'` (08),
`'   P.U.'` (09) ; si 23 le texte **est remplacé** par `'Prix à définir'` ; puis `' Code Vente diff.'` (10), `' Vente à 0'` (22).
Zone de 35 caractères (tronquée). Colonnes imprimées : RNFAC, FENUM, F/A, FEGEND, FECLID, nom (AA0003.CNOM du client dossier),
FEDOSS, FECLE, CCDES1, CA = FECA (signe inversé pour un avoir, donc positif), observation. Totaux : factures, avoirs, CA
(⚠ les avoirs s'**ajoutent** au CA total).

---

## 7. Anomalies et bizarreries du RPG (à ne pas reproduire, sauf décision contraire)

| # | Anomalie | Ligne | Proposition |
|---|---|---|---|
| 1 | Mode 5 jamais traité (COMP '8' éteint 05) | 813-817 | traiter le mode 5 comme prévu ; vérifier s'il existe des clients en mode 5 |
| 2 | Mode 7 non regroupé par article (TART7/TIND7 jamais alimentés) ; alternance C/R ⇒ factures multiples | 460-461, 1788-1795 | clé = dossier + état, ou vraiment par article : décision métier |
| 3 | Mode 7 négoce CCTYPC < '3' : TNLIG = 0 ⇒ lecture EXP015 de la ligne 0, aucune ligne probable | 3380-3384 ; FAC401 l. 254 | lire toutes les lignes de l'élément |
| 4 | FDRGLT pris sur l'enregistrement qui suit le groupe (zones TFAC401 écrasées par le READE de rupture) ; FDNECH… du premier | 2673 | prendre les conditions du groupe |
| 5 | Modes 2, 3, 7 : EXP011 lu pour le **dernier** élément de BL seulement ; les autres BL sont marqués facturés, leurs lignes EXP011 ne le seront jamais | 1060 (WNOBL/WNUM de SPDET2) | traiter chaque élément de BL |
| 6 | Mode 6 : quantité de la ligne dossier **cumulée** d'un BL à l'autre du même dossier (QTE non remise à 0 en SPDET76, sauf si AB004/EXP011 ont vidé ZFIC) | 1699-1705, 1727 | quantité du BL seul |
| 7 | Mode 6 : remise CTXRM écrite **par BL**, calculée sur le CA cumulé déjà remisé | 1739-1746 | une remise par élément (dossier) |
| 8 | > 99 éléments : facture écrite sans FAC081, la suivante garde l'en-tête, SAVLIG, WMTN et **reprend l'acompte FACPT** (double déduction) | 1291-1296 | découpage propre ou pas de limite (FENUM 2 chiffres) |
| 9 | FDEDIT : seul le dernier élément compte (modes 4, 6, 8) ; tous les avoirs 'A' | 2768-2791 | à décider : reproduire ou corriger |
| 10 | Indicateur 27 laissé allumé quand CDCVD remplace CCTYPV (modes 1-4, 7), éteint par la ligne suivante en négoce | 907, 3396 | évaluer chaque ligne |
| 11 | Unité blanche ou 'LT' : montant = PU, positif même pour un avoir | 3015-3017 | quantité négative ⇒ montant négatif |
| 12 | « Le tout » (TNUM 999 999 999) ⇒ montant 0 | 3013-3014 | montant = PU (forfait), comme le reste du prototype |
| 13 | Ligne de texte AB004 (F4CL = '9') écrite avec une désignation **blanche** (ZFIC vidée, F4DES non recopiée) | 3274-3277 | FGDES = F4DES |
| 14 | Ligne de texte CCDES2 écrite même vide après chaque ligne dossier | 3129-3132 | seulement si non vide |
| 15 | Lignes AB004 / EXP011 / remise sans n° de BL (FGBL = 0) | 3274, 3344 | à décider (le RPG les laisse à 0) |
| 16 | Adresse d'envoi blanche si ni AB001FA ni A500F, ou si facturé ≠ donneur d'ordre | 2689-2719 | vérifier comment l'édition comble le vide |
| 17 | Acompte, référence, date de commande, attention, texte de pied : ceux du **dernier** dossier de la facture | 2669-2686 | à décider en multi-dossiers |
| 18 | GETFACTURE −1 non testé ; FAC040 absent pour l'année/mois ⇒ n° −1 ⇒ doublon ⇒ arrêt | 2643 ; AXISRV l. 584-586 | créer le compteur ou refuser avant de commencer |
| 19 | Aucune transaction : BL marqués (EXP010) **avant** l'écriture de FAC080 ; un arrêt laisse des BL pointant sur une facture inexistante et un trou de numérotation | 3316-3328, 2793 | une transaction par facture (n° + écritures + marquages) |
| 20 | Journal (FAC210CLP) lancé seulement si l'édition des factures est demandée (IF imbriqués) | 1919-1975 | séparer les deux options |
| 21 | Bornes d'édition PFACD–PFACF : plage de n°, peut inclure des factures créées entre-temps par d'autres | 2645-2654, 1925-1951 | éditer la liste des factures créées |
| 22 | Zones lues non remises à zéro si CHAIN échoue (AB001.CCTYPC, CCREFC, AA0003.CRELVE, TAB_TNUM, LTYPE) | 3242-3244, 3010, 3071 | lecture explicite, défaut connu |
| 23 | TESC tronqué (CESCOM 4,2 → 2,1) dans FAC401 | FAC401 l. 221 | garder le taux exact |
| 24 | Total CA de FAC402PR : avoirs ajoutés en positif | 2923-2925 | total signé |

Points **non établis** :
- conditionnement d'un `IF` par indicateur (`N30 LTYPE IFNE 'R'`, l. 3282) quand le code ligne AB004 est absent de FAC020 :
  la branche ELSE est-elle exécutée ? (selon la règle RPG, le groupe IF est sauté ; la ligne serait alors marquée facturée
  sans ligne FAC100) ;
- comportement de READE à la rupture (zones non modifiées en fin de clé : comportement standard supposé, anomalie 4) ;
- conversion EURO/FRANC (copy EUROFRANC4 absent du dossier source) ;
- valeur réelle de CCTYPC pour un dossier de fabrication (FAC402P suppose '1', le glossaire dit '2' ; question déjà ouverte
  dans `docs/facturation/facturation-analyse.md`) ; avec '2', FAC402P passe par la branche négoce (EXP015 + NEG100) et FETYP = 0.

---

## 8. FAC412 (provisoire) comparé à FAC402P

FAC412 reprend l'algorithme dans des copies QTEMP de FAC080/090/100. Différences relevées par comparaison des deux sources :

| Sujet | FAC402P (définitif) | FAC412 (provisoire) |
|---|---|---|
| Mises à jour | EXP010, EXP011, AB004 mis à jour ; FAC081 écrit | lecture seule ; pas de FAC081 |
| Numérotation | GETFACTURE 'D' à chaque facture | GETFACTURE 'P' pour la première, puis n° + 1 (FAC412 l. 1688-1712) |
| Échéance | GETECHEAN | non calculée (DATECH = 0) |
| Élément modes 4 / 8 | par **élément** de BL (RL84 = 1-60, RL85 = 1-222) | par BL + dossier (RL84 = 1-57, RL85 = 1-219) |
| Mode 6 | élément = dossier, lignes par BL (3 niveaux) | élément = élément de BL (2 niveaux, RL86 = 1-60) |
| Remise | montant positif retranché | montant **négatif** ajouté (FAC412 l. 2169-2175) ; FGMON = CCMON |
| Montant ligne | DIV à 5 décimales puis MULT arrondi | `EVAL (Q × PU) / TNUM` sans arrondi (troncature) |
| À contrôler | indicateurs au moment de FAC080 + 10, 22 + acompte | 14 posé dans SPFAC090 (n'importe quel élément), sans 10 ni 22 ni acompte |
| Contrôle unité/PU | variante ci-dessus | conditions différentes (l. 2095-2100) |
| > 99 éléments | — | FDACPT = 2 sur la facture coupée |
| Mode 7 | pas de lignes DDES2/DDES3 | DDES2/DDES3 écrits aussi en mode 7 |
| Indicateur 38 | remis par COMP | en plus SETOFF explicite |

Conséquence : le provisoire **ne prévoit pas exactement** le définitif en modes 4, 6, 8 (découpage en éléments différent)
et pour les remises (signe). En cible, un seul service avec un mode « simulation » supprime ces écarts.

---

## 9. Corrections à reporter dans les fiches existantes

- `FAC402P.md` R1/R2 : mode 5 jamais traité ; mode 7 non découpé par article ; modes 4/8 un élément par élément de BL ;
  mode 6 un élément par dossier avec lignes par BL ; modes 2/3/7 une seule ligne « dossier » (quantité totale).
- `FAC402P.md` R5 : les ajustements de fin de GETECHEAN agissent bien sur la valeur renvoyée.
- `FAC402P.md` R10 : EXP011 seulement pour le dernier BL en modes 2, 3, 7 ; aussi pour les avoirs.
- `FAC402P.md` R13 : pas de repli sur l'adresse du client (adresse blanche).
- `FAC402P.md` R15 : effets « dernier élément » et « tous les avoirs ».
- `FAC401.md` R4/R6 : client facturé toujours HGENF/HCLIF ; WGEN/WCLI = client des conditions et de la fréquence.
