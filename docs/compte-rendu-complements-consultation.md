# Consultation des devis — compléments : compte rendu d'analyse

_30/10/2026 — avant toute ligne de code_

## 1. Sources lues

- **ConsultationDevis, branche DLLNew** (`C:\Mes Projets.Branches\DLLNew\ConsultationDevis`). J'ai extrait le code de 20 fenêtres et états :
  - FEN_ConsultationDevis (menus) et FEN_DetailDevis (boutons) ;
  - FEN_SaisieEchec, FEN_Documents, FEN_RecapsousTotaux, FEN_CalculRecap + ETAT_CalculRecap, FEN_VisuTexteLibre, FEN_ValeursDefaut ;
  - FEN_TableauPrixSAV (ancienne copie, hors service) ;
  - FEN_Decomposition, FEN_Legendes, FEN_EnregLegende, FEN_Impression, ETAT_Decomposition (survol seulement : appelants et procédures).
- **Rétro-doc SaaS**, fiche `ConsultationDevis.md` (règles R8 à R26, tables, anomalies), écrite à partir de la branche `D:\Mes Projets`. C'est la seule source pour les **tags** (FEN_Tags), absents de DLLNew.
- **Non disponibles** :
  - `FEN_TableauPrix` : fenêtre du composant `DevisTableauPrix`, projet *ComposantsExternes*. Le `.wdk` est compilé, donc illisible. Seule l'ancienne copie `FEN_TableauPrixSAV` est lisible.
  - `FEN_Tags` (branche `D:\Mes Projets`) : ce dossier n'est pas accessible depuis cette session.

## 2. Inventaire des compléments

| # | Complément | Appelé depuis | Lecture / écriture | Tables | Taille |
|---|---|---|---|---|---|
| 1 | **Texte libre** du devis ou d'un élément | menu liste, détail, onglet élément | lecture | D50004, D60004 | petite |
| 2 | **Récapitulatif des sous-totaux** | menu liste, détail | lecture | D60002 + C800F / C000F / AZ00F + AA00081 `ST`, D60001 (support) | petite |
| 3 | **Tableau des coefficients** (+ impression) | menu liste | lecture | D5000F (toutes hypothèses) + D50009 + AA00083 `UV` | petite |
| 4 | **Tableau des prix** (toutes les quantités côte à côte) | menu liste, détail | lecture | D60002 par atelier, D60001, D50001, D5000F (DEPRA… DEPVR, TNUM) | moyenne, **source manquante** |
| 5 | **Valeurs par défaut** (filtre devis, type, présentation du détail) | menu Paramètres | préférence utilisateur | — (ex-`.ini` du poste) | petite |
| 6 | **Saisie de l'échec** | menu liste, détail | **écriture** | D50006 (insert / update / delete) ; AA00081 `ME` | petite |
| 7 | **Documents** (liste, ouverture, ajout, glisser-déposer, suppression) | menu liste, colonne Docs, détail | **écriture** | D50008 (BLOB), droits PRT000P1 `PCDOCUMENT` + AI00F.AGROUP | moyenne |
| 8 | **Tags** | menu liste | **écriture** | TAG_DEVIS (sans société), D50010 | petite, source = rétro-doc |
| 9 | **Récapitulatif PDF** | menu liste, détail | appel IBM i | `DEV100NAV` par QSYS2.QCMDEXC → PDF dans l'IFS `/home/pdf/temp` | petite si on garde DEV100NAV |
| 10 | **Recherches F4** (~20 listes de choix) | zones de critères | lecture | AA0003, AX00F, AA0009, AE0003, B000F, AI00F, normes, AB001, B7000F, D000F, C800F, C000F, AZ00F, AA00081 (SP, KL, ME)… | moyenne (nombreuses mais simples) |
| 11 | **Décomposition analytique + prix remis** (+ légendes, modèles, impression) | **détail** seulement | **écriture du chiffrage** | D5000F, D50007, D60001, D60002, D50001, DEV025E/D, AA000Z | **grosse** (1 600 lignes WinDev, R15 à R24) |
| — | Saisie / aperçu de l'offre | menu | lance `Offre.exe` | OFFRE | autre appli |
| — | Étude d'imposition, Historique du dossier | colonnes, menu | lancent `EtudeImposition.exe` et `Historiquedossiers.exe` | — | autres modules (Production) |
| — | Modification du devis | menu | `FEN_Modification` (DLL Devis2017) | — | étape Saisie, plus tard |

## 3. Règles à reprendre

Ces règles viennent du code, et je les ai recoupées avec la rétro-doc.

