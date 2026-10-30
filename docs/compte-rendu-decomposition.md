# Compte rendu — Lot E : décomposition analytique et prix remis

*Analyse du 30/10/2026. Aucun code écrit : à valider avant développement.*

## 1. Sources lues

- Branche `C:\Mes Projets\ConsultationDevis` (référence) et branche DLLNew, en mode texte :
  - `FEN_Decomposition` (≈ 1 600 lignes) ;
  - `FEN_Legendes`, `FEN_EnregLegende` ;
  - `FEN_Impression`, `ETAT_Decomposition`.
- Catalogue DB2 (`colonnes.csv`) : D5000F, D60001, D60002, D50001, D50007, DEV025E, DEV025D.
- Rétro-doc `ConsultationDevis.md`, règles R15 à R24 : elles concordent avec le code. Les détails ci-dessous les complètent.

**Point d'entrée** : bouton *Décomposition* du **détail** du devis seulement (`FEN_DetailDevis`), avec les paramètres devis, version, hypothèse et type de coût. Ce n'est pas dans le menu de la liste.

## 2. Ce que fait l'écran

### 2.1 Bandeau et totaux

- Le bandeau affiche le devis, le client (ou prospect), la désignation et la quantité devisée.
- Deux zones de saisie :
  - **Prix de vente remis** (`DEPVR`), saisi dans l'unité choisie ;
  - **unité de vente** : liste AA00083 `UV`. Son diviseur `TNUM` vaut 999999999 pour « le tout ».
- Totaux affichés : prix de revient, prix de vente et **coefficient de vente**.
- Changer d'unité relance le calcul.

### 2.2 Arbre du chiffrage

L'arbre a une colonne libellé, une colonne commentaire, les colonnes PR, PV et PV unitaire, puis 15 couples **valeur / %** (chapitres A à O). Seuls les chapitres présents dans la légende sont affichés.

```
Élément 1 (D60001)
├─ Papier  sorte sous-sorte grammage coloris LxH      ← D60001 DDDA1/DDDAPC … DDDO/DDDOPC
├─ Machines
│   └─ Section
│       └─ Opération (+ commentaire DDCOMF)         ← D60002 DDEA/DDEAPC … par DDLIG
├─ Matières
│   └─ Matière                                       ← D60002
└─ Sous-traitances
    └─ Lien fournisseur-opération                     ← D60002
Élément 31 Prépresse, Élément 32 Façonnage (pseudo-éléments)
Transport                                            ← D50001 DDTA/DDTAPC …
```

- **Papier** : valeur = `CONVUP(...) × DDPA`.
  - Pour une bobine (TPPERB), cette valeur est multipliée par un coefficient. Il se calcule à partir du nombre de bobines (DDCON), de la hauteur de fabrication (DDHAFT) et du nombre de tours (DDNTR/DDNTRG).
- **Fusion des lignes** : des lignes D60002 consécutives qui ont la même opération (ou matière) et le même commentaire sont fusionnées. Leurs valeurs et leurs `DDRSQT` s'additionnent.
- Une saisie sur un **nœud** recopie la valeur sur toutes les feuilles en dessous (`MAJColonnes`).
- Replier un nœud affiche son cumul ; le déplier affiche le détail.

### 2.3 Calcul (R20 à R23)

**PV remis total** = `PV saisi × DEQTED / TNUM`, ou le PV saisi tel quel pour « le tout ».

**Méthode B** (AA000Z.CFTYPE = `B`) :

- PR ligne = valeur ;
- PV ligne = valeur × PV remis total / (`DEPRA + DEMES + DEMRI + DEMCR + DEMFF + DEMFS + DEMFA + DEMFMA`).

**Autres méthodes** :

- PR ligne = coût direct majoré + valeur × (`DEMCO + DEMRI + DEMES + DEMCR`) / `DEPRA`.
- Le coût direct majoré dépend du type de ligne :

  | Ligne | Coût direct majoré |
  |---|---|
  | Papier | valeur × (1 + CKFG1 %), sans majoration si la provenance est `S` (stock) |
  | Opération | DDRSQT × DDPRMA × (1 + CKFG3 %) + DDRSQT × DDPRMO × (1 + CKFG2 %) |
  | Matière | valeur × (1 + CKFG5 %) |
  | Sous-traitance | valeur × (1 + CKFG4 %) |
  | Transport | valeur |

