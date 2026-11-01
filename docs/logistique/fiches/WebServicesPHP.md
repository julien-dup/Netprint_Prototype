# Web services PHP des terminaux code-barre (rétro-documentation)

Sources lues (lecture seule) : `D:\docs\Netprint\web services\` — `rest_netprint`, `rest_netprintGRA`, `rest_netprintBEN`,
`rest_nortier` (≈ 100 000 lignes PHP, 1er novembre 2026). Les `API_*.md` présents dans ces dossiers ont servi de point de
départ ; ils sont globalement fidèles pour les paramètres et les étapes, mais ne signalent ni l'absence de transaction, ni les
URL écrasées entre contrôleurs, ni les points d'appel sans session (voir § 10).
Croisements : `cartographie-db2\colonnes.csv`, `carto_tables.csv`, `carto_programmes.csv`.
*(supposé)* = déduit, non vérifié dans le code ou sur NP11F.

Objectif : préparer la reprise (ou l'interfaçage) par l'application Blazor du module Logistique — pré-BL palettes
(EXP509/510/512/516/517/520), OPR (OPR010/015/016), palettes SSCC (PROD_ENTITE, PROD_COLISAGE), stock (A700F, A900F, AL00F,
AP00F, AK00F), BL (EXP009/010/015/016).

---

## 1. En bref

- Une application PHP par client, installée sur l'IBM i (PHP sous Apache, extension `ibm_db2`, connexion `*LOCAL`), appelée en
  REST/JSON par les terminaux code-barre (tablettes, pistolets) de l'atelier et de l'expédition.
- Toutes les écritures se font **sans transaction** (autocommit), en **SQL concaténé**, et en GET pour la moitié d'entre elles.
- Les PHP **n'écrivent jamais les BL** (EXP009/010/015/016) ni EXP510/EXP512 : ils préparent (palettes, colis, pré-BL,
  quantités préparées de l'OPR) ; le BL est créé ensuite par le WinDev (OPR, PreBL) ou le RPG (EXP540). Ils lisent
  `PROD_ENTITE.NUMERO_BL` / `PROD_COLISAGE.NUMERO_BL` pour l'affichage.
- Tables écrites par la logistique PHP : **PROD_ENTITE, PROD_COLISAGE, A700F, A900F, OPR015 (ODQPREP), EXP509 (HENBL),
  EXP516 (DCLOTU), EXP517, EXP520 (DESTAT), DOS010, AB0020, SPF010, SYN100, STOS20, AA00082 (compteurs), SESSIONS, USERS_TAB**.
- Après chaque mouvement, appel de la procédure **STOREVNAV** (recalcul des positions de stock AK00F/AL00F/AP00F *(supposé :
  « Revalorisation stock cde, résa » dans la cartographie)*) : les PHP n'écrivent jamais AK00F/AL00F/AP00F directement.

---

## 2. Organisation technique

### 2.1 Cadre et routage
- Framework : **RestServer de Jacob Wright** (`RestServer.php`, `RestFormat.php`, `RestException.php`), routes déclarées par
  l'annotation `@url VERBE /chemin/$param` dans le commentaire de chaque méthode. Le fichier `restler.php` (Luracast Restler 3)
  est présent mais **n'est pas chargé** par `index.php`.
- `index.php` charge les contrôleurs puis `$server->addClass(...)` ; `.htaccess` réécrit toute URL vers `index.php` et ouvre le
  CORS (`Access-Control-Allow-Origin: *`, méthodes GET/POST/OPTIONS/DELETE/PUT).
- Mode `production`, mais `refreshCache()` est appelé à chaque requête : la table des routes (`urlMap.cache`) est reconstruite
  à chaque appel.
- **Collision de routes** : la table est un tableau `[verbe][url]` ; quand deux classes déclarent la même URL, **la dernière
  classe ajoutée gagne**. Conséquences dans `rest_netprint` (ordre : Articles, GRAPPG, PET, Bobines, PAO, Matieres, Feuilles,
  ArticlesPalettes, Conso, PapierMatiereUnifie) :
  - `getEntite`, `getUnitesEmplacements`, `ajouteUnitesDansCarton`, `retireUnitesCarton`, `supprimeCarton`, `clotureCarton`,
    `ouvreCarton`, `editeEtiquetteCarton` → servis par **ArticlesControllerPET**, pas par ArticlesController ;
  - `APDepots`, `APGenereEntreeSimple`, `APGenereTransfertDepotDetail`, `APGenereTransfertDepotGlobal`, `APGenereSortie` →
    servis par **ArticlesPalettesController**, pas par la copie de GRAPPG ;
  - `GRAGetEntite`, `GRAEmplacementsTransit`, `FEretourMagasin`, `FEregroupementUnites`, `FEControlePapier`, `fournisseur`,
    `inventaire/$inventaire` → servis par **PapierMatiereUnifieController**.
  - Dans `rest_netprintGRA`, `APGenereEntreeSimple` est servi par **ArticlesControllerCFP** (ajouté en dernier).
- Paramètres : `$session` dans le chemin ; le reste en **query string** (`$_GET`) pour les GET, en **JSON brut** dans le corps
  (`php://input`) pour les POST (souvent un tableau `sscc: [...]`).
- Réponse : toujours HTTP 200, JSON de la forme `{"erreurs":[{"erreur":"1"}]}` = succès ; tout autre texte = message d'erreur
  (parfois accompagné de la requête SQL fautive, renvoyée au terminal). `{"erreur":"2"}` + `quantites` = demande de
  confirmation (dépassement du contractuel, déclaration de fabrication).

### 2.2 Authentification et autorisations
- **Aucune authentification HTTP** : aucune classe n'implémente `authorize()` ; le OAuth2 est entièrement commenté.
- Session applicative : `POST /login` (`PPGlogin`, `PMUlogin` selon l'appli) lit `USERS_TAB` (`login` + `mdp = md5hash_hex(mdp)`,
  fonction SQL côté IBM i), crée une ligne `SESSIONS (ID = GUID, LOGIN, DEMARRE, EXPIRE = +8 h)` et met à jour
  `USERS_TAB.LASTCONNECTION`. Les variantes GRA/PPG/PMU joignent `B100F` pour renvoyer le dépôt de l'utilisateur.
- Chaque point d'appel « avec session » appelle `ArticlesController::controleSession($session)` (lecture `SESSIONS` non
  expirée) et récupère le login, écrit ensuite dans `OPERATEUR_*` de PROD_ENTITE.
- **Les droits `AUTO_*` de USERS_TAB** (déclaration fab, mise en stock, sortie prépa, conditionnement, transferts, destruction,
  inventaire, expédition, déstockage…) sont **renvoyés au terminal au login mais jamais contrôlés côté serveur** : c'est le
  terminal qui masque les fonctions.
- Points d'appel **sans aucune session** (URL sans `$session`) qui écrivent : `POST /repriseStock/`, `GET /PPGrepriseStock/$sscc`,
  `GET /PPGprelevementPalette/$sscc` ; lecture sans session : `POST /controleSSCC2`, `GET /PPGgetEntite/$sscc`,
  `GET /editeFicheExpedition/$prebl`, `GET /GRAediteEtiquetteSSCC/$sscc` (ce dernier lance une édition), `GET /APDepots/`,
  `GET /PETgetMatieresCarton/`, `GET /getOFArticlesPalettes/$dossier` (nortier).
- La colonne `SESSIONS.LOGIN` n'est jamais rapprochée d'une société : la société vient du fichier `custom.ini` de l'installation.

### 2.3 Connexion DB2
- `dbcommon.php` : identifiants DB2 **en dur** (utilisateur / mot de passe d'un profil technique — présents dans les 4
  variantes, non recopiés ici), lecture de `custom.ini`, passage des paramètres en variables globales.
