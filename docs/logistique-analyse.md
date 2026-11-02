# Logistique : vue d'ensemble avant de démarrer (31/10/2026)

Synthèse de ce qui existe (rétro-doc, cahier des charges, mesures d'usage, analyses des projets WinDev) pour le module
SaaS « Stocks et logistique », volet livraisons. Les points marqués *(supposé)* sont des déductions.

## 1. Documentation existante (`D:\docs\Netprint`)

| Dossier / fichier | Contenu | Usage pour le SaaS |
|---|---|---|
| `BL-bons-de-livraison\fiches\EXP010S.md`, `EXP010.md` (+ `notes\`) | Rétro-doc du 5250 : liste / gestion des BL (EXP010S, ~6 500 l.) et création / mise à jour d'un BL (EXP010, ~9 300 l.) | Référence des règles du BL |
| `BL-bons-de-livraison\note-analyse-bl-lot1-windev.md` | **Cahier des charges validé (v3, 15/10/2026)** de la saisie des BL produit fini / négoce et des BL divers : écrans, règles, transaction, stock, port, verrous, pièges, décisions | **Spécification à reprendre telle quelle** pour le SaaS (elle visait ConsultationBL en WinDev) |
| `BL-bons-de-livraison\inventaire-sources-bl.md` | Inventaire des sources BL, EXP540, numérotation GETBL, STOREVNAV / MAJPMP / STOCK03, logiques EXP010 | Socle technique |
| `BL-bons-de-livraison\mesures-usage-bl.md` | **Usage réel des BL sur 12 mois, par serveur et société** | Priorités (§ 3) |
| `BL-bons-de-livraison\glossaire-metier-bl.md`, `analyse-stock-lots-EXP010.md` | Glossaire, contrôle de stock et répartition lots / emplacements | Règles de stock |
| `OPR\RETRODOCUMENTATION.md`, `DOC_UTILISATEUR.md`, `REVUE_DE_CODE.md` | Rétro-doc, guide et revue du WinDev OPR (avril 2026, en partie dépassée : voir § 4) | Circuit OPR |
| `Mouvements de stock\fiches\` (MAJPMP, STOCK03-04, STO06x, GETNUM…) | Mouvements et positions de stock | Sorties de stock du BL |
| `Etiquettes-codesoft\` | Étiquettes (DOS15x) | Étiquettes palettes / colis, plus tard |

**Web services PHP** (articles_controller, …GRA, …GRAPPG, …CFP, StockArticle) : aucune rétro-doc ; la cartographie
(`carto_tables.csv`, colonne WEBSERVICES_PHP) indique seulement les 35 tables qu'ils touchent (A700F, A900F, AL00F,
AP00F, EXP009, EXP509, EXP517, EXP520, OPR010, OPR015, PROD_ENTITE…). Sources absentes de ce poste.

## 2. Circuits de livraison

| Circuit | Création du BL | Préparation | Clients (d'après David et les mesures) |
|---|---|---|---|
| **A. Saisie au 5250** (sans code-barre) | EXP010 / EXP010S | Livraisons à effectuer (WinDev, lecture) + état des préparations de commande **EXP078** | soc 1, 2, 22, 23, 24, 26, 27, 28, 30… : la majorité des sociétés |
| **B. OPR** | WinDev OPR (`FEN_CreationBL`, écriture directe) | OPR010 / 015 / 016, préparation et colisage (PROD_ENTITE, PROD_COLISAGE) | Printor étiquettes, Gouzy, Petrel (soc 8 : 50 %, soc 9 : 88 %, soc 31) |
| **C. OPR avec code-barre** (toutes les unités de produit fini identifiées, SSCC) | WinDev OPR (`FEN_CreationBL`), repris en L7 | OPR ; sortie de stock, préparation (PROD_ENTITE statut 3), cartons et clôture (PROD_COLISAGE, statut 4) par le web service PHP `articles_controller.php` | 2 clients |
| **D. Pré-BL palettes code-barre** | RPG **EXP540** (transformation du pré-BL) | WinDev PreBL / PreBLSansPalette + web services PHP GRA / GRAPPG / CFP (pointage des palettes SSCC) | Graphidecor, Printor Étuis, CoffretPack, Draeger (soc 20 : 98 %, soc 29 : 94 %, soc 25, soc 8 : 7 %) |

Commun à tous : liste / consultation des BL (ConsultationBL WinDev, EXP010S), édition (EXP030 → EXP035P2), certificats
de conformité (EXP045), colisage (EXP046), envoi (EnvoideBL : Outlook), facturation (FAC*), retours.

## 3. Ce que disent les mesures d'usage (12 mois, 4 serveurs)

- **BL produit fini (type 2) = presque tout** ; négoce (type 3) marginal ; **aucun BL de fabrication** (type 1 / plan de
  livraison EXP020) ni BL vierge : ces parcours peuvent attendre ou disparaître.
- **Saisie à partir des lignes de commande** : majoritaire (serveur 3 à 100 %, sociétés 1, 2, 24, 28, 30).
- **BL multi-dossiers** très fréquents sur certaines sociétés (jusqu'à 61 % des éléments) : l'ajout d'un dossier à un BL
  est indispensable.
- **BL divers** et **retours** partout, en petits volumes (dizaines à centaines par an).
- Multi-dépôts (jusqu'à 70) et stock par emplacement : la répartition dépôt / lot / emplacement est nécessaire.

## 4. Points techniques clés

- **Tables BL** : EXP009 (en-tête), EXP010 (élément = un dossier sur le BL ; unité de facturation), EXP011 (lignes à
  facturer en plus), EXP012 (pied), EXP015 (lignes), EXP016 (lots / emplacements). Pré-BL : EXP509 / 510 / 512 / 516 / 517.
- **N° de BL** : `GETBL(soc, enseigne, 'P' | 'D')` (AXISRV) ; sous-traitance : SYN005.
- **Sur la commande** : `NEG100.DNQTEL += q`, `DNQTER = DNQTEC − DNQTEL`, DNSOLD si reste ≤ 0 ou « appel à solder » ;
  AB001 soldé quand toutes ses lignes le sont.
- **Stock** : A700F (transfert TR vers le dépôt principal, sortie EX, n° de BL sur chaque mouvement), contrôle
  CTRPOSSTK / CTRPOSEMP (jamais négatif sur toute la chronologie), puis STOREVNAV (MAJPMP, STOCK04, STOCK03), période
  de stock ouverte (AG00F).
- **Port** selon AA0007.SOP09 (1 reprise du devis, 2 calcul pendant le BL avec B200F / B300F / B400F, 3 saisie).
- **Existant fragile** : aucune transaction dans OPR, PreBL ni EXP540 ; compteurs lus puis incrémentés ; transformation
  pré-BL rejouable ; client facturé repris d'un dossier à l'autre (EXP540) ; anomalies OPR (ligne marquée livrée même
  partiellement, cartons sortis deux fois, lignes de frais sans quantité livrée).

## 5. Découpage proposé

| Étape | Contenu | Remplace |
|---|---|---|
| L1 ✅ | **Consultation des BL** : liste multicritère, fiche d'un BL (éléments, lignes, lots), BL facturés / facturables / retours, documents | ConsultationBL, EXP010S (consultation), EXP525 |
| L2 ✅ | **Édition du BL** en PDF natif (+ certificat de conformité, colisage plus tard) | EXP030 / EXP035P2 |
| L3 ✅ | **Saisie des BL produit fini / négoce** selon le cahier des charges lot 1 : création à partir des lignes de commande ou d'un OPR, ajout d'un dossier, modification, suppression, répartition dépôt / lot / emplacement, port, en une transaction | EXP010, `FEN_CreationBL` d'OPR |
| L4 ✅ | **BL divers** | EXP010 (BL divers) |
| L5 ✅ | **Livraisons à effectuer** + **état des préparations** (EXP078) en PDF, avec « Créer le BL » sur les lignes choisies | LivraisonsAEffectuer, EXP078 |
| L6 ✅ | **Envoi des BL** (Outlook / SMTP, journal ENV100 comme les ARC, 31/10) et **retours de marchandises** (31/10 : un seul chemin pour EXP010S option RM et le clic droit de ConsultationBL, palettes SSCC comprises) | EnvoideBL, EXP010S, `FEN_RetourBLSaisie` |
| L7 ✅ | **OPR** : gestion faite le 31/10 (liste, création à partir des lignes de commande, ajout, modification, suppression) ; édition de l'OPR (OPR150 en PDF natif) faite le 31/10 ; situation de la préparation (colisage), génération des BL selon le mode du client (AA0003.CDRES1 : par colis, par commande, par article, total) et finalisation (palettes, poids, pied) écrites le 31/10, testées sur NP11F le 01/11 (OPR 65980 : BL généré puis supprimé, base identique) | WinDev OPR |
| L8 | **Pré-BL palettes** (détail § 7) : L8a consultation, L8b transformation en BL, L8c saisie, L8d éditions | PreBL, PreBLSansPalette, EXP540, EXP535P2 |

Hors périmètre pour l'instant : BL de fabrication (plan de livraison EXP020), BL vierge, copie de BL, sous-traitance
(SYN100 / SYNBL), facturation (module Facturation).

## 6. Questions ouvertes

- `NEG100.DNQTER` dans le circuit OPR : le WinDev ne l'écrit pas ; qui le tient ?
- Les décisions du cahier des charges lot 1 (15/10) valent-elles pour le SaaS ? (*supposé* : oui)

## 7. Pré-BL palettes (L8), analyse du 01/11/2026

Fiches : `docs/logistique/fiches/PreBL.md` (WinDev PreBL et sa variante sans palette), `EXP540.md` (transformation en BL),
`PreBL-tables-et-programmes.md` (modèle de données, autres EXP5*), `WebServicesPHP.md` (terminaux code-barre).

**Circuit.** Le pré-BL (EXP509 en-tête, EXP510 lignes de commande, EXP516 lots, EXP517 palettes, EXP512 pied) est saisi
dans le WinDev, son n° est un n° de BL réservé par GETBL. Les préparateurs pointent les palettes sur les terminaux (PHP GRA /
CFP : PROD_ENTITE statut 3 puis 4, EXP517, EXP516.DCLOTU, EXP509.HENBL « E » en cours). Quand tout est pointé, « Transformer
en BL » appelle EXP540 : BL de même n° (EXP009/010/015/016/012), transfert TR puis sortie EX, palettes au statut 5, commande et
dossier mis à jour, EXP509.HENBL = « O ». Variante sans palette (EXP005.FEPREB = O, choisie par MenuNetprint) : on saisit une
quantité par lot, ce sont les scans qui créent EXP517.

**Ce que le SaaS doit respecter** : les terminaux PHP continuent d'écrire PROD_ENTITE, PROD_COLISAGE, EXP517, EXP516.DCLOTU,
EXP509.HENBL, A700F / A900F et les compteurs de palettes (AA00082 G1 / GG). Ils n'écrivent ni BL, ni EXP510, ni OPR (sauf
OPR015.ODQPREP).

| Étape | Contenu | Remplace |
|---|---|---|
| L8a ✅ | **Consultation des pré-BL** (fait le 01/11 : menu « Pré-BL palettes ») : liste (client, date, état « à préparer / en cours / prêt / livré »), fiche avec lignes, lots, palettes pointées ou non | FEN_GestionPreBL (liste) |
| L8b ✅ | **Transformation en BL** native (faite le 01/11 : bouton « Transformer en BL » de la fiche du pré-BL), en une transaction, avec le code de la saisie des BL : refus si une palette n'est pas pointée (statut 4), pré-BL déjà transformé ou vide ; sortie sur la quantité réelle des palettes | EXP540 / EXP540NAV |
| L8c ✅ | **Saisie du pré-BL** (faite le 01/11, récapitulatif palettes compris) : création, en-tête, lignes de commande, lots et palettes réservées (ou quantité seule si FEPREB = O), récapitulatif des palettes, suppression (refusée si livré) | FEN_CreationPREBL, FEN_SaisiePREBL, FI_AjoutPalettes* |
| L8d ✅ | **Éditions** (faites le 01/11 ; fiche `docs/logistique/fiches/Editions-PreBL.md`) : pré-BL en PDF, feuille de préparation, feuille camion | EXP535P2, éditions du WinDev |

Anomalies à ne pas reproduire (détail dans les fiches) : aucune transaction (WinDev, EXP540, PHP), erreurs avalées (« BL créé »
toujours annoncé), client facturé reporté d'un dossier à l'autre, valeurs reportées d'une ligne à l'autre, double transformation
possible, modification qui efface récapitulatif et clôture, palettes non réservées, pré-BL livré supprimable.

Réponses de David (01/11) :
1. **N° de pré-BL = n° de BL : on garde** (n° réservé par GETBL à la création du pré-BL).
2. **Lots « A PROD » (OF pas encore produit) : non utilisés** ; pas repris dans la saisie (L8c), refusés à la transformation.
3. **Colisage Hermès (e-SCM), facturation Kimmel du stockage et feuille camion : utilisés** ; à reprendre (L8d et suites).
4. SPF010.AOPT7 (« option div 7 ») : David ne sait pas. Seul usage trouvé : en pré-BL « stock payé », les lignes de commande
   dont l'article a AOPT7 = O ne sont pas proposées (FEN_AjoutLigneCde) ; aussi imprimé à part sur la feuille camion. Le
   libellé de l'option dans la fiche article dirait à quoi elle sert.
5. « OPR avec sacs » était une faute de frappe : **OPR avec code-barre** (toutes les unités de produit fini identifiées). Le
   PHP est `articles_controller.php` : il prépare (statut 3) et met en cartons (PROD_COLISAGE, statut 4) ; le BL reste créé
   par le WinDev OPR, déjà repris en L7 (la situation de la préparation lit justement ces palettes et ces cartons).