- PV ligne = PR ligne × PV remis total / `DEPR`.

**Pour toutes les méthodes** :

- PV unitaire = PV × `TNUM` / `DEQTED`, ou le PV pour « le tout ».
- L'écart d'arrondi (PR, PV, PV unitaire) est imputé sur la **dernière ligne** (R22).
- Les nœuds portent les cumuls de leurs feuilles.

### 2.4 Contrôles (R18), sur chaque feuille

Sens du % : il **répartit la ligne entre plusieurs chapitres**. Avec un seul chapitre renseigné, le % reste vide et vaut 100 % implicitement. Les colonnes `…PC` sont en S 2 0 (99 au plus) : 100 ne peut pas être stocké.

Messages, repris tels quels du WinDev :

- « La valeur doit être renseignée. » : un % est saisi sans valeur ;
- « Le pourcentage doit être renseigné. » : plusieurs valeurs sur la ligne, dont une sans % ;
- « La valeur n'existe pas dans la légende. » ;
- « La somme des pourcentages doit être égale à 100. » : contrôlé seulement si 0 < total < 100 ;
- « Au moins une valeur doit être renseignée. »

### 2.5 Validation (R19), dans l'ordre

1. Contrôle de toutes les lignes.
2. Si `TNUM` = 999999999, on le remplace par `DEQTED` ; même chose pour le TNUM d'origine.
3. On convertit les prix mini et conseillés dans la nouvelle unité : `DEPVM`, `DEPVMM`, `DEPVC`, `DEPVCM` × TNUM nouveau / TNUM ancien.
4. `UPDATE D5000F` : DEPVR, DEUNIT, DEPVM, DEPVMM, DEPVC, DEPVCM.
5. `DELETE` puis `INSERT` de la légende **D50007** : F7CHAPI, F7LIGNE (0 = titre du chapitre), F7TXT.
6. `UPDATE D60001` (papier) par élément.
7. `UPDATE D60002` par élément et DDLIG.
8. `UPDATE D50001` (transport).
9. L'écran propose ensuite d'imprimer la décomposition.

### 2.6 Légende et modèles (R15, R17)

- La légende (`FEN_Legendes`) comprend jusqu'à 15 chapitres. Chacun a un titre et jusqu'à 99 valeurs de 60 caractères.
  - Un chapitre peut être copié puis collé.
  - Contrôle : « La désignation doit être renseignée. »
- Les modèles (DEV025E en-tête, DEV025D détail) sont soit globaux (client 0/0), soit propres au client du devis ; ces derniers s'affichent en rouge. Le choix d'un modèle remplit la légende.
- `FEN_EnregLegende` offre trois actions :
  - **Créer** : un nom est obligatoire (« Le nom du modèle doit être renseigné. »). Le n° vaut max(MLNUM) + 1. On choisit entre global et client.
  - **Remplacer** : il faut un modèle choisi (« Une légende doit être renseignée. »). On supprime le détail, puis on le réinsère.
  - **Supprimer** : après confirmation, on supprime DEV025E et DEV025D.
  - Les chapitres sans titre sont ignorés, et les suivants sont renumérotés A, B, C…

### 2.7 Impression (R24)

- Le contrôle doit réussir avant l'impression.
- L'état liste, pour chaque chapitre (rupture) et chaque valeur, le libellé, le PV et le PV unitaire, avec un total par chapitre et un total général.
- L'en-tête reprend la société, le devis, la quantité, la désignation, le client ou prospect, et « pour TNUM » (ou « le tout »).

## 3. Anomalies relevées dans le WinDev