- `dbconnection.php` : `db2_connect("*LOCAL", uid, pwd, ['i5_lib' => 'NP11F', 'autocommit' => DB2_AUTOCOMMIT_ON])` —
  **bibliothèque NP11F en dur**, autocommit forcé. Une connexion par appel de méthode (`db_connect()` répété).
- Programmes IBM i : appelés soit comme procédures SQL (`call storevnav('…')`, `call EVE100NAV('…')`, sans bibliothèque :
  liste de bibliothèques du profil *(supposé)*), soit via `call QSYS2.QCMDEXC('call NP11P/DOS15xNAV PARM(''…'')')` —
  **bibliothèque programmes NP11P en dur**.
- Autres fichiers contenant des secrets (non recopiés) : `test_db.php` (identifiants de connexion), `sendSMTPmail.php`
  (mot de passe SMTP en dur), `rest_netprint/custom.ini` (section `smtp_*` avec mot de passe). Le dossier `.vs` de
  `rest_netprint` contient un historique de conversation Copilot (à ne pas diffuser).

### 2.4 custom.ini (paramètres de l'installation)
Clés fonctionnelles (les clés `etiquette*`, `FE*`, `MA*`, `M2*`, `SF*` décrivent la mise en page des étiquettes PDF) :

| Clé | Rôle | netprint | GRA | BEN | nortier |
|---|---|---|---|---|---|
| `as400srl` | n° de série / nom du système (non utilisé pour la connexion, `*LOCAL`) | présent | présent | présent | présent |
| `societe` | **société unique de l'installation** (`$psoc`) | 29 | 29 | 28 | 55 |
| `nomSociete` / `villeSociete` | étiquettes | PETREL | GRAPHIDECOR | PRINTOR PACKAGING | NORTIER PACKAGING |
| `depotPrincipal` / `depotSurplus` | dépôt contractuel / dépôt surplus | 1 / 2 | 1 / 2 | 1 / 2 | 1 / 2 |
| `depotPrincipalPaye`, `depotKimmel`, `depotKimmelPaye` | stock payé et entrepôt externe Kimmel | — | 3, 21, 23 | — | — |
| `depotQuarantaine` | dépôt de quarantaine (lu seulement par nortier) | 0 (non lu) | 0 | — | 40 |
| `depotsDeprecies[]` | dépôts exclus | 4, 24, 34 | 4 | 4 | 4, 24 |
| `emplacementMS` | emplacement de mise en stock par défaut | 888 | 888 | PROD | 888 |
| `emplacementPrepa` | emplacement de préparation OPR | PREPA | PREPA | PREPA | PREPA |
| `emplacementPREBL` | emplacement des palettes pointées sur pré-BL | PREPA | DESTOCK | DESTOCK | — |
| `emplacementTransit` / `emplacementDestockage` | transit Kimmel / déstockage | — | TRANSIT / DESTOCK | TRANSIT / DESTOCK | — |
| `typeStockPF` | type de stock produits finis (MVTSTO, PLTSTO…) | P1 | P1 | P1 | P1 |
| `partieFixeSSCC` | préfixe GS1 ajouté aux SSCC saisis sur 9 chiffres | présent | vide | présent | présent |
| `mouvementTransfert` | transfert de dépôt (A700F) | TD | TD | TD | TD |
| `mouvementReception` | réception | EC | EC | EC | EC |
| `mouvementSortieSimple` / `mouvementEntreeSimple` | sortie / entrée simple | SS / ES | SS / ES | SS / ES | SS / ES |
| `mouvementDestruction` | destruction | DU | DU | DU | DU |
| `mouvementPrepa` | transfert d'emplacement vers pré-BL (A900F) | TP | TP | TP | — |
| `mouvementDestockage` / `mouvementTransfertLog` | déstockage / transfert logistique | — | TS / TK | TS / TK | — |
| `mouvementLiberation` | libération de quarantaine (lue, non utilisée) | — | — | — | LI |
| `cheminPDF*` | dossiers des étiquettes PDF générées | présent | présent | présent | présent |

Anomalie de configuration : dans `rest_netprint`, `dbcommon.php` ne lit ni `emplacementDestockage`, ni `mouvementDestockage`,
ni `depotQuarantaine`, ni les dépôts payés/Kimmel : les points d'appel GRAPPG de déstockage/Kimmel et
`APLiberationQuantiteQuarantaine` y tournent avec des valeurs vides *(supposé : non utilisés sur cette installation)*.

---

## 3. Variantes : qui utilise quoi

| Dossier | Contrôleurs chargés | Client(s) | Circuit |
|---|---|---|---|
| `rest_netprint` | Articles, **GRAPPG**, **PET**, ArticlesPalettes, Bobines, PAO, Matieres, Feuilles, Conso, PapierMatiereUnifie (`pointage_controller` présent mais commenté) | installation « générique » ; `custom.ini` = PETREL soc 29 ; DOS155NAV = « Réédition SSCC depuis PHP (PET) » → **PETREL** ; DOS156NAV = « Édition palette EXP depuis PHP (GRA PPG) » | OPR + cartons/colis (PET), pré-BL palettes (GRAPPG), palettes génériques (AP) |
| `rest_netprintGRA` | ArticlesController de `articles_controllerGRA.php`, **ArticlesControllerCFP**, Bobines, PAO, Matieres | **Graphidecor** (soc 29 dans l'ini, dépôts payé/non payé, entrepôt Kimmel) ; **CFP** = CoffretPack *(supposé, d'après la liste des clients pré-BL de `logistique-analyse.md`)* | pré-BL palettes (GRA : palettes choisies d'avance dans EXP517 ; CFP : palettes ajoutées au pointage) |
| `rest_netprintBEN` | Articles, Bobines, PAO, Matieres, Feuilles, Conso, PapierMatiereUnifie, **Inventaires** (`articles_palettes_controller.php` présent mais non chargé) | **Printor Packaging** soc 28 (BEN = site Bénard *(supposé)*) | surtout papier/bobines/matières (bobines imprimées, gaufrage), inventaires ; OPR/cartons possibles via Articles |
| `rest_nortier` | **Robot**, Articles, Bobines, PAO, ArticlesPalettes | **Nortier Packaging** soc 55 | palettes avec quarantaine (dépôt 40, mouvement LI), robot de palettisation |

Différences de code notables :
- `articles_palettes_controller.php` : identique netprint/BEN ; nortier écrit le **login** dans `MVPRO`/`TEPRO` au lieu de
  `'TABCAB'`, ne génère pas de mouvement pour une quantité nulle, et lit la quarantaine.
- `articles_controller.php` : BEN et nortier réactivent les contrôles « l'unité n'est pas dans le bon dépôt » commentés dans
  netprint ; nortier joint `EXP005` et `AL00F` (stock du lot) dans `getofarticles` ; netprint contrôle la **DLU** (`AL00F.PLDLU`)
  au contrôle PREP.
- `articles_controllerGRA.php` (Graphidecor) ≈ `articles_controllerGRAPPG.php` + login/OPR/inventaire ; GRAPPG ajoute les
  préfixes `PPG…` (pré-BL avec filtre dépôts surplus/dépréciés, reprise de stock, prélèvement) et l'édition DOS156NAV.
- `pointage_controller.php` identique partout.
- Compteurs d'usage (cartographie) : DOS157NAV (fiche d'expédition GRA/CFP) dernier usage 2023 ; DOS156/158/159NAV utilisés en
  2026. Les compteurs IBM i étant peu fiables, ne pas en déduire que CFP est abandonné.

---

## 4. Conventions communes