- **Échec** (R8) :
  - un seul échec par **n° de devis**, toutes versions confondues ;
  - motif (table `ME`), date de clôture et descriptif obligatoires, avec les mêmes messages que le WinDev ;
  - suppression après confirmation.
- **Documents** (R9, R10) :
  - rattachés au **n° de devis** ;
  - 7 097 152 octets au plus ;
  - nom nettoyé de `| : \ / * ? " < > '` et limité à 80 caractères ;
  - le droit de suppression se lit dans PRT000P1 `PCDOCUMENT` (AOPT01 = `**` interdit) ; le paramétrage de l'utilisateur prime sur celui de son groupe ; l'ajout n'est jamais restreint.
- **Tags** (R11) :
  - liste commune TAG_DEVIS (50 au plus, libellé unique, 20 caractères) ;
  - association par devis / version / hypothèse dans D50010.
- **Sous-totaux** (R25) :
  - regroupement par code `ST` : section C800F.RSTOT pour les opérations, C000F.GTOT pour les matières, AZ00F.HSTOT pour la sous-traitance ;
  - frais fixes = lignes `DDRSMP = 0`, frais variables = lignes `DDRSMP > 0` ;
  - les lignes sans code vont dans « DIVERS … » ;
  - ligne Support = valeur du papier (CONVUP) ;
  - total général.
- **Coefficients** (R26) :
  - une ligne par hypothèse, triée par quantité ;
  - base = PR total si `DPBASE = '1'`, sinon PRA, ramenée à l'unité de vente ;
  - prix = base × coefficient, **tronqué** à 2 décimales ;
  - la liste s'arrête au premier coefficient nul.
- **Texte libre** : lignes D50004 (devis) ou D60004 (élément) ; une ligne vide reste une ligne vide.
- **Récap PDF** (R12) : paramètre de 150 caractères = société(4) + devis(5) + version + hypothèse + type de coût + `000000`.

## 4. Points à trancher