| # | Anomalie | Correction proposée |
|---|---|---|
| A1 | **Lignes fusionnées** : seul le **premier DDLIG** du groupe est mis à jour, les autres gardent l'ancienne ventilation. | Garder la liste des DDLIG de chaque ligne affichée et les mettre **tous** à jour. |
| A2 | Branche DLLNew : les pseudo-éléments sont 16/17 au lieu de 31/32. | Utiliser 31/32, comme la branche de référence. |
| A3 | Impression : le PV unitaire d'une valeur mélange les lignes avec % et sans %. Son résultat dépend de l'ordre des lignes. | Calculer PV unitaire = PV × TNUM / DEQTED à la fin, par valeur. |
| A4 | Un total des % **supérieur à 100** est accepté. | Refuser tout total différent de 100 (et de 0). Le message reste le même. |
| A5 | Validation : après le remplacement de « le tout » par DEQTED, le test `TNUM <> 999999999` est toujours vrai. La branche « sinon » ne sert jamais. | Sans effet sur le résultat. On ne la reprend pas. |
| A6 | Aucune transaction : une erreur au milieu laisse un devis à moitié écrit (la légende supprimée mais pas réinsérée, par exemple). | Une transaction si les fichiers sont journalisés (Q1). Sinon, contrôler tout avant d'écrire et écrire D5000F en dernier. |
| A7 | SQL construit par concaténation (apostrophes doublées à la main). | Requêtes paramétrées, comme dans les lots précédents. |
| A8 | `DEPVRM` (PV remis « 1000 + », lu par le tableau des prix) n'est pas recalculé quand le PV remis change. | À trancher (Q4). |
| A9 | Le n° de modèle est calculé par `max + 1`, sans protection si deux postes créent un modèle en même temps. | Insertion qui relit le n° (`FINAL TABLE`), en réessayant en cas de doublon. |

## 4. Proposition pour le prototype

### 4.1 Découpage

- Contrat `IDecompositionDevis` dans `Ventes/Contrats` : lecture, calcul, validation, légendes, modèles.
- Données `DecompositionDevisDb2` : partielle de la couche données, sur le modèle de ComplementsDevisDb2.
- Service `DecompositionDevisService` :
  - **calcul pur** (sans base), donc testable par tests unitaires : R20 à R23 et l'écart d'arrondi ;
  - contrôles et restriction représentant.
