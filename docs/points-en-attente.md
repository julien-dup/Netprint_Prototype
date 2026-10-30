# Points en attente — reprise de la saisie de commande

_Mise à jour : 30/10/2026 — branche `commandes/liste-dossiers`_

Ce document liste, par module, ce qui reste à décider, à développer ou à vérifier.
Il évite de perdre un point au fil des échanges : on y ajoute chaque nouveau point, on raye ce qui est fait.
Légende : 🔴 bloquant ou prioritaire · 🟠 important · ⚪ plus tard.

---

## 1. Ventes — Commandes (module de David)

### 1.1 Décisions à valider par David

Écarts volontaires avec le RPG, déjà codés (signalés « ÉCART » dans le code) :

| # | Sujet | Choix actuel | RPG |
|---|---|---|---|
| D1 | Prix saisi sur une ligne | Jamais remplacé par le tarif | Écrasé par le tarif si le prix de cession est vide (CTL520) |
| D2 | Réactivation d'une ligne soldée | Réactive aussi la commande soldée « D » | Le solde automatique n'est jamais annulé |
| D3 | Solde de la commande | Événements du dossier (EVE100 / EVE105) conservés | Supprimés (DOS500S R8) |
| D4 | Suppression de la commande | Textes de lignes, AB001FA, AB0016 supprimés aussi | Laissés orphelins |
| D5 | Suppression de la dernière ligne | La commande n'est pas soldée automatiquement | Soldée (elle devenait inmodifiable) |
| D6 | Avertissements de ligne (stock, date…) | Acceptés un par un, explicitement | 2e Entrée ; masquait ensuite les contrôles de date |
| D7 | N° de dossier | Réservé à l'enregistrement seulement | N° provisoire affiché puis remplacé |
| D8 | Clé de commande quand DOS005 POPT02 = 'O' et POPT05 ≠ 'O' | « 9 » (CDECLEF est un programme spécifique NP11PRI, sans source) | Appel de CDECLEF |
| D9 | AB0016 (marge) | Lu et écrit par sa clé complète (n° + clé) | Lu par n° seulement, clé réécrite |

Questions ouvertes :

| # | Question |
|---|---|
| Q1 | Date des tarifs : date du jour (comme le RPG, choix actuel) ou date de la commande ? |
| Q2 | Encours client (DOS901) : bloquant sans forçage, comme le RPG ; faut-il une dérogation tracée ? |
| Q3 | Qualité client 5 (« bloqué en livraison ») : la commande reste possible après confirmation (F19) ; faut-il la bloquer ? |

### 1.2 À développer

| Prio | Sujet | Source | Notes |
|---|---|---|---|
| 🔴 | **Appels de livraison** | NEG520 (option DE, SpOrigin, NEG520B), NEG300NAV, NavigateurLogistique, TableauBordEDI | Créés à parts égales depuis NEG520, le navigateur et l'EDI : **un seul moteur** (NEG300NAV, actions C/V/S/A) pour les trois. Un appel reprend le **prix de sa ligne à la demande** ; modifier ce prix doit pouvoir être **reporté sur tous les appels dépendants** (NEG520B). Créer / modifier / supprimer un appel reporte l'écart sur la ligne d'origine (DNQTEA, DNQTEC, solde). Maxi livrable restant (F23). En attendant : modification, solde et suppression d'un appel **bloqués** dans le nouvel écran |
| 🟠 | Encours client à l'ajout d'une ligne | NEG520 F9 → DOS901 'C' | Contrôlé à la création de l'en-tête seulement pour l'instant |
| 🟠 | Copie d'une commande | DOS500S option 3, DOS505 COPT 03 | |
| 🟠 | Copie / transfert de reliquat d'une ligne vers une autre commande | NEG510 F14 | Déplace quantité et réservation : opération transactionnelle |
| 🟠 | ARC (édition, envoi, visualisation) | ARC105P, ARC525, ARC100 (type 1), NEG510 F18 | Service documents commun (cadrage) |
| 🟠 | Lancement en fabrication / affectation de stock | NEG515, NEG510 F10 (création auto des OF) | Frontière avec le module Production |
| 🟠 | Reprise des lignes d'un devis | NEG520 F10 (NEG52009) | Les prix de beaucoup de commandes réelles en viennent (DNDEV) |
| 🟠 | Articles avec nomenclature | NEG520 (lignes composants, DNLCOM) | Refusés aujourd'hui |
| ⚪ | Articles libres « 999… » | CTL520 l. 804-845 | Refusés aujourd'hui |
| ⚪ | Lignes de frais E / P, remises R | CTL520 l. 1643-1707, NEG520 SPCAVT | Refusées aujourd'hui |
| ⚪ | Changement de date de livraison en masse | NEG520 F20 (NEG520CW) | |
| ⚪ | Import Excel (NEGIMP / NEGIMPS) | NEG520 R3, ImportCommande | |
| ⚪ | Contrôle de la norme | NOR010 (colonnes inconnues dans la cartographie) | Non contrôlée aujourd'hui |
| ⚪ | Paramètres de facturation automatique (F13) | DOS505 DOS50505, AB001FA | Valeurs par défaut écrites, pas de saisie |
| ⚪ | Changement de client d'une commande (F17) | DOS505 R15 | Client figé en modification |
| ⚪ | Texte complémentaire ARC, événements | DOS560, EVE300 | |
| ⚪ | Transformation d'un dossier de fabrication en commande | DOS500S option TR | RAUCLEF absent des sources |
| ⚪ | Création depuis une proforma | DOS505 R20 | Doit passer par le même service que la saisie |
| ⚪ | Recherche d'article (F4) dans la saisie de ligne | — | Pas de liste « Article » dans le Noyau (`IListesDeChoix`) |