### 4.1 PROD_ENTITE (unité = palette, carton, unité SSCC)
Colonnes (nom SQL / nom système) : SOCIETE, NUMERO_SSCC/NUMER00001 (CHAR 26), ARTICLE, INDICE, QUANTITE_EX/QUANT00001,
NUMERO_OF, CLE_OF, STATUT, DATE_HEURE_FABRICATION, OPERATEUR_FABRICATION, DEPOT, EMPLACEMENT (CHAR 8),
OPERATEUR_MISE_EN_STOCK, OPERATEUR_SORTIE_STOCK, NUMERO_OPR, NUMERO_LIGNE_OPR, NUMERO_COLISAGE, NUMERO_LIGNE_FABRICATION,
NUMERO_LIGNE_BL, NUMERO_BL, ELEMENT_BL, SSCC_ORIGINE, SSCC_RETOUR, BL_RETOUR, PALETTE_GROUP, DATE_ENT_STOCK, DATE_SOR_STOCK,
TYPE_PALETTE, CDT_FINAL, OBS. Les PHP utilisent indifféremment les noms SQL et système (`NUMER00001`, `QUANT00001`).

Statuts tels que le code les pose et les teste :

| Statut | Sens dans le code PHP | Posé par (PHP) |
|---|---|---|
| 0 | déclaré (SSCC pré-imprimé, non fabriqué) | jamais créé par PHP (créé à l'édition des étiquettes *(supposé : WinDev/RPG)*) ; `PPGprelevementPalette` décrémente une unité en 0 |
| 1 | fabriqué (déclaré par la sortie de fabrication) | `genereSortieFab`, `GRAGenereSortieFab`, `APGenereSortieFab` (création en 1) |
| 2 | en stock | transferts, mises en stock, entrées simples, reprises, regroupements, dépointage |
| 3 | en préparation (OPR) / pointé sur pré-BL ou déstockage | `generePrepa`, `preBLPointePalette`, picking pré-BL, `destockagePointePalette`, `retireUnitesCarton` |
| 4 | conditionné (dans un colis OPR) / chargé (pré-BL) | `ajouteUnitesDansCarton`, `preBLExpediePalette`, `PMUdestockageChargePaletteNavette` |
| 5 | expédié — **mais aussi** « unité vidée vers la prépa » | `APGenereSortiePrepa(Picking)` (quantité mise à 0) ; le passage à 5 avec n° de BL est fait hors PHP (WinDev/Blazor) |
| 6 | détruite / soldée / absorbée (quantité 0) | `genereSortie`, `APGenereSortie`, regroupements, transfert global, dépointage d'une palette de picking |
| 7 | testé comme « inutilisable » (message) ; = **retourné** côté BL (Blazor `SaisieBlDb2.Retour`) | jamais posé par PHP |

Préfixes de NUMERO_SSCC créés par PHP : `P` + 8 chiffres (compteur AA00082 `RTABLE='G1'`), `G` + 8 chiffres pour une palette
groupée (compteur `GG`). Pseudo-unités « globales » (dépôts gérés sans SSCC) : NUMERO_SSCC = dépôt(3) + article(15) +
indice(2) + lot, ou article + indice + lot pour le stock global de l'emplacement PREPA (MERGE, voir § 5.6).

### 4.2 Lot, mouvements, journal des transferts
- Lot = `NUMERO_OF` sur 5 chiffres + `CLE_OF` (ex. `12345A`).
- **A700F** (mouvement de stock) — colonnes écrites selon les cas : RSOC, MVAA, MVMM, MVJJ, MVTSTO (P1), MVDEP, MVCMVT, MVSENS
  (E/S), MVREF, MVIND, MVFAM, MVSFAM (SPF010.AFAM/ASFAM), MVQTUS, MVUS='UN', MVPRO='TABCAB' (login chez nortier), MVEMP, MVLOT,
  MVDEPX (dépôt de contrepartie), MVOBS (= SSCC, ou 'REPRISE SSCC'), MVLOTX, MVBON, MVDATS, MVDOS/MVDCLE (commande), MVLIGB
  (ligne DOS010), MVPUUS/MVPUUP/MVUP (valorisation), MVCHRO. Un transfert de dépôt = deux lignes S puis E avec le code `TD`.
- **A900F** (transfert d'emplacement) — insert positionnel des 16 colonnes : RSOC, TEAA, TEMM, TEJJ, TETSTO, TEDEP, TECMVT (`TE`,
  `TP` pré-BL, `TS` déstockage), TELOT, TEEMPO, TEEMPD, TEREF, TEIND, TEQTUS, TEUS='UN', TEPRO='TABCAB', TEOBS = SSCC.
- Après chaque écriture de stock : `call storevnav(soc(4) + typeStock(2) + dépôt(3) + article(15) + indice(2))`.
- Valorisation des entrées de fabrication (`MVPUUS`) selon `SOP20` (AA0007) : 3 = prix de revient article (APRVT), 4 = prix de
  vente de la commande / TNUM de l'unité de vente, 5 = prix de revient commande / TNUM ; **TNUM = 999999999 (« Le tout »)**
  → divisé par la quantité commandée ; valeur > 99999 remise à 0.

### 4.3 Déclaration de fabrication (commun à plusieurs points d'appel)
DOS010 (historique des sorties de fab) : `rsoc, sfndos, sfclef, sfnfab (= max+1), sfdsaa/mm/jj, sfqtef, sftypd ('P' partiel,
'D' soldé), sfnbuc, sfdest='', sfart, sfind, sfemb1/2=0, sfnuc2=0, sfpoid=0, sfdosc/sfclec (commande), sfdep, sfemp)` ;
A700F code `MS` sens E ; `AB0020.LAQTEF += qté` et `LASOLD='O'` si soldé ou si la quantité lancée est atteinte ;
`SYN100 (HTSOC, HTDOSS, HTDCLE, HTDATE, HTHEUR, HTSTAT=' ', HTACT='SF')` si le dossier est en sous-traitance (`CCOP11='O'`).

---

## 5. Points d'appel logistiques

Notation : **L** = tables lues, **É** = écritures (colonnes), **St** = statut PROD_ENTITE posé, **Pgm** = programmes appelés,
**Tx** = transaction. Sauf mention, Tx = **aucune** (autocommit ; arrêt au premier échec sans annulation des écritures déjà faites).

### 5.1 Authentification
| URL | Verbe | Paramètres | Effet |
|---|---|---|---|
| `/login` | POST | JSON `user`, `mdp` | L USERS_TAB ; É `SESSIONS` (ID, LOGIN, DEMARRE, EXPIRE +8 h), `USERS_TAB.LASTCONNECTION` ; renvoie session + droits AUTO_*. SQL concaténé (injection possible sur `user`). |
| `/PPGlogin` (GRAPPG), `/login` (GRA), `/PMUlogin` | POST | `user`, `mdp`, `depot` | idem + jointure B100F (dépôt, DDIR) et droits logistiques (AUTO_ENVOILOG, AUTO_RECEPDESTOCK, AUTO_RECEPLOG, AUTO_RANGELOG, AUTO_GROUPEREF, AUTO_EXPE, AUTO_DESTOCK…). |
| `/logout/$session` | GET | — | `DELETE FROM SESSIONS WHERE ID=…`. |
| `/testConnexion` | GET | — | test de vie. |

### 5.2 Contrôles et consultations (lecture seule)
- **`POST /controleSSCC/$session`** (Articles) — JSON `sscc[]`, `typeControle`, `emplacement`, `opr`, `ligneOpr`. Vérifie
  l'existence (requête préparée, seule requête paramétrée du module), puis selon `typeControle` :
  `MES` (déclaration : statut = 0), `MSP` (mise en stock emplacement : statut 1/2/3 et emplacement différent), `MSS`
  (surplus : statut 1/2), `SVM` (retour du surplus : dépôt surplus, statut 1/2), `PREP` (prépa OPR : statut 0/1/2, DLU non
  dépassée, cohérence OF ↔ commande de la ligne OPR via AB0020), `COL` (colisage : statut 3 et même OPR ; refuse 4, 5, 6, 7),
  `SOR` (sortie : refuse 0, 3, 4, 5, 6, 7). L PROD_ENTITE, SPF010, AB001, AA0003, AL00F.
- **`POST /APControleSSCC/$session`** (palettes) — mêmes principes + transferts de dépôt détail/global (`depoto`, `depotd`).
- **`POST /PPGcontroleSSCC/$session`** (GRAPPG) / `/controleSSCC` (GRA) — types supplémentaires : `ENK` envoi Kimmel, `REK`
  réception Kimmel (emplacement de transit STO050, mauvais dépôt), `RAK` rangement Kimmel, `REG` regroupement, `APG`/`APGP`
  ajout sur palette groupée, `REP` reconditionnement, `TRD` transfert de dépôt (**refusé si la palette est dans un pré-BL EXP517
  ou une demande de déstockage EXP520 non soldée**), `PREPI` picking pré-BL (doit être dans EXP517 avec DPPICK='O'), `EXP`
  chargement (statut 3, emplacement pré-BL, présent dans EXP517, déjà « pické »). Refuse une unité non `G` à quantité 0.
- **`POST /CFPcontroleSSCC/$session`** — contrôles CFP (ligne de pré-BL `hnum`/`dnlig`, EXP510/EXP517).
- `POST /controleSSCC2` (sans session) — contrôle pour inventaire / reprise (B100F, STO050).
- `GET /getEntite/$session?sscc=` (servi par PET), `/GRAGetEntite`, `/APGetEntite`, `/PPGgetEntite/$sscc` — fiche d'une unité
  (complète le SSCC de `partieFixeSSCC` s'il fait 9 caractères ; joint EXP009 pour afficher le BL).
- `GET /getUnitesEmplacements/$session?emplacement=` — contenu d'un emplacement.
- `GET /getQuantitesStockArticle/$session?article=` — stock par dépôt (AP00F/AL00F, hors dépôts de type `S`).
- `GET /getEmplacementsArticle/$session?article=` — emplacements (AP00F) + unités.
- `GET /getofarticles/$session?dossier=&article=` — articles à fabriquer d'un OF (AB001/AB0020).
- `GET /APControleOF`, `/APControleArticle`, `/APDepots/` (B100F), `/GRAEmplacementsTransit/` (STO050).

### 5.3 Déclaration de fabrication et entrées
| URL | Verbe | Paramètres | Règles et écritures |
|---|---|---|---|
| `/genereSortieFab/$session` (Articles) | POST | `dossier`, `ligne` (AB0020), `quantite`, `sscc[]`, `asolder`, `depot` | Refuse si un SSCC n'est pas en 0 ou si la somme des quantités ≠ `quantite`. É § 4.3 (DOS010, A700F MS, AB0020, SYN100) ; **St 1** : `UPDATE PROD_ENTITE SET STATUT=1, DATE_HEURE_FABRICATION, OPERATEUR_FABRICATION, DEPOT, EMPLACEMENT=emplacementMS, NUMERO_LIGNE_FABRICATION` ; `SPF010.ADTFAB, ADRDOS, ADRCLE` (dernière fabrication). Pgm STOREVNAV. |
| `/GRAGenereSortieFab/$session` | POST | `dossier`, `article`, `indice`, `quantite`, `sscc[]`, `asolder` | idem (Graphidecor). |
| `/APGenereSortieFab/$session` (palettes) | **GET** | `dossier`, `ligne`, `depot` (0 = contractuel + surplus), `qteg`, `qteContrat`, `asolder`, `ignore`, `nb1..10`/`qte1..10`/`excedent1..10` (nombre de palettes × quantité, excédent → surplus) | Si gestion contractuelle et dépassement : renvoie `erreur 2` + quantités (confirmation). Sinon DOS010 + A700F MS ventilés contractuel/surplus ; AB0020 ; si soldé en sous-quantité : **Pgm EVE100NAV** (événement de rappel) ; **crée les palettes** `INSERT PROD_ENTITE (SOCIETE, NUMERO_SSCC='P'+compteur G1, ARTICLE, QUANTITE_EX, NUMERO_OF, CLE_OF, STATUT='1', INDICE, DEPOT, EMPLACEMENT, DATE_HEURE_FABRICATION, OPERATEUR_FABRICATION, NUMERO_LIGNE_FABRICATION)` + étiquette PDF ; dépôt « global » : MERGE d'une pseudo-unité (St 2). SYN100 'SF'. |
| `/APGenereEntreeSimple/$session` | GET | `dossier`, `article`, `indice`, `depot`, `emplacement`, `datemvt`, `qteg`, `nbN/qteN` | A700F code `ES` ; palettes `P…` **St 2** (ou MERGE global) ; SYN100 'SF'. Version CFP dans GRA. |
| `/PMUreceptionArticle/$session` | GET | `unite`, `depotTablette`, `emplacementTablette` | Réception d'une palette produit fini dans le dépôt de la tablette : A700F TD S/E, A900F, `PROD_ENTITE.DEPOT`, St 2. |

### 5.4 Mise en stock, transferts, reprises
| URL | Verbe | Paramètres | Règles et écritures |
|---|---|---|---|
| `/genereTransfertEmplacement/$session`, `/APGenereTransfertEmplacement/$session` | POST | `sscc[]`, `emplacement` | A900F (TE) ; **si l'unité était en 3 : `OPR015.ODQPREP = max(0, ODQPREP − qté)`** (annulation de prépa) ; `PROD_ENTITE SET EMPLACEMENT, STATUT='2', NUMERO_OPR=0, NUMERO_LIGNE_OPR=0, OPERATEUR_SORTIE_STOCK='', OPERATEUR_MISE_EN_STOCK=login` ; STOREVNAV. |
| `/genereTransfertDepot/$session` | POST | `sscc[]`, `depot`, `emplacement` | A700F TD S (dépôt origine) + E (destination), A900F, `PROD_ENTITE SET DEPOT, EMPLACEMENT, STATUT='2'` ; STOREVNAV × 2. |
| `/APGenereTransfertDepotDetail/$session` | POST | `sscc[]`, `depoto`, `depotd`, `emplacement`, `destinationDetailOuGlobal` | idem ; vers un dépôt global : unité mise à **6** (qté 0) et MERGE de la pseudo-unité du dépôt (St 2). |
| `/APGenereTransfertDepotGlobal/$session` | GET | `dossier`, `ligne`, `depoto`, `depotd`, `qteg`, `nbN/qteN` | Dépôt global → détail : A700F TD, A900F, décrément de la pseudo-unité, création de palettes `P…` St 2. |
| `/GRAGenereTransit/$session` | POST | `sscc[]` (ou palette groupée), `emplacement` | Envoi vers Kimmel : A900F TE, `EMPLACEMENT` = transit (statut inchangé). |
| `/GRAGenereReceptionKimmel/$session` | POST | `sscc[]`, `depotPaye`, `depotNonPaye` | Dépôt destination selon `B100F.DFACTUR` (payé / non payé) : A700F TD S/E, `PROD_ENTITE SET DEPOT, STATUT='2'`. |
| `/GRAStockageKimmel/$session` | POST | `sscc[]`, `emplacement` | Rangement : A900F TE, `EMPLACEMENT`, St 2. |
| `/PMUgenereTransfertDepot`, `/PMUgenereTransit` | GET | `unite`, `papOuMatOuArt`, dépôts, emplacement | même logique, étendue aux bobines/matières (AN00F). |
| `/APGenereRetourMagasin/$session` | GET | `sscc` ou `article`/`indice`/`dossier`, `qteg`, `emplacement` | Retour de la prépa vers le stock : A900F, décrément de la pseudo-unité PREPA, réactivation/création d'une unité St 2. |
| `/APLiberationQuantiteQuarantaine/$session` (nortier) | GET | `dossier`, `ligne`, `depot`, `qteg`, `nbN/qteN/excedentN` | Quarantaine (dépôt 40) → contractuel/surplus : A700F code **TD** (le code `LI` de `custom.ini` est lu mais jamais utilisé), A900F, palettes `P…` St 2. |
| `/repriseStock/` | POST, **sans session** | `sscc[]`, `depot`, `emplacement` | Reprise d'une unité « déclarée » : A700F `SS` (obs 'REPRISE SSCC') puis `ES`, `PROD_ENTITE SET DEPOT, EMPLACEMENT, STATUT='2', DATE_HEURE_FABRICATION, OPERATEUR_*='REPRISE'`. |
| `/PPGrepriseStock/$sscc` | GET, **sans session** | `emplacement` | Conversion stock lot → unité : unité en 0 requise, stock AP00F suffisant ; A700F SS/ES ; `PROD_ENTITE SET EMPLACEMENT, STATUT='2', DATE_ENT_STOCK` ; **`SPF010.AGESTU='2'`** (article passé en gestion par unité). |

### 5.5 Sorties
| URL | Verbe | Paramètres | Règles et écritures |
|---|---|---|---|
| `/genereSortie/$session` | POST | `sscc[]`, `type` (D = destruction `DU`, sinon `SS`), `bon` | A700F S (MVBON) ; **St 6** (`UPDATE … WHERE numero_sscc IN (…)` **sans filtre société**) ; SYN100 `HTACT='SQ'` (article, indice, quantité, dépôts) si sous-traitance. |
| `/APGenereSortie/$session`, GRAPPG `/APGenereSortie` | GET | `sscc` ou dossier/article (global), `qteg`, `type`, `bon`, `obs` | partielle : `QUANTITE_EX -= qté` ; totale : qté 0, St 6. |
| `/PPGprelevementPalette/$sscc` | GET, **sans session** | `quantite` | Prélèvement sur une unité **en statut 0** (`QUANTITE_EX -= qté`), sans mouvement de stock. |
| `destruction.php` | script | — | Destruction d'unités statut < 3 : A700F DU, St 6 *(script hors routeur)*. |

### 5.6 Préparation OPR (circuit « OPR avec colis/sacs »)
| URL | Verbe | Paramètres | Règles et écritures |
|---|---|---|---|
| `/getOpr/$session`, `/PETgetOpr/$session` | GET | `opr`, `article` | L OPR010/OPR015/OPR016, AB001, AA0003, SPF010, NEG100, A500F, AK00F (stock hors dépôts `S`, en cours de fab), AB0020 (lots affectés), puis PROD_COLISAGE + PROD_ENTITE (colis, contenu, poids, NUMERO_BL). Adresse de livraison : A500F si `DNLIGA>0`, sinon NEG100, sinon client. |
| `/generePrepa/$session` (Articles), `/GRAgenerePrepa/$session` | POST | `opr`, `ligneOpr`, `quantite`, `sscc[]` | **1)** `OPR015 SET ODQPREP = ODQPREP + quantite` (en premier) ; 2) A900F TE vers PREPA par lot ; 3) unités d'un autre dépôt (St 1/2) : A700F TD S/E vers le dépôt principal (pas en GRA) ; 4) unités en 0 : **déclaration de fabrication** § 4.3 (DOS010 type P, A700F MS avec MVPUUS/MVPUUP, AB0020, SYN100) ; 5) **St 3** : `PROD_ENTITE SET STATUT=3, OPERATEUR_SORTIE_STOCK, EMPLACEMENT=PREPA, DEPOT=principal, NUMERO_OPR, NUMERO_LIGNE_OPR` (+ données de fabrication pour les 0) ; STOREVNAV. |
| `/APGenereSortiePrepa/$session` | POST | `sscc[]` | Variante « prépa globale » : surplus → A700F TD ; A900F vers PREPA ; unité **mise à 5, qté 0** ; MERGE d'une pseudo-unité PREPA (article+indice+lot) St 2. |
| `/APGenereSortiePrepaPicking/$session` | POST | `sscc`, `qteg`, `depot` | idem en partiel : unité à 5 si vidée, sinon reste en 2 avec qté diminuée. |
| `/ajouteCarton/$session` (Articles), `/PETajouteCarton/$session` | GET | `opr`, `codeMatiere` (PET) | `INSERT PROD_COLISAGE (SOCIETE, CARTON = NUMERO_COLISAGE (max+1 de l'OPR), NUMERO_OPR, CLOTURE='N', OPERATEUR_COLISAGE, MATIERE_CARTON)`. |
| `/ajouteUnitesDansCarton/$session` (PET) | POST | `opr`, `carton`, `sscc[]` | Refuse si colis inexistant ou clôturé ; **St 4** : `PROD_ENTITE SET STATUT=4, NUMERO_COLISAGE` (filtre société + OPR). |
| `/retireUnitesCarton/$session` | POST | `opr`, `carton`, `sscc[]` | **St 3**, `NUMERO_COLISAGE=0`. |
| `/supprimeCarton/$session` | GET | `opr`, `carton` | unités du colis → St 3 ; `DELETE PROD_COLISAGE`. |
| `/clotureCarton/$session` | GET | `opr`, `carton`, `poids`, `hauteurPalette` (PET) | `PROD_COLISAGE SET CLOTURE='O', POIDS, HAUTEUR_PALETTE` + étiquette de contenu. |
| `/ouvreCarton/$session` | GET | `opr`, `carton` | `CLOTURE='N'` (**sans vérifier que le colis n'est pas déjà sur un BL**). |
| `/calculePoidsCarton/$session` (PET) | GET | `opr`, `carton` | calcul du poids (C000F, PROD_COLISAGE). |
| `/PETclotureOPR/$session` | GET | `opr` | Contrôles : aucun colis ouvert, toutes les unités de l'OPR en 4 avec colis, plus de ligne `ODQTEA > ODQPREP` pour un article stocké (`ASTK='O'`) ; puis **envoi d'un courriel** à l'adresse du profil créateur (`AI00F.ACMELE`). **N'écrit rien** (ni OPR010, ni BL). |
| `/editeEtiquetteCarton/$session` | GET | `opr`, `carton` | étiquette PDF (FPDF, `cheminPDFCarton`). |
| `/PETgetMatieresCarton/` | GET | — | liste des matières d'emballage (C000F). |

> « StockArticle » / OPR avec sacs : aucun fichier PHP ne porte ce nom. Le circuit correspond à ArticlesController +
> ArticlesControllerPET (OPR, colis = sacs/cartons, poids, clôture) *(supposé)*. La **génération du BL** depuis l'OPR n'est pas
> en PHP : elle est faite par le WinDev OPR (`FEN_CreationBL`), reprise par Blazor en L7, qui pose `PROD_ENTITE.STATUT='5'`,
> `NUMERO_BL`, `ELEMENT_BL`, `NUMERO_LIGNE_BL` et `PROD_COLISAGE.NUMERO_BL`, `ELEMENT_BL`.

### 5.7 Pré-BL palettes — Graphidecor (GRA) et GRAPPG
Le pré-BL (EXP509 entête, EXP510 éléments, EXP516 lots, EXP517 palettes, EXP512 texte) est créé par le WinDev PreBL, qui choisit
les palettes d'avance dans EXP517 (`DPPICK='O'` = palette à entamer). Le terminal pointe ensuite les palettes.

| URL | Verbe | Paramètres | Règles et écritures |
|---|---|---|---|
| `/preBL/$session` (GRA/GRAPPG) | GET | `prebl`, `depot`, `depotPaye` | L EXP517 + PROD_ENTITE + EXP509 (HSTOCKP : stock payé O/N filtre le dépôt) + AA0003 ; exclut `HENBL='O'` (basculé en BL) ; statut affiché 3 = OK, 4 = Chargé. |
| `/PPGpreBL/$session` | GET | `prebl`, `depot`, `depotPaye`, `depotSurplus`, `depotDeprecie` | idem + EXP510 (commande, ligne). |
| `/preBLPointePalette/$session` | GET | `prebl`, `palette` (SSCC ou palette groupée G) | Pour chaque unité (hors G) : A900F code `TP` vers `emplacementPREBL` si besoin ; **St 3** + `EMPLACEMENT=emplacementPREBL` ; STOREVNAV ; `EXP509.HENBL='E'` (en cours). |
| `/PPGpreBLPalettePicking/$session`, `/preBLPalettePicking/$session` | GET | `sscc`, `prebl`, `qteg`, `element`, `ligne517` | Crée une palette de picking : `INSERT PROD_ENTITE (…, NUMERO_SSCC='P'+G1, QUANTITE_EX=qteg, STATUT='3', DEPOT, EMPLACEMENT=pré-BL, SSCC_ORIGINE=sscc)` ; A900F TP ; `QUANTITE_EX -= qteg` sur l'origine ; `EXP517 SET DPSSCC='P…', DPPICK='N', DPOBS='Picking sur <sscc>'` (élément/ligne) ; 2 étiquettes (DOS158NAV). |
| `/preBLDepointePalette/$session` | GET | `prebl`, `palette` | Palette de picking : **St 6** qté 0, quantité rendue à `SSCC_ORIGINE` (St 2), A900F retour, `EXP517 SET DPSSCC=origine, DPPICK='O', DPOBS=''` ; palette entière : **St 2** (l'emplacement reste celui du pré-BL). `EXP509.HENBL=' '` si plus aucune palette pointée. |
| `/preBLExpediePalette/$session` | POST | `prebl`, `sscc[]` | Chargement : A900F TP vers PREPA, **St 4** + `EMPLACEMENT=PREPA`. |
| `/editeFicheExpedition/$prebl` (sans session) | GET | `depot`, `profil` | **Pgm DOS156NAV** (fiche/étiquette palette d'expédition Codesoft). |
| `/GRAediteEtiquetteSSCC/$sscc`, `/editeEtiquetteSSCC/$session` | GET | `sscc`, `profil` | **Pgm DOS158NAV** (GRA), **DOS159NAV** (Articles), **DOS155NAV** (PET). |

Regroupement / reconditionnement (Graphidecor) :
| URL | Verbe | Effet |
|---|---|---|
| `/GRARegroupementPalette/$session` | POST `sscc[]`, `qteg` | unités sources **St 6** qté 0 (**UPDATE sans filtre société**), nouvelle palette `P…` St 2 avec la quantité globale + étiquette. Pas de mouvement de stock. |
| `/GRACreerPaletteGroupee/$session` | GET | crée une palette groupée `G…` (compteur GG) St 2, quantité 0. |
| `/GRAAjoutePalette/$session`, `/GRARetirePalette/$session` | GET `sscc`, `groupage` | `PROD_ENTITE.PALETTE_GROUP = G… / ' '`. |
| `/GRAAjoutePalettePicking/$session` | GET `sscc`, `groupage`, `qteg` | palette `P…` St 2 (`PALETTE_GROUP`, `SSCC_ORIGINE`), origine décrémentée. |
| `/GRAReconditionnePalette/$session` | GET `sscc`, `qteg` | palette `P…` St 2 avec `CDT_FINAL='O'`, origine décrémentée. |
| `/APTransfertPalette/$session` | GET `sscco`, `ssccd`, `quantiteo`… | regroupement de deux palettes : origine St 6 qté 0, destination `+qté`, A900F. |

### 5.8 Pré-BL CFP (palettes ajoutées au pointage, palettes d'expédition mixtes)
| URL | Verbe | Paramètres | Règles et écritures |
|---|---|---|---|
| `/CFPpreBL/$session` | GET | `prebl`, `depot`, `depotPaye` | L EXP510 + EXP516 + EXP517 + PROD_ENTITE : quantité préparée, nb palettes par ligne, clôture. |
| `/CFPpreBLPointePalette/$session` | GET | `prebl`, `palette`, `hnum`, `dnlig` | A900F TP, **St 3** + emplacement pré-BL, **`INSERT EXP517 (RSOC, DNBL, DNUM, DNLIG, DPSSCC, DPQTEP=quantité, DPPICK='N')`**, `EXP509.HENBL='E'`. |
| `/CFPpreBLPalettePicking/$session` | GET | `sscc`, `paletteExpe` (`prebl-n°palette`), `qteg`, `hnum`, `dnlig` | palette `P…` St 3 avec `SSCC_ORIGINE`, `TYPE_PALETTE='PICK'`, `NUMERO_COLISAGE` = n° de palette d'expédition (EXP516.DNPALM) ; A900F ; origine décrémentée ; `INSERT EXP517 (…, DPOBS='Picking sur …')`. |
| `/CFPdepointePalette/$session` | GET | `prebl`, `palette`, `hnum`, `dnlig` | picking : St 6 + `NUMERO_COLISAGE=0`, quantité rendue à l'origine, **`DELETE EXP517`**, **`DELETE A900F`** (journal effacé par TEOBS = SSCC) ; palette entière : St 2, A900F retour, `DELETE EXP517` ; `HENBL=' '` éventuel. |
| `/CFPcloturePrepa`, `/CFPdecloturePrepa` | GET | `prebl`, `hnum`, `dnlig` | `EXP516.DCLOTU = 'O' / ' '`. |
| `/CFPpreBLPalettesExpe`, `/CFPdetailPrepa`, `/CFPlistePalettes` | GET | — | lectures EXP510/516/517. |
| `/CFPediteFicheExpedition/$prebl` | GET | `depot`, `profil` | **Pgm DOS157NAV**. |

### 5.9 Déstockage (EXP520 / EXP520D)
| URL | Verbe | Règles et écritures |
|---|---|---|
| `/destockage/$session` | GET `destockage`, `depot`, `depotPaye` | L EXP520D + EXP520 + PROD_ENTITE. |
| `/destockagePointePalette/$session` | GET `destockage`, `palette` | A900F code `TS` vers `emplacementDestockage`, **St 3** + emplacement ; `EXP520.DESTAT='S'` (soldé) quand toutes les palettes de la demande sont en 3 à l'emplacement de déstockage. **Ne vérifie pas que la palette appartient à la demande** (filtre commenté). |
| `/destockageDepointePalette/$session` | GET | **St 2** (emplacement inchangé), `DESTAT=' '`. |
| `/PMUdestockage*`, `/PMUdestockageChargePaletteNavette/$session` | GET `destockage`, `palette`, `papOuMatOuArt`, `depotDestination` | même logique étendue aux bobines/matières (AN00F.PNEMP/PNSTAT) ; chargement navette : **St 4**. |

### 5.10 Inventaire
| URL | Verbe | Effet |
|---|---|---|
| `/inventaireSSCC/$session` | GET `inventaire` | L STOS10E. |
| `/pointeSSCC/$session`, `/APPointeSSCC/$session` | POST `inventaire`, `emplacement`, `sscc[]` | `INSERT STOS20 (SOCIETE, INVENTAIRE, EMPLACEMENT, SSCC, A_SUPRIMER, QUANTITE_POINTEE)` (insert positionnel), sans mouvement de stock. |
| BEN `/INVemplacement`, `/INVgetInventaire`, `/INVunite`, `/INVpointe` | GET | inventaire tournant : STOS20, STOC20. |

---

## 6. Autres points d'appel (une ligne chacun)

**BobinesController** (papier en bobines, AN00F/A700F/A1001/A1002/BOBINE/STOC20) : `commandeFrn` commande fournisseur ;
`commandesAVenir` réceptions attendues ; `receptionCommandeFrn` réception (crée AN00F + A700F, A1002) ; `decoupeBobine`
découpe en bobines filles ; `impressionBobine`/`gaufrageBobine` (BEN) bobines imprimées ; `BOgenereSortie` sortie ;
`BOEntreeSimple` entrée hors commande ; `dossier`/`resaDossier` réservations d'un OF ; `bobine`, `consoBobine`,
`nbBobinesRestant`, `bobinesRef`, `bobinesEmp`, `bobinesFrn`, `emplacement`, `depot` consultations ; `transfertDepot`,
`transfertEmplacement` transferts ; `BOsortieMagasin`/`BOretourMagasin` (BEN) ; `inventaire`, `pointeBobines` inventaire
(STOC20) ; `rechercheSS`/`recherchePapier` (BEN) ; `editeEtiquette` étiquette ; appelle **DIR010NAV** (marqueurs).

**MatieresController** (encres, colles, consommables ; préfixe `MA`) : commande/réception fournisseur, entrée simple, transfert
dépôt, réservation d'OF, consultation unité/référence/emplacement/fournisseur, contrôle matière, sortie, étiquette (AN00F, A700F,
A1001/A1002 ; BEN écrit aussi C000F).

**FeuillesController** (papier en feuilles ; préfixe `FE`) : commande/réception fournisseur, sortie/retour magasin, regroupement
d'unités, sortie, contrôle papier, fournisseur, entrée simple, **refente**, inventaire, étiquette.

**ConsoController** : `controleResa` contrôle de réservation ; `unite` fiche ; `genereConsoMatiere`, `genereConsoFeuille`,
`genereConsoBobineMetrage`, `genereConsoSemiFini` consommations sur OF (A700F, AN00F, AJ001/AJ002, A20001/A20002 ; semi-fini :
PROD_ENTITE).

**PapierMatiereUnifieController** (`PMU…`, appli unifiée papier/matière/semi-fini/article) : login, commandes et réceptions
fournisseur (article, matière, papier), rangement, transfert d'emplacement/dépôt/transit, sortie et annulation de
consommation, déstockage (§ 5.9), entrée simple, refente, consultations (unités par référence, emplacements, contenu
d'emplacement, réservations), éditions d'étiquettes (feuille, matière, article, article PDF) ; appelle **DIR010NAV**,
**DIR012NAV**, **DOS158NAV**.

**PAOController** : `getOFPAO`, `getArticlesOFPAO` OF en attente de PAO ; `majStatutPAO` met à jour le statut PAO (AB001).

**PointageController** (non chargé dans netprint) : matricules, entrées/sorties, production, début/fin d'opération,
recherches (PAT101F, PAT103F).

**InventairesController** (BEN) : login/logout, inventaire par emplacement/unité, pointage (§ 5.10).

**RobotController** (nortier) : `getOFArticlesPalettes/$dossier` — articles et palettisation d'un OF pour le robot (lecture).

Scripts hors routeur : `etiquette.php`, `editeEtiquetteDirect.PHP`, `code39.php`, `articleJPEG.php` (image article),
`convertion.php` (compteurs de lots NL / matières, chrono), `destruction.php`, `evaluerExpression.php`, `sendSMTPmail.php`.

---

## 7. Programmes IBM i appelés

| Programme | Appel | Rôle (cartographie) | Appelants |
|---|---|---|---|
| STOREVNAV | `call storevnav('…')` procédure SQL | revalorisation / recalcul des positions de stock (AK00F, AL00F, AP00F *(supposé)*) | toutes les écritures de stock |
| EVE100NAV | `call EVE100NAV('…')` procédure SQL | événement avec rappel (sous-quantité soldée) | `APGenereSortieFab` |
| DOS155NAV | QCMDEXC `NP11P/DOS155NAV` | réédition SSCC Codesoft (PET) | `PETediteEtiquetteSSCC` |
| DOS156NAV | QCMDEXC | édition palette EXP Codesoft (GRA PPG) | `editeFicheExpedition` |
| DOS157NAV | QCMDEXC | édition palette EXP Codesoft (GRA) | `CFPediteFicheExpedition` |
| DOS158NAV | QCMDEXC | réédition SSCC Codesoft (GRA) | `GRAediteEtiquetteSSCC`, PMU |
| DOS159NAV | QCMDEXC | réédition SSCC Codesoft | `editeEtiquetteSSCC` |
| DIR010NAV / DIR012NAV | QCMDEXC | marqueurs (papier, matière) | bobines, feuilles, matières, PMU |

---

## 8. Anomalies (à ne pas reproduire)

1. **Aucune transaction** : autocommit forcé à la connexion, aucun `db2_commit`/`db2_rollback`. Une erreur au milieu d'un
   enchaînement laisse un état partiel. Exemple : `generePrepa` incrémente `OPR015.ODQPREP` **avant** toute autre écriture ;
   un échec ensuite laisse la quantité préparée gonflée sans palette en 3. Les appels STOREVNAV ne sont pas contrôlés.
2. **SQL concaténé partout** (sauf une requête de contrôle d'existence) : injection possible via `user` au login, `sscc[]`,
   `prebl`, `palette`, `emplacement`… ; les valeurs numériques ne sont pas typées.
3. **Écritures en GET** (pointage pré-BL, déstockage, cartons, sorties de fab AP…) : rejouables par un simple rechargement ou
   un proxy.
4. **Pas d'authentification HTTP**, droits `AUTO_*` non contrôlés côté serveur, points d'appel d'écriture **sans session**
   (`repriseStock`, `PPGrepriseStock`, `PPGprelevementPalette`), CORS `*`.
5. **Identifiants en dur** (DB2, SMTP) ; bibliothèques NP11F/NP11P en dur.
6. **Compteurs non verrouillés** (`attributionGS1`/`GG` : SELECT puis UPDATE de `AA00082.TNUM` sans verrou ni transaction) :
   deux terminaux simultanés peuvent obtenir le même n° de palette `P…`. Dans `APGenereSortieFab`, la boucle incrémente avant
   usage : un numéro est sauté à chaque appel. `NUMERO_COLISAGE` (= max+1) et `DOS010.SFNFAB` (= max+1) ont la même faiblesse.
7. **UPDATE sans filtre société** : `genereSortie` (statut 6), `GRARegroupementPalette`, `genereTransfertEmplacement`
   (`WHERE numero_sscc=…` seul) — un SSCC identique dans une autre société serait modifié.
8. **Précédence AND/OR** : `preBLPointePalette`, `preBLDepointePalette`, `CFPpreBLPointePalette` écrivent
   `societe=X and quantite_ex>0 and (sscc=…) or (PALETTE_GROUP=… …)` : la branche « palette groupée » ignore la société et la
   quantité.
9. **Statut 5 surchargé** : `APGenereSortiePrepa` met à 5 (« expédié ») une palette simplement vidée dans la prépa globale ;
   statut 6 désigne à la fois destruction, solde, regroupement et palette de picking annulée.
10. **Routes écrasées** (§ 2.1) : le code « visible » dans `articles_controller.php` n'est pas celui qui répond pour les
    cartons et `getEntite` dans `rest_netprint`.
11. **Cohérence non vérifiée** : `ouvreCarton`/`retireUnitesCarton`/`supprimeCarton` ne vérifient pas que le colis ou l'unité
    n'est pas déjà sur un BL (statut 5, `NUMERO_BL>0`) ; `destockagePointePalette` ne vérifie pas l'appartenance de la palette à
    la demande ; `preBLDepointePalette` remet une palette entière en 2 sans la ramener à son emplacement d'origine ;
    `CFPdepointePalette` efface des lignes du journal A900F (perte de traçabilité).
12. **Messages techniques renvoyés au terminal** (texte SQL complet en cas d'erreur).
13. Le login GRA/PPG/PMU lit des colonnes `AUTO_*` (AUTO_ENVOILOG, AUTO_EXPE…, AUTO_RETOUR_MAG…) absentes du catalogue
    `USERS_TAB` de la cartographie NP11F *(supposé : colonnes ajoutées sur les bases clients seulement)*.

---

## 9. Ce que les PHP ne font pas (frontière avec Blazor)

- **BL** : aucune écriture dans EXP009/EXP010/EXP015/EXP016. Le passage pré-BL → BL est fait par le RPG **EXP540** (qui pose
  `EXP509.HENBL='O'` *(supposé)*) ou par le WinDev PreBL ; le BL d'OPR par le WinDev OPR (repris en Blazor, L7).
- Pas de création d'OPR (OPR010/015/016) ni de modification d'OPR010 : seul `OPR015.ODQPREP` est mis à jour.
- Pas de création de pré-BL (EXP509/510/512/516) : seuls `EXP509.HENBL`, `EXP516.DCLOTU` et EXP517 (CFP : insert/delete ;
  GRA : update) sont touchés.
- Pas de création des unités en statut 0 (étiquettes pré-imprimées) *(supposé : WinDev EtiquettesCodesoft / RPG DOS15x)*.
- Pas d'écriture directe de AK00F/AL00F/AP00F (déléguée à STOREVNAV).

---

## 10. Contrat à respecter si Blazor reprend les écritures

Tant que les terminaux PHP restent en service, l'application Blazor écrit dans **les mêmes lignes** : il faut garder ces
invariants (et les tester sur NP11F).

**PROD_ENTITE**
- Clé fonctionnelle `SOCIETE + NUMERO_SSCC` ; SSCC `P` + 8 chiffres pris dans `AA00082 RTABLE='G1'`, `G` + 8 chiffres dans
  `RTABLE='GG'` — Blazor doit **prendre ses numéros dans les mêmes compteurs**, en verrouillant la ligne (UPDATE … puis SELECT
  dans la même transaction) pour ne pas entrer en collision avec les PHP.
- Cycle de statuts : 0 → 1 (déclaration) → 2 (stock) → 3 (prépa OPR / pointé pré-BL / déstockage) → 4 (colis OPR / chargé)
  → 5 (sur BL) ; 6 = soldée / détruite / absorbée ; 7 = retournée. Les PHP refusent une unité en 4, 5, 6, 7 pour la plupart des
  opérations : **poser 5 avec NUMERO_BL/ELEMENT_BL/NUMERO_LIGNE_BL à la création du BL** (déjà fait en L7) suffit à la soustraire
  aux terminaux ; la suppression de BL doit **remettre 4** (fait) pour que le colis redevienne modifiable.
- Colonnes que les PHP lisent et que Blazor doit tenir à jour : `QUANTITE_EX` (0 = épuisée), `DEPOT`, `EMPLACEMENT` (PREPA,
  emplacement pré-BL, transit, déstockage), `NUMERO_OPR`, `NUMERO_LIGNE_OPR`, `NUMERO_COLISAGE`, `PALETTE_GROUP`,
  `SSCC_ORIGINE` (picking), `NUMERO_BL` (affiché par `getEntite`).
- Pseudo-unités « globales » (dépôt + article + indice + lot ; article + indice + lot à PREPA) : à ne pas confondre avec des
  palettes ; Blazor ne doit pas les afficher comme SSCC ni les effacer.

**PROD_COLISAGE** : `SOCIETE + NUMERO_OPR + NUMERO_COLISAGE` (= max+1 par OPR), `CLOTURE` 'N'/'O', `POIDS`, `HAUTEUR_PALETTE`,
`MATIERE_CARTON` ; Blazor lit les colis clôturés et pose `NUMERO_BL`/`ELEMENT_BL`. Les PHP pouvant rouvrir un colis déjà sur
un BL, prévoir côté Blazor un contrôle à la génération (colis clôturé, unités en 4) — déjà fait — et, si les PHP sont modifiés,
leur ajouter le refus « colis sur BL ».

**OPR015.ODQPREP** : incrémenté par `generePrepa`, décrémenté par les transferts d'emplacement d'une unité en 3. Blazor
(situation de la préparation, génération des BL) doit le lire comme la quantité préparée et ne pas le recalculer à partir des
palettes sans tenir compte de ce mode d'écriture.

**Pré-BL** : EXP517 (`DNBL, DNUM, DNLIG, DPSSCC, DPQTEP, DPPICK, DPOBS`) est partagé : le WinDev/Blazor crée les lignes (GRA),
les PHP les modifient (picking) ou les créent/suppriment (CFP). `EXP509.HENBL` : ' ' (rien), 'E' (pointage en cours, posé par
PHP), 'O' (basculé en BL : les PHP ignorent alors le pré-BL). `EXP516.DCLOTU` 'O' = préparation de la ligne close (CFP).
L'emplacement de pré-BL (`emplacementPREBL`) et le code mouvement `TP` viennent du `custom.ini` : à reprendre en paramètres
de société dans Blazor.

**Déstockage** : `EXP520.DESTAT` ' ' / 'S' calculé à partir des palettes en 3 à l'emplacement de déstockage.

**Stock** : toute écriture Blazor de mouvement doit suivre le même format A700F (MVPRO, MVOBS = SSCC, MVLOT = lot OF,
MVDEPX/MVLOTX pour les transferts, codes TD/MS/ES/SS/DU/TP/TS de `custom.ini`), journaliser les changements d'emplacement dans
A900F (TEOBS = SSCC) et relancer **STOREVNAV** (ou un équivalent natif qui met à jour AK00F/AL00F/AP00F de la même façon)
pour chaque couple dépôt/article/indice touché.

**Déclaration de fabrication** (si Blazor la reprend un jour) : DOS010 (SFNFAB max+1), A700F MS valorisé selon SOP20 et
TNUM (« Le tout »), AB0020.LAQTEF/LASOLD, SYN100 'SF' en sous-traitance, SPF010.ADTFAB.

**Sessions et utilisateurs** : USERS_TAB et SESSIONS sont propres aux terminaux ; Blazor n'a pas à les écrire, mais une
reprise des terminaux devrait remplacer ce mécanisme (jeton, droits vérifiés côté serveur, société de la session).

**Recommandation pour une interface** *(supposé)* : plutôt que de réécrire les terminaux d'emblée, exposer depuis Blazor une
API équivalente pour les seules opérations critiques (pointage pré-BL, préparation OPR, colis) en **une transaction par
appel**, SQL paramétré, mêmes tables et mêmes statuts, puis faire basculer les terminaux client par client (PETREL,
Graphidecor/PPG, CoffretPack, Printor, Nortier) en conservant les URL et le format de réponse `{"erreurs":[{"erreur":"1"}]}`.