- Écrans :
  - `FenetreDecomposition.razor`, ouverte depuis le **détail** du devis (bouton aujourd'hui grisé) ;
  - `FenetreLegende.razor` ;
  - `FenetreModeleLegende.razor`.

### 4.2 Ergonomie

- **Grille arborescente** sur toute la largeur, avec des triangles ▸ / ▾ par nœud et une action « Tout déplier / replier ».
  - Les colonnes libellé, PR, PV et PV unitaire restent figées à gauche.
  - Les chapitres défilent horizontalement : seulement ceux de la légende, avec leur titre en en-tête.
  - L'infobulle d'un chapitre donne la liste de ses valeurs.
- **Saisie** :
  - la valeur se tape au clavier, ou se choisit dans une petite liste (F4, ⋯) qui montre les libellés de la légende ;
  - le % est à côté ;
  - une saisie sur un nœud se propage à ses feuilles ;
  - les lignes en erreur sont surlignées, avec le message du WinDev.
- **Recalcul** immédiat quand le PV remis ou l'unité change. Le bouton *Calculer* devient inutile.
- **Boutons** : Légende…, Imprimer, Valider, Annuler.
  - L'impression se fait dans le navigateur, comme pour le tableau des coefficients : pas de PDF serveur pour l'instant.
  - Après *Valider*, l'écran propose d'imprimer, comme le WinDev.
- Fenêtre déplaçable, comme les autres fenêtres internes.

### 4.3 Hors périmètre

Pas de recalcul du chiffrage (DDVALO, DEPRA…) : c'est le moteur DEV100. Le lot E ne modifie que le prix remis, l'unité et la ventilation analytique.

## 5. Questions à trancher

| # | Question | Ma proposition |
|---|---|---|
| Q1 | **Les fichiers NP11F sont-ils journalisés ?** C'est ce qui décide si une transaction est possible. Pour vérifier : `SELECT OBJNAME, JOURNALED FROM TABLE(QSYS2.OBJECT_STATISTICS('NP11F','*FILE')) WHERE OBJNAME IN ('D5000F','D50007','D60001','D60002','D50001','DEV025E','DEV025D')` | Transaction si tous le sont. Sinon : tout contrôler, écrire D5000F en dernier, et signaler précisément ce qui n'a pas pu être écrit. |
| Q2 | **Qui peut modifier ?** Le WinDev ne bloque rien : ni le statut du devis (accepté, en échec), ni le profil, ni le représentant. | Appliquer la restriction représentant déjà en place. Pour le reste, faire comme le WinDev, sauf si tu veux bloquer certains statuts (lesquels ?). |
| Q3 | Lignes fusionnées (A1) : mettre à jour **tous** les DDLIG du groupe ? | Oui. |
| Q4 | `DEPVRM` (« 1000 + » du PV remis) : le laisser tel quel comme le WinDev, ou le recalculer ? | Le laisser tel quel, comme le WinDev : le recalcul relève du moteur. |
| Q5 | Total des % supérieur à 100 (A4) : le refuser ? | Oui. |
| Q6 | Accès simultané : le WinDev et le prototype peuvent modifier le même devis en même temps. | Relire DEPVR et DEUNIT avant d'écrire. S'ils ont changé depuis l'ouverture, prévenir (« le devis a été modifié par ailleurs ») et ne rien écrire. |
| Q7 | Impression : celle du navigateur suffit-elle, ou faut-il un PDF dans la GED ? | Navigateur pour l'instant. |

## 6. Ordre de réalisation proposé

1. Lecture et affichage : arbre, légende, calcul. Aucune écriture.
2. Contrôles, puis validation : D5000F, D50007, D60001, D60002, D50001.
3. Légende et modèles : DEV025E/D.
4. Impression.

Les étapes 1 et 2 forment une livraison, les étapes 3 et 4 une seconde.

## 7. Décisions de Julien (30/10/2026)

- **Q1** : D5000F, D50007, D60001, D60002 et D50001 sont **journalisés**, mais DEV025E et DEV025D ne le sont pas. La validation de la décomposition se fait donc dans **une transaction**. Les modèles de légende seront écrits sans transaction.
- **Q2 à Q7** : toutes les propositions sont acceptées :
  - restriction représentant seule ;
  - tous les DDLIG des lignes fusionnées sont mis à jour ;
  - DEPVRM n'est pas modifié ;
  - un total des % différent de 100 est refusé ;
  - refus si le devis a été modifié par ailleurs ;
  - impression dans le navigateur.

## 8. Livraison 1 (30/10/2026, à compiler)

Contenu : arbre, calcul, contrôles, **légende** (édition) et validation.

L'édition de la légende a été avancée dans cette livraison : sans elle, on ne peut pas décomposer un devis qui n'a pas encore de légende. Restent pour la livraison 2 :

- les **modèles** de légende (DEV025E/D) ;
- l'**impression** de la décomposition.

Fichiers :

- `Contrats/DecompositionDevis.cs` : `IDecompositionDevis` (lecture, calcul pur, contrôle, validation).
- `Donnees/DecompositionDevisDb2.cs` : lecture, puis écriture en transaction.
- `Services/DecompositionDevisService.cs` : arbre, calcul R20 à R23, contrôle R18, validation R19.
- `Services/CalculBobine.cs` : coefficient bobine, commun avec le détail du devis.
- `Ecrans/Devis/FenetreDecomposition.razor` et `FenetreLegende.razor`, ouvertes par le bouton « ⚖ Décomposition » du détail.

Écarts avec le WinDev, en plus de A1 à A9 :

- **Concurrence** : le D5000F n'est mis à jour que si DEPVR, DEUNIT, DEPVM, DEPVMM, DEPVC et DEPVCM n'ont pas changé depuis la lecture. Sinon, tout est annulé et l'utilisateur est prévenu.
- **% obligatoire** : sur une ligne ventilée sur plusieurs chapitres, **chaque** chapitre doit avoir son %. Le WinDev ne contrôlait pas le premier, qui comptait alors pour 100 % à l'impression.
- **Légende** : quand un chapitre vide est supprimé, la ventilation des lignes **suit son chapitre**. Le WinDev la laissait sur l'ancienne lettre, ce qui l'affectait au mauvais chapitre.
- **Transport** : la valeur affichée est le port total de D50001. Le WinDev ne montrait que le premier groupe de lignes de même ventilation. Comme avant, la ventilation est écrite sur toutes les lignes.
- **Saisie sur une ligne de regroupement** : elle s'applique aussi quand on efface la valeur. Le WinDev ne recopiait que les valeurs non nulles.
- **Affichage** :
  - les nœuds affichent toujours leurs cumuls ;
  - un nœud montre la ventilation commune à ses lignes, ou « ≠ » quand elle diffère ;
  - le recalcul se fait à la frappe (plus de bouton Calculer) ;
  - la valeur se choisit dans la liste de la légende.
