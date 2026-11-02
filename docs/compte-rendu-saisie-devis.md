# Compte rendu — Saisie d'un devis : analyse et plan

*Analyse du 02/11/2026, sans code. À valider avant développement.*

## 1. Décisions de départ (Julien, 02/11/2026)

| Sujet | Choix |
|---|---|
| Moteur de calcul | **Port en .NET 10 du C# Devis2017**, avec les corrections trouvées pendant le port WinDev 2026, puis confrontation à DEV100 au centime. **DEV100 fait foi.** |
| Premier parcours | **Modifier un devis existant**. Copier et Nouvelle version réutilisent ensuite le même écran. |
| Écran | **Onglets + bandeau de prix fixe** : PR, PV mini, PV conseillé et PV remis, toujours visibles et recalculés. |

## 2. Sources analysées

| Source | Contenu utile |
|---|---|
| **Devis2017** (`C:\SAAS\Outils\Devis2017`, C# .NET 4.8) | 93 fichiers, ~21 000 lignes, dont : **Model** ~3 500 lignes (Devis, ElementDevis, LigneFabrication, Base, ParametrageDevis, Support…) ; **BLL** ~3 000 (DevisBLL 1 300 : TraiteElement, valorisation A/B ; Precalculs, RechercheTableau, ElementCalculBLL, ValidationElement, RecapPresenter, LivraisonPresenter) ; **DAL** ~4 500 (DevisDB 1 320, ParametrageDevisRepository 1 050 et 12 autres) ; **14 fichiers de tests** (valorisation, précalculs, tableaux, réponses, meilleur papier…) ; UI WinForms/DevExpress ~7 000 lignes, **non reprise**. |
| **Specs spec-kit 001 à 017** | Le « quoi » formalisé : saisie et calcul, onglets, contrôles de l'en-tête, livraison, étapes, entête d'élément, hypothèses, récap et prix remis, texte libre, actions de la liste, validation des éléments, filtre papier. |
| **Port WinDev 2026** (`C:\SAAS\Outils\ConsultationDevis\docs\changelog-decisions.md`, 3 400 lignes) | Bilans des sprints 5 à 9 et **corrections métier** découvertes en confrontant le C# au RPG (§ 4). |
| **Rétro-doc DEV100** (`Devis2017\doc\architecture_DEV100`) | Glossaire, 22 fiches d'écran, 3 fiches de calcul, architecture des classes, verrouillage PRTLCK. |
| **DEV100.rpgle** (`SchemaBase\sources AS400\NP11`) | Référence pour les zones absentes du C# (§ 5). |

## 3. Ce que fait le moteur Devis2017 (à porter)

1. **Chargement du paramétrage** (`AppService.LoadContext`) : 14 caches par société (paramètres généraux, types de produit, machines, matières, rubriques et liens machine, modèles de lignes, tableaux de calcul, normes…). Dans le prototype, ce sera un **cache serveur par société** (`IMemoryCache`), chargé à la première ouverture d'un devis.
2. **Chargement d'un devis** : D5000F (en-tête), D60001 (éléments, 138 colonnes), D60001P/R (rubriques), D60002 (lignes de fabrication, 75 colonnes), D6001C (coloris), D60003, D50001 (transport), D50002 (texte technique), D50009 (coefficients).
3. **TraiteElement**, le hub du calcul par élément, en 6 blocs :
   - constitution des lignes d'impression ;
   - poses et format de tirage ;
   - recalcul des cadences (bloc 3) ;
   - gâche automatique (bloc 4) ;
   - valorisation de l'impression ;
   - constitution du détail à partir des liens rubrique / machine.

   Il s'appuie sur **25 précalculs** (NPQ plaques, NLA lavages, NPO poses, CAR, SU1/SU2 surfaces…), sur les **tableaux de calcul** H000F (abaques) et sur la **substitution des réponses** aux questions.
4. **Valorisation** : base 100, puis méthode **A** (frais généraux par poste) ou **B** (coefficient global + commission par paliers), puis PV mini / conseillé, puis récap.
5. **Hypothèses de quantité** : clonage et revalorisation.
6. **Sauvegarde** : D5000F en INSERT ou UPDATE ; les sous-tables (D60001, P/R, D60002…) sont supprimées puis réinsérées, dans une transaction.

## 4. Corrections du port WinDev à intégrer dès le départ

| # | Correction | Impact |
|---|---|---|
| W1 | `DDPRMA` / `DDPRMO` sont des **taux horaires** (machine / main-d'œuvre), pas des valeurs | valorisation des opérations |
| W2 | Précalculs **NPQ / NLA** : le nombre de plaques ou de lavages saisi (DDPQDF / DDCLDF) **remplace** le calcul, il ne s'y ajoute pas | nombre de plaques et de lavages |
| W3 | **CAR** = hauteur **+ 6** (le C# faisait − 6) | cartonnage |
| W4 | `DDSENS` = **type d'élément** métier, pas le sens de fibre | mapping D60001 |
| W5 | `*IN13` = groupe de type de produit (plat avec pli / continu), pas un mode | règles d'affichage et de calcul |
| W6 | **Colonnes écrites mais pas relues** : par exemple `DDATEL` (atelier) perdu à la resauvegarde. Toute donnée calculée doit aussi être relue au chargement. | perte silencieuse de données |
| W7 | Cycle de vie complet des **pseudo-éléments 31 / 32** (Prépresse / Façonnage) | lignes hors élément |
| W8 | **11 précalculs** absents du C# portés depuis DEV100 (ETU, FOU, POC, FAU, BCO, TR, ML…) | lignes à zéro sinon |
| W9 | `TraitementPrecalculs` doit accepter les lignes d'impression sans rubrique parente | résultats à zéro sinon |
| W10 | Conversions d'épaisseur EP1 / EP2 / EP3 exactes, unité d'épaisseur sur l'élément | poids, cartonnage |

## 5. Ce que le C# ne fait pas (DEV100 fait foi)

| # | Zone | Pour « Modifier » | Pour « Créer » |
|---|---|---|---|
| R1 | Recalcul des cadences (bloc 3) | nécessaire si l'on change quantité ou machine | nécessaire |
| R2 | **Gâche automatique par étape** (`SPGAcetape`, option TPGACA du type de produit) | idem | nécessaire |
| R3 | **Déduction papier ↔ machine ↔ poses** (papier non codé 999999) | seulement si l'on change le papier | **cœur de la création** |
| R4 | Récap papier (écran D4) : prix des papiers non codés, conditionnement | utile | nécessaire |
| R5 | Saisie en pouces et 1/6 de pouce (bobine) | si clients concernés | idem |
| R6 | Coefficients D50009 pré-remplis depuis DEV042 | lecture déjà faite (lot A) | à la création |
| R7 | **Verrou PRTLCK** : DEV100 pose un verrou à l'ouverture d'un devis en modification | **indispensable** pour cohabiter avec DEV100 | idem |
| R8 | Numérotation AA00082 non atomique | — | à sécuriser |

## 6. Le point clé : un contrôle « à blanc » avant toute écriture

Les devis modifiés seront relus par DEV100 et par la consultation. Le moteur doit donc donner **les mêmes chiffres que DEV100** avant d'écrire quoi que ce soit.

Je propose un premier livrable sans écriture :

> **Recalcul à blanc** : on charge un devis réel, on le recalcule avec le moteur porté sans rien changer, puis on compare ligne par ligne et colonne par colonne avec ce que DEV100 a enregistré (DDVALO, DDRSQT, DEPRA, DEPR, DEPVM, DEPVC…). L'écart doit être de 0,00 €.

Ce contrôle servira de :

- **golden master** : à rejouer automatiquement sur 10 à 20 devis de référence à chaque évolution du moteur ;
- **filet de sécurité** : la modification ne sera ouverte en écriture que sur les types de devis dont le recalcul à blanc est exact.

## 7. Plan proposé, par lots

| Lot | Contenu | Écriture DB2 |
|---|---|---|
| **S1 — Modèle et chargement** | Modèle C# (.NET 10, sans DevExpress) ; chargement complet d'un devis et du paramétrage (cache par société) ; projet de tests `NetPrint.Modules.Ventes.Tests` avec les 14 fichiers de tests Devis2017 portés en xUnit | non |
| **S2 — Moteur + recalcul à blanc** | TraiteElement, précalculs, tableaux, réponses, valorisation A/B, récap ; corrections W1 à W10 ; écran **« Contrôle du calcul »** (devis → écarts moteur / DEV100) ; golden master sur les devis de référence | non |
| **S3 — Écran de saisie (simulation)** | Onglets **En-tête** (Général, Livraison), **Éléments** (entête, papier, rubriques et étapes), **Fabrication** (lignes O/M/S), **Hypothèses**, **Récap** ; bandeau de prix fixe ; recalcul à chaque modification ; explications ⓘ ; pas d'enregistrement | non |
| **S4 — Enregistrement** | Verrou PRTLCK à l'ouverture ; transaction (si les tables sont journalisées) ; suppression et réinsertion des sous-tables comme le C# ; contrôle d'accès simultané ; puis **Copier** et **Nouvelle version** (numérotation AA00082 sécurisée) | **oui** |
| **S5 — Créer et zones DEV100** | Recalcul des cadences, gâche automatique, déduction papier / poses, récap papier, pouces 1/6 ; carte **Créer** ; **Renouveler** et **Réactualiser** | oui |

Chaque lot suit le même cycle : compte rendu → validation → code → compilation chez toi → essais.

Architecture dans le prototype :

- **moteur** dans `Ventes/Moteur/`, C# pur, sans accès à la base, donc testable ;
- **chargement et écriture** dans `Ventes/Donnees/` ;
- **écrans** dans `Ventes/Ecrans/Devis/Saisie/`.

Le moteur ne dépend que du modèle. C'est la même règle d'architecture que pour le reste du prototype.

## 8. Questions avant le lot S1

1. **Devis de référence** : peux-tu me donner 10 à 20 devis réels variés, avec leurs n°/version/hypothèse ? Il faudrait couvrir :
   - les méthodes A et B ;
   - des devis en feuille et en bobine ;
   - des devis à 1 élément et à plusieurs ;
   - de la sous-traitance, des matières, des pseudo-éléments 31 / 32 ;
   - plusieurs hypothèses ;
   - un devis avec papier non codé.

   Sinon, je les choisis automatiquement dans la base de test selon ces critères.
2. **Journalisation** des autres tables écrites (D60001P, D60001R, D50002, D6001C, D60003, AA00082) : peux-tu lancer la même requête `QSYS2.OBJECT_STATISTICS` que pour la décomposition ? Cela décidera de la transaction du lot S4.
3. **Verrou PRTLCK** : confirmes-tu que DEV100 le pose bien sur `D5000F` avec la clé `SSSSCDDDDDVH`, et que la consultation WinDev le respecte ?
4. **Pouces 1/6** (R5) : des clients actuels font-ils des devis en pouces ? Sinon, je reporte.
5. **Corrections WinDev** : le journal s'arrête au 23/06/2026. Des corrections ont-elles été faites ensuite, ailleurs que dans ce journal ?

## 9. Réponses de Julien (02/11/2026)

1. **Devis de référence** : je les choisis moi-même dans la base de test, à l'aide de critères écrits en SQL. Ce sera au lot S2, avec l'écran « Contrôle du calcul ».
2. **Journalisation** des autres tables : inconnue. Au lot S4, le service la vérifiera au moment d'enregistrer (requête `QSYS2.OBJECT_STATISTICS`) : transaction si toutes les tables sont journalisées, sinon écriture contrôlée.
3. **PRTLCK** confirmé :
   - au clic sur « Saisie », on crée une entrée avec le profil et le fichier (la référence du devis) ;
   - si un autre utilisateur veut modifier le même devis, la présence de cette entrée lui refuse l'accès ;
   - à la validation ou à l'abandon, on supprime l'entrée.

   Le format exact de la clé (`PLCLE`) sera vérifié au lot S4, en lisant PRTLCK pendant qu'un devis est ouvert dans DEV100.
4. **Pas de devis en pouces** : la zone R5 est abandonnée.
5. **Aucune correction du moteur** en dehors du journal WinDev.

## 10. Lot S1 : découpage et choix techniques

Le lot S1 est livré en trois parties, compilées l'une après l'autre :

| Partie | Contenu |
|---|---|
| **S1a** | **Modèle** : les 17 classes de `Net.Axilog.Model`, reprises telles quelles dans `Ventes/Moteur/Modele` (espace de noms `NetPrint.Modules.Ventes.Moteur.Modele`). `ThreadedBindingList` n'est pas reprise (inutilisée). Les **5 fichiers de tests du modèle** sont portés de NUnit vers xUnit dans `tests/NetPrint.Modules.Ventes.Tests/MoteurDevis`. |
| S1b | **Référentiel de calcul** (les 14 caches de `AppService.LoadContext`) : une classe d'instance par société au lieu des classes statiques de Devis2017, gardée en cache serveur, et chargée par `Donnees/ReferentielDevisDb2` en SQL paramétré. |
| S1c | **Chargement d'un devis** (port de `DevisDB.GetDevisById`) avec les colonnes « écrites mais non relues » corrigées : DDNTRG, DETXGA, DEFRAC / DEMOIS / DEFATL ≠ DEFAC, DDBCK, DDFRN, DDPGAV, DDNSEQ, DDPERF, DDPERV, DDSENS (type d'élément), DDUNEPAI, DDATEL. Il est accompagné d'un **test d'intégration** `[FactDb2]` sur un devis de la société 8. |

Choix techniques :

- **Le modèle est repris sans réécriture**, à l'espace de noms près. Cela limite le risque d'écart avec Devis2017, qui sert d'oracle. Les corrections métier seront marquées « CORRECTION » dans le code.
- **Plus de données statiques** : Devis2017 gardait le paramétrage dans des classes statiques, ce qui est impossible dans une application web à plusieurs utilisateurs et plusieurs sociétés. Le référentiel devient un objet par société, transmis au moteur.
- **Accès DB2** : ODBC et SQL paramétré, comme le reste du prototype, à la place du pilote `IBM.Data.DB2.iSeries`. Les noms de tables sont préfixés par la bibliothèque `Db2:Bibliotheque`.
- **Le moteur reste dans l'assemblage Ventes**, dans l'espace de noms `Moteur` et non dans un projet à part. Un projet `NetPrint.Modules.Ventes.Moteur` serait refusé par la règle d'architecture « un module ne référence que les Contrats des autres ».

**S1a livré le 02/11/2026 (à compiler).**
