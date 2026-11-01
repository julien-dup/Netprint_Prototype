# Pré-BL — modèle de données et programmes EXP5xx

> *Sources :* DDS `EXP509/510/510L1/512/516/517/520/520D/520L/530/531` et programmes `EXP5*` de `D:\dvp\sources AS400\NP11` ; WinDev `D:\Mes Projets\PreBL` ; PHP `D:\docs\Netprint\web services\rest_netprint*\articles_controller{GRAPPG,CFP}.php` ; cartographie `D:\docs\Netprint\cartographie-db2`.
> *Date d'analyse :* 2026-11-01. Détail de la transformation : [EXP540](EXP540.md).

## Le cycle du pré-BL

1. **Saisie** dans le WinDev PreBL / PreBLSansPalette (`FEN_SaisiePREBL`, `FEN_AjoutLigneCde`, `FI_AjoutPalettesEXP517`…) : en-tête EXP509, lignes EXP510, lots EXP516, palettes EXP517, textes de pied EXP512. Le **n° est réservé sur le compteur des BL** (`GetBL(ccense, n, 'P')` puis `'D'`) : le n° de pré-BL **sera** le n° du BL. Verrou de modification : `PRTLCK` (`PLFIC = 'PREBL'`, clé société + n°).
2. **Pointage des palettes** sur terminal (contrôleurs PHP GRAPPG / CFP) : EXP517, PROD_ENTITE, A900F ; passe `EXP509.HENBL` à `'E'` (en cours).
3. **Édition du pré-BL** (PDF) : EXP530NAV → EXP530NA2 → **EXP535P2**.
4. **Transformation en BL** quand toutes les palettes sont au statut 4 et hors picking : EXP540NAV → **EXP540** → EXP009/010/012/015/016, A700F, PROD_ENTITE statut 5, NEG100, AB001 ; `HENBL = 'O'`.
5. **Suppression du BL** dans EXP010S : remet `HENBL = 'E'` (EXP010S l. 4263-4265).

## Tables (toutes `UNIQUE`, bibliothèque NP11F)