| # | Question | Ma proposition |
|---|---|---|
| Q1 | **Écrire dans DB2 depuis le prototype ?** L'échec, les documents, les tags et la décomposition écrivent dans les tables de production. Ce serait la première écriture du prototype. | Oui, dans **les mêmes tables que le WinDev** (cohabitation : les deux applis voient les mêmes données). Toujours en requêtes paramétrées, et en **transaction** pour la décomposition. **Tests sur une bibliothèque de test**, pas sur NP11F. Laquelle ? |
| Q2 | **Documents** : garder les BLOB dans D50008, ou passer par un futur module Documents du noyau ? | **D50008** tant que le WinDev tourne. On ne télécharge un fichier qu'au clic, et non tous à l'ouverture comme le WinDev. L'ajout se fait par sélection ou glisser-déposer de fichiers. Les e-mails Outlook en `.msg` marchent s'ils sont d'abord enregistrés en fichier : le glisser direct depuis Outlook n'est pas possible dans un navigateur. |
| Q3 | **Tags** : le WinDev ne permet pas de retirer un tag, et chaque enregistrement crée des doublons. Doit-on pouvoir retirer un tag ? Les tags restent-ils communs à toutes les sociétés ? | Permettre de retirer un tag et éviter les doublons. Garder une liste commune, comme aujourd'hui. Il me faut aussi **FEN_Tags** (branche `D:\Mes Projets`) ou ton accord pour partir de la rétro-doc seule. |
| Q4 | **Tableau des prix** : le vrai `FEN_TableauPrix` est dans le projet *ComposantsExternes*. | Me donner accès à ce projet. Sinon, je pars de `FEN_TableauPrixSAV`, qui est peut-être en retard sur la version en service. |
| Q5 | **Récap PDF** : refaire l'édition côté serveur, ou garder DEV100NAV ? | **Garder DEV100NAV** pour l'instant : appel par QCMDEXC, puis lecture du PDF dans l'IFS par SQL (QSYS2.IFS_READ_BINARY) et affichage dans le navigateur. Il n'y a plus de partage réseau `\\host\home\pdf\temp`. On refera l'édition (QuestPDF) plus tard. |
| Q6 | **Anomalie du tableau des coefficients** : le WinDev teste `TNUM <> 99999999` (8 chiffres) au lieu de 999999999, donc le prix « Le tout » est calculé comme « au mille ». | Corriger, comme on l'a fait dans la liste (999999999 = « Le tout »). |
| Q7 | **Valeurs par défaut** | Préférences par utilisateur et par société côté serveur (même mécanisme que l'affichage des grilles), au lieu du `.ini` du poste. |
| Q8 | **Décomposition analytique** : c'est le seul écran qui modifie le chiffrage (prix remis, ventilation sur 15 chapitres, modèles de légende). | La faire **en dernier**, après une analyse détaillée de FEN_Decomposition et un compte rendu séparé. |
| Q9 | **Offre, Étude d'imposition, Historique du dossier** | Les laisser grisés : ce sont d'autres applis ou modules, à brancher quand ils existeront. |

## 5. Ordre proposé

1. **Lot A — lecture seule, aucun risque** : texte libre, récapitulatif des sous-totaux, tableau des coefficients (avec impression navigateur), valeurs par défaut.
2. **Lot B — écritures simples** (après réponse à Q1) : saisie de l'échec, documents, tags.
3. **Lot C** : tableau des prix (après Q4), récapitulatif PDF (DEV100NAV).
4. **Lot D** : recherches F4. Un composant commun de liste de choix, branché sur chaque zone : client, prospect, catégorie, sous-catégorie, fabricant, représentant, norme, OF, papier, sorte, sous-sorte, coloris, machine, section, opération, matière, fournisseur et opération de sous-traitance, motif d'échec.
5. **Lot E** : décomposition analytique et prix remis, avec son propre compte rendu (Q8).

Chaque lot : code → compilation chez toi → essai → lot suivant.

## 6. Réponses de Julien (30/10/2026)

1. Écrire dans les mêmes tables que le WinDev, bibliothèque **NP11F** : l'IP configurée est celle du **serveur de test**.
2. FEN_TableauPrix : `C:\Mes Projets\ComposantsExternes` (mode texte).
3. FEN_Tags : `C:\Mes Projets\ConsultationDevis`.
4. Tags : oui, on peut retirer un tag (et plus de doublons).
5. Récap PDF : oui, garder DEV100NAV et lire le PDF par SQL.
6. Coefficients : oui, corriger le test de « Le tout ».

Lot A livré le 30/10/2026 (à compiler).
Lot B livré le 30/10/2026 (à compiler). Écarts avec le WinDev, tous dans le sens d'une correction :
- documents : seul le nom et la date sont lus à l'ouverture de la fenêtre (le WinDev extrayait tous les fichiers) ; identifiant relu par `FINAL TABLE` au lieu de `max(id)` ; e-mail Outlook à enregistrer d'abord en .msg ;
- tags : retrait possible, plus de doublons (suppression puis réinsertion des tags du devis), 20 caractères acceptés, doublon de libellé contrôlé sans tenir compte des majuscules, type de coût réel enregistré (le WinDev forçait « C ») ;
- échec : motif choisi dans une liste (table ME) ; descriptif limité à 800 caractères (taille de F6TXT).

Lot C livré le 30/10/2026 (à compiler). Écarts avec le WinDev :
- tableau des prix : toutes les lectures filtrent sur le type de coût du devis (le WinDev mélangeait « rcout = 'C' » en dur et le type demandé : tableau vide pour un devis type, montants doublés quand le devis existe en C et en T) ; « 1000 + » du PV conseillé « pour … » divisé comme les autres lignes (méthode globale) ; « 1000 + » du PV remis « pour … » affiché aussi en méthode A ;
- récapitulatif PDF : même commande DEV100NAV, mais le PDF est relu par SQL dans l'IFS (plus de partage `\\host\home\pdf\temp`) et affiché dans le navigateur ; la clé du devis est contrôlée avant d'entrer dans la commande.
- Libellés des zones confirmés par le catalogue DB2 : DEPRA = PRA, DEMFG = frais généraux, DEMCO = commission, DEMES = escompte, DEMRI = ristourne, DEMCR = majoration conditions de règlement, DEMFF = frais financiers, DEMFS = frais de stockage, DEMFA = frais administratifs, DEMFMA = frais de manutention, DEPR = prix de revient, DEPVM / DEPVC / DEPVR = PV mini / conseillé / remis, DEOBSP = observation prix.

Lot D livré le 30/10/2026 (à compiler). Recherche par mots comme les FEN_Rec* (mot terminé par « * » = commence par) ; grandes listes (clients, prospects, dossiers, papiers, matières, fournisseurs) sur demande, petites listes affichées en entier ; 200 lignes au plus. Simplifications : les fenêtres papier, matière et dossier du WinDev ont de nombreux filtres (sorte, grammage, famille, type de commande…) ; ici une seule zone de recherche par mots (et le n° en clair). Les clients fermés sont affichés en rouge au lieu d'être masqués.