### 1.3 Techniques

| Prio | Sujet | Notes |
|---|---|---|
| 🔴 | Journalisation des tables restantes (David, prévu demain) | ABREPE, ABREPD, ABCOUTE, ABCOUTD, ABCOUTB, AFC020, AB0020, AB001T, ABDIRL, ABDIRP, DIR100, EVE200. Ensuite : les faire entrer dans la transaction de suppression, et l'audit EVE200 dans les transactions |
| 🟠 | Moteur de prix non validé sur de vrais tarifs | La base de test n'a presque pas de tarifs (SPF011 : 2 lignes) ; comparaison à refaire sur une copie de production |
| 🟠 | STOCK03 appelé par QCMDEXC | Dépend de la liste de bibliothèques du travail ODBC (NP11F, NP11P) ; à recoder en C# ou à exposer proprement |
| ⚪ | Nombre de connexions par action | Paramètres société relus à chaque appel : cache de session à prévoir |
| ⚪ | Verrou applicatif PRTLCK | Remplacé par un contrôle d'empreinte (verrou optimiste) ; à confirmer pour la cohabitation avec le 5250 |

---

## 2. Socle et Noyau (communs, à voir avec le collègue)

| Prio | Sujet | Notes |
|---|---|---|
| 🔴 | Connexion IBM i impossible sur le poste de David | `BaseDb2.VerifierProfilAsync` reconstruit la chaîne ODBC : « Missing system name ». Contourné en mode démo |
| 🟠 | Fonctions ODBC (`Commande`, `Lire`, `Executer`) recopiées dans chaque classe `Donnees` | À regrouper dans `BaseDb2` |
| 🟠 | Liste de choix « Article » | Manque dans `IListesDeChoix` |
| 🟠 | Service Compteurs (n° de dossier, GETDOSSIER) | Cible du cadrage : attribution atomique |
| 🟠 | Qualité client (CQUACL) | Règle transverse devis / commande / BL : à porter dans le Noyau |
| ⚪ | Droit du profil sur la société à la connexion (PRT000P1) | Déjà noté dans decisions.md |

## 3. Production

| Prio | Sujet | Notes |
|---|---|---|
| 🟠 | OF (type 2) et commandes simples (type 1) | Listés par HistoriqueDossiers, non repris (hors module Ventes) |
| 🟠 | Lancement en fabrication, création auto des OF | Voir 1.2 |

## 4. Logistique et EDI

| Prio | Sujet | Notes |
|---|---|---|
| 🔴 | Appels de livraison depuis le navigateur logistique et le tableau de bord EDI | Même moteur que la saisie de commande (voir 1.2) |
| ⚪ | BL, ordres de préparation (OPR) | Lus dans la fiche commande ; une ligne d'un OPR non livré n'est plus modifiable |

## 5. Organisation

| Prio | Sujet | Notes |
|---|---|---|
| 🟠 | Demande de fusion de `commandes/liste-dossiers` | La branche contient aussi `socle/outillage-equipe` (non fusionnée dans `main`) |
| 🟠 | `docs/donnees-de-test.md` | Société 8, client 411100 376 (Clarins) ; fabricant « LE » inexistant (« LEN » existe) ; compteurs de dossiers en retard sur les données |
| ⚪ | `docs/decisions.md` | Y reporter les décisions D1 à D9 une fois validées |
| ⚪ | Tests d'écriture | Lancés seulement avec `NETPRINT_TESTS_ECRITURE=1` ; ils nettoient ce qu'ils créent |