| Table | Rôle | Clé | Colonnes importantes |
| --- | --- | --- | --- |
| **EXP509** | En-tête de pré-BL (miroir d'EXP009) | RSOC, HNOBL | Date de départ `HDEXAA/MM/JJ` ; client livré `HGENE/HCLIE` + n° d'adresse `HNADRE` (A500F) ; transport `HTRAN`, `HTYPL` (M/E), `HTYPP` (F/D/P), `HBART` ; totaux `HNCOLT`, `HNPALT`, `HPDSTT` ; `HNBEPL` ; enseigne `HENS` ; profil créateur `HPROF` ; observations `HOBS/HOBS2/HOBS3` ; mention BL/facture `HMENTI` ; **`HENBL`** ; date de création `HDCREA` ; **`HSTOCKP`** stock payé O/N |
| **EXP510** | Ligne de pré-BL = une ligne de commande livrée | RSOC, HNOBL, HNUM | `HNUM` (n° de ligne du pré-BL, libellé « élément » trompeur) ; dossier `HNODOS/HCLEF` + ligne de commande `HNUMLC` (→ NEG100) ; `DART/DIND`, `DQTE` ; désignations `DOBS1-3` ; `DAPPSOLD` appel à solder ; `DETALI` état de livraison A/T/S/E ; `DENPAL`, `DENUC`, `DEEXMEC` (palettes, UC, exemplaires de l'UC incomplète) ; emballage `DEMBP1`, `DEEMBPE` (exceptionnel), `DTYEMB` P/C, `DENPALE` |
| EXP510L1 | Logique d'EXP510 | RSOC, HNOBL, HNODOS, HCLEF, HNUMLC | Une seule ligne de pré-BL par ligne de commande ; ordre de lecture d'EXP540 |
| **EXP512** | Texte de pied | RSOC, HNOBL, HTNLIG | `HTTXT` (40 c.) → EXP012 |
| **EXP516** | Lots prévus d'une ligne | RSOC, DNBL, DNUM (= EXP510.HNUM), DNLIG (n° de lot dans la ligne) | `DNTYPL` type 1/2 *(supposé : lot en stock / OF en cours, cf. `FI_AjoutOFEncours`)* ; `DNLOT` lot ou OF ; `DNDEP` dépôt ; `DNQTPL` qté prise ; `DNPAL`, `DNPALM` (n° de palette mixte), `DNUC`, `DEXMEC`, `DCOLIM`, `DPALINCOM` ; `DLIBPAL` libellé des n° de palettes (rempli par le récapitulatif palettes) ; `DDOBS1/2` ; `DCLOTU` préparation clôturée |
| **EXP517** | Palettes (SSCC) choisies pour un lot | RSOC, DNBL, DNUM, DNLIG, DPSSCC | `DPSSCC` (26 c., → `PROD_ENTITE.NUMERO_SSCC`) ; `DPQTEP` qté prise ; `DPPICK` picking O/N ; `DPOBS` |
| EXP520 / EXP520D / EXP520L | **Demande de déstockage / mise à disposition** (pas le pré-BL) : en-tête (`DETYPE` DESTOCK/MAD, dépôts départ/arrivée, `DESTAT`, `DESTOCKP`), palettes SSCC, lots | RSOC, DEDEM (+ DDSSCC / DLLOT) | Utilisées seulement par WinDev (DemandeDestockage, InventairesSSCC, Transfert*) et PHP ; aucun programme RPG |
| EXP530 / EXP531 | Comptage journalier des palettes pour facturation du stockage (produits finis / papier) | RSOC, CPDATE | `CPPALNM` non mouvementées, `CPPALMO` mouvementées, `CPPALHN` hors norme (EXP531) ; lues par `FEN_FacturationKimmel` ; tables vides sur NP11F |

**Statuts**

- `EXP509.HENBL` : blanc = saisi ; `'E'` = palettes en cours de pointage (PHP) ou BL supprimé ; `'O'` = basculé en BL (affiché « Livré », plus modifiable).
- `EXP509.HSTOCKP = 'O'` : stock déjà payé par le client → BL **non facturable**.
- `PROD_ENTITE.STATUT` : 0 déclaré … **4 conditionné** (exigé pour transformer) → **5 expédié** (posé par EXP540) ; `'2'` = en stock (comptage EXP530) *(supposé)*.
- `EXP517.DPPICK = 'O'` : palette en picking → transformation refusée.

## Programmes

| Programme | Type | Rôle | Pré-BL ? |
| --- | --- | --- | --- |
| **EXP540** / **EXP540NAV** | RPG + CL | Transformation pré-BL → BL, appelée par le WinDev (voir [fiche](EXP540.md)) | Oui |
| **EXP535P2** (2 351 l., DL oct. 2022) | RPG impression | **Édition du pré-BL** (fond de page) : lit EXP509, EXP510 (curseur joint à NEG100 et EXP516, regroupé par article/appellation/dossier), EXP516, EXP517, EXP512, A500F, NEG100T… ; recalcule colis/poids en mémoire (même règle que EXP540, avec `TESTN` sur le poids palette, l. 955-958 et 1450-1462) ; n'écrit aucune table. Paramètre : société, BL début/fin, poids palette en 27-29. `EXP535P2SA` = sauvegarde | Oui |
| **EXP530NAV** | CL | Point d'entrée WinDev « Imprimer » (`FEN_GestionPreBL` l. 511) : selon la version d'OS (`QSS1MRI`) appelle EXP530NA1 ou EXP530NA2 ; `MONMSG CPF0000` | Oui |
| **EXP530NA2** | CL | `OPNQRYF` sur EXP509 (société + n°) puis EXP535P2 avec `OVRPRTF … TOSTMF(/Home/PDF/Temp/PREBLssssnnnnnn.PDF) WSCST(*PDF)` ; le WinDev ouvre ce PDF | Oui |
| EXP530NA1 | CL | Variante V5 : spoule converti par `CVTSPLFPDF` en `BLssssnnnnnn.PDF` ; **son OPNQRYF porte sur EXP009 au lieu d'EXP509** (défaut, version morte d'après la cartographie : 0 jour d'usage) | Oui (obsolète) |
| EXP530CLP | CL | Édition par lot du pré-BL (OUTQ, copies, fonds de page) via EXP535P2 ; dérivé de l'ancien CL d'édition des BL (1998-2001) ; aucun appelant trouvé, 0 jour d'usage | Oui (dormant) |
| EXPEMPNA1 / EXPEMPNA2 | CL | « PREBL groupé pour envoi par email » via EXP535P2 (cartographie ; non lus) | Oui |
| DOS159P3 / DOS159P4 | RPG | Étiquettes palettes de pré-BL Codesoft (GRA, GRA PPG) (cartographie ; non lus) | Oui |
| **EXP530** | RPG (DL mai 2023) | **Comptage quotidien des palettes** pour la facturation du stockage : supprime puis réécrit EXP530 (palettes PROD_ENTITE statut 2 non mouvementées / mouvements A900F du jour) et EXP531 (palettes papier AN00F, sorties/entrées A700F, réceptions). Paramètres : société, date, 5 dépôts, sorte, 2 types de stock. Appelant inconnu (travail planifié *(supposé)*) ; tables vides sur NP11F | Non (facturation logistique) |
| EXP525 | RPG 5250 | Visualisation d'un BL multi-dossiers en fond de page (écran NEG525FM) — option VB d'EXP010S ; lit EXP009/010/015/016/012 ; aucune écriture | Non (BL) |
| EXP500 / EXP500CLP | RPG 5250 + CL | Écran de paramètres (date, BL début/fin, transporteur, enseigne) puis soumission `PRTBCH00` de l'**édition des bordereaux de transport** : EXP500CLP fait un `OPNQRYF` sur EXP009 et appelle le programme du format du transporteur | Non (BL, 1997-2007) |
| EXP501P, P2, P5, P3, P4, P6, P7, P8 | RPG impression | Bordereaux de transport : standard, LACOMBE, GRAVELEAU, DANZAS, DPD, GEFCO (désactivé), La Poste (écrit le fichier EXPPOS), import GEFCO ; lisent EXP009/EXP010/EXP015/AB001/AA0003 | Non |
| EXP550 / EXP550CLP / EXP550P | RPG 5250 + CL + impression | État de facturation des transporteurs (lit P£EXP009) | Non |

## Points d'attention pour la réécriture

- Le préfixe EXP5xx regroupe deux générations : les **éditions de transport des années 1997-2007** (EXP500/501/550) et le **pré-BL 2022-2023** (EXP509-517, EXP530NAV, EXP535P2, EXP540). Seule la seconde concerne le pré-BL.
- Le pré-BL est un **brouillon de BL dans des tables parallèles** ; dans la réécriture, un BL « en préparation » (statut) dans les tables BL éviterait la recopie, à condition de garder le **n° définitif réservé dès la saisie** (R1 d'EXP540).
- Écritures concurrentes sur EXP509/EXP517 : WinDev (saisie, PRTLCK), PHP (pointage, `HENBL = 'E'`), EXP540 (bascule), EXP010S (suppression → `'E'`). Aucune transaction nulle part.
