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
| D10 | Appel au-delà du reste à appeler de la ligne d'origine | Avertissement à accepter, pour tous les canaux (EDI compris) | NEG520 : avertissement NEG0029 ; navigateur et EDI : aucun contrôle |
| D11 | Report du prix de l'origine sur ses appels (NEG520B) | Proposé après un changement de prix ; appels **ni soldés ni facturés** seulement ; la remise de l'appel est réécrite aussi | Tous les appels, soldés et facturés compris ; remise de l'appel laissée |
| D12 | Prix d'un appel | Toujours celui de l'origine à la création (l'appelant ne transmet pas de prix) | Navigateur et EDI transmettaient le prix ; NEG300NAV héritait remise et prix théorique de la **dernière ligne** du dossier |
| D13 | Reste de l'origine | Recalculé depuis la quantité du contrat et la somme des appels relue en base ; origine soldée quand plus rien à appeler, **remise en cours** si un reste réapparaît ; un solde fait à la main (option SO) alors qu'il restait à appeler est conservé ; l'article d'une ligne à la demande qui a des appels est figé | Trois calculs différents (NEG520 DE ne « désolde » jamais) |
| D14 | Solde d'un appel | Son reste non livré **n'est pas** rendu à l'origine (comme le RPG) | Idem |
| D15 | Défauts d'un nouvel appel | N° et date d'appel, adresse, commentaires, textes repris de l'origine ; date de livraison à saisir | NEG520 : idem ; EDI : date d'appel = date de livraison |
| D16 | Appel créé par l'EDI (EDI105) | Ne se modifie et ne se supprime que par l'EDI | Rien ne l'empêchait |
| D17 | ARC : canevas absent dans la langue du client | Canevas français | ARC sans en-tête ni pied |
| D18 | ARC : variables `$CRSF…` sans client facturé | Adresse du client | Vides (condition inversée) |
| D19 | ARC : ordre des lignes | N° de ligne, regroupées par livraison (adresse, date, à la demande) | Clé adresse / date / statut / article |
| D20 | ARC : canevas qui place l'adresse du client (`$CRS`) | Il remplace les blocs « références » et « destinataire » du nouvel en-tête | L'overlay et le canevas faisaient tout |
| D21 | ARC : variables du canevas | Valeur sans blancs de fin (le `§` réaligne) | Longueur fixe, blancs compris (probable) |

Questions ouvertes :

| # | Question |
|---|---|
| Q1 | Date des tarifs : date du jour (comme le RPG, choix actuel) ou date de la commande ? |
| Q2 | Encours client (DOS901) : bloquant sans forçage, comme le RPG ; faut-il une dérogation tracée ? |
| Q3 | Qualité client 5 (« bloqué en livraison ») : la commande reste possible après confirmation (F19) ; faut-il la bloquer ? |

### 1.2 À développer

| Prio | Sujet | Source | Notes |
|---|---|---|---|
| ✅ | ~~Appels de livraison~~ | NEG520 (option DE, SpOrigin, NEG520B), NEG300NAV | Fait (30/10) : moteur `IAppelsLivraison` (création, modification, suppression, report de prix, solde du maxi) partagé avec la saisie des lignes ; écran : « Nouvel appel » sur une ligne à la demande. Décisions D10 à D16 ci-dessus |
| 🟠 | Appels : contrôle du pré-BL non basculé (EXP510 / EXP509.HENBL) | NEG300NAV contrôle 21, NEG520 option S | Seul l'OPR non livré est contrôlé aujourd'hui |
| ⚪ | Appels : import Excel en mode appels | NEG520 SpExcel | 4e point d'entrée, à brancher sur `IAppelsLivraison` (canal `ImportExcel`) |
| 🟠 | Encours client à l'ajout d'une ligne | NEG520 F9 → DOS901 'C' | Contrôlé à la création de l'en-tête seulement pour l'instant |
| 🟠 | Copie d'une commande | DOS500S option 3, DOS505 COPT 03 | |

| ✅ | ~~ARC : édition et aperçu~~ | ARC105P, NEG510 F18 | Fait (30/10) : PDF produit par l'application (QuestPDF), canevas ARC010F / ARC015F décodés, traductions PRT008, aperçu à côté de la fiche mis à jour à chaque modification. Guide : `docs/editions-pdf.md` |
| ✅ | ~~ARC : envoi par e-mail~~ | NEG510 F18, ARC105CLP / ARC106, EnvoiARC, VisuArc | Fait (30/10) : brouillon Outlook (.eml) avec les ARC joints, sans SMTP ; contacts ARC PRT051F (1er coché = destinataire, autres en copie) ; objet d'ARC105CLP / EnvoiARC, texte = chapitre A du canevas. Depuis la fiche (une commande) et la liste (commandes affichées d'un client, 50 au plus) |
| ✅ | ~~ARC : envoi groupé direct~~ | EnvoideFactures (SMTP FAC010) | Fait (30/10) : depuis la liste, un message par client à ses contacts ARC, résultat client par client ; depuis la fenêtre ARC, « Envoyer » à côté de « Préparer (Outlook) ». Garde-fou `Messagerie:Mode` (désactivé par défaut, redirection de test) |
| ✅ | ~~Expéditeur des ARC~~ | AI00F.ACMELE | Décidé par David (30/10) : l'adresse de l'utilisateur (e-mail externe de son profil) si elle est du domaine de la société, sinon FAC010.PEXPEN avec l'utilisateur en « Répondre à ». Avec la connexion Entra ID, l'adresse viendra du compte |
| 🟠 | Envoi au nom de l'utilisateur par le serveur de la société | FAC010 | Le serveur SMTP doit accepter d'envoyer au nom des adresses des utilisateurs (même domaine, SPF). Avec Entra ID : envoi par Microsoft Graph au nom de l'utilisateur (message dans ses « Éléments envoyés », plus de mot de passe SMTP dans FAC010) |
| ✅ | ~~Historique des envois d'ARC~~ | — | Fait (30/10) : journal ENV100 (qui, quand, canal, à qui, objet, échec, PDF envoyé), onglet « Envois ARC » de la fiche. Commun à tous les documents (offres, BL, factures) |
| ✅ | ~~Créer la table ENV100~~ | `db/db2/20261030-2230_ENV100_journal_envois.sql` | Appliqué sur NP11F de test le 30/10 (table, index, journalisation NP11JRN/JRN). À appliquer sur les autres bibliothèques au déploiement |
| ⚪ | Journal ENV100 : lignes d'un même message dans une transaction | Une fois ENV100 journalisée ; aujourd'hui une ligne par document, un échec en cours de message laisse une trace partielle (signalée dans les journaux techniques) |
| ⚪ | Aides communes « table absente » (SQLSTATE 42704) et « tronquer une valeur » | Recopiées dans plusieurs classes `Donnees` : à regrouper dans `BaseDb2` (avec les fonctions ODBC, voir 2.) |
| ⚪ | Envoi groupé : une seule connexion SMTP | — | Aujourd'hui une connexion (et une lecture de FAC010) par message ; port 465 (TLS implicite) non pris en charge par le client SMTP de .NET |
| 🟠 | Brouillon .eml dans le nouvel Outlook / Outlook web | — | Outlook classique ouvre le brouillon ; le nouvel Outlook et Outlook web gèrent mal `X-Unsent`. Pour le SaaS : brouillon créé par Microsoft Graph dans la boîte de l'utilisateur |
| 🟠 | ARC : logos des sociétés | Fichiers BMP / JPEG des éditions WinDev (répertoire NetPrint des postes) | Les copier dans `src/NetPrint.Web/Logos` (sur le serveur : dossier `Logos` de la publication), sans les renommer ; mode d'emploi `Logos/LISEZMOI.md` |
| ⚪ | ARC : historique des envois, ARC sans prix, ARC modificatif | — | N'existent pas dans le RPG : à décider |
| ⚪ | ARC des dossiers de fabrication (type 1) | ARC100 / ARC105F | Hors commandes de négoce et produit fini |
| ⚪ | Maintenance des canevas d'ARC | ARC010S / ARC010 (5250 seulement) | Écran à reprendre si les canevas restent la source des textes |
| 🔴 | **Lancement en fabrication / affectation de stock** (en cours) | NEG515, NEG510 F10 | Initialise la production à partir de la commande. Décisions de David (30/10) : module Production minimal (contrat `IInitialisationProduction`) ; ouverture de l'OF laissée au 5250 (DOS500S) pendant le pilote ; F10 repris tel quel ; BAG affiché en lecture dans l'écran de lancement (la validation des BAG passe par CloudFlow d'Hybrid Software ou un concurrent, statuts reçus par JDF201 dans AB009 : intégration à faire avec le module Production) ; option AL limitée aux OF à ouvrir ; provisoire / définitif (CCOPT2 'P') abandonné (jamais utilisé). Étape A faite : demande d'OF (AB001T + AB0020) en transaction. Étape B faite : écran de lancement en lecture (calculs NEG515, BAG affiché). Étape C faite (30/10) : actions de l'écran — lancer un OF pour un article ou des articles cochés (valeurs proposées : reste à affecter, modèle de l'article et dernier dossier du modèle (SOP22), devis, catégorie), modifier / supprimer un OF à ouvrir, rattacher le reste à affecter à un OF à ouvrir (AL), affecter / retirer le stock d'un OF ouvert (AF / SA, SYN100 « AS » si sous-traitée), OF automatique (F10). Étape D faite (31/10) : option EC (événements de l'article dans le dossier, bouton 💬 de chaque OF). F8 (mise à jour du BAG de l'article, DOS595) non repris : les BAG se gèrent dans CloudFlow |
| ✅ | ~~Clé des OF~~ | GETDOSSIER / RAUCLEF | Clé calculée comme GETCLE (n° × client modulo 26) ; `Production:CleOf = PremierChiffreLibre` reproduit RAUCLEF du pilote (68537/9 → OF 68537/2) |
| 🟠 | Écarts du lancement à confirmer | NEG515, NEG510 F10 | Un lien de fabrication n'est plus transformé en affectation de stock (A2) ; l'affectation reçoit la date de livraison de l'OF (NEG515 écrivait 0, A1) ; en F10, dossier précédent et modèle du premier article stocké (NEG510 : du dernier) et catégorie de secours si aucun article stocké ; enseigne de la commande reprise aussi en CR ; AL refusé si l'OF sert déjà la commande pour l'article (NEG515 remplaçait la quantité) ; contrôle « ≤ disponible » en modification sans double compte de l'à fabriquer. Affectation : commande et OF verrouillés, total affecté ≤ commandé revérifié dans la transaction ; le disponible du lot, lui, n'est contrôlé qu'à la lecture (comme NEG515) |
| ✅ | ~~Recalcul du stock après retrait d'une affectation~~ | NEG515 SA → STOCK04 | Pas d'appel à STOCK04 : les liens « S » sont soldés et n'entrent pas dans l'attendu (PRQCDE). Validé par David le 31/10 |
| ✅ | ~~Suppression d'une commande et OF à ouvrir~~ | DOS500S SPS20 | Fait le 31/10 : après la suppression de la commande et de ses liens AB0020, la production supprime les OF à ouvrir (AB001T) qui ne servent plus à aucune commande, avec le suivi de leur BAG (`IInitialisationProduction.SupprimerOfsSansCommandeAsync`) |
| ✅ | ~~Reprise des lignes d'un devis~~ | NEG520 F10 (NEG52009, SPDEV, SPADR) | Fait le 31/10 : bouton « 📄 Ligne d'après un devis » de la fiche commande ; devis chiffrés du client (les plus récents d'abord, recherche par n° ou désignation), choix de l'adresse si le devis en a plusieurs ; ligne « à la demande » pré-remplie (désignation, unité, prix remis, cession d'après le prix de revient, quantité, adresse), puis complétée dans la fenêtre de ligne ; devis enregistré sur la ligne (DNDEV / DNVERS / DNHYPO) |
| 🟠 | Écarts et questions de la reprise d'un devis | NEG520 F10 | Seuls les devis du client de la commande sont proposés (NEG520 acceptait n'importe quel n°) ; quantité de l'adresse reprise aussi quand c'est un n° d'adresse du client (NEG520 la laissait à 0) ; « LT » = le tout, ramené au mille (la fiche de rétro-doc supposait « le mille ») ; désignation et unité du devis gardées si l'article est saisi ensuite (comme RECUP de CTL520). Devis **non marqué** « passé en commande » (D5000F.DETAG), comme NEG520 : décision de David du 31/10 |
| 🟠 | Articles avec nomenclature | NEG520 (lignes composants, DNLCOM) | Refusés aujourd'hui |
| ✅ | ~~Articles libres « 999… »~~ | CTL520 l. 804-845 | Abandonnés : plus utilisés (David, 31/10) ; toujours refusés à la saisie |
| ⚪ | Lignes de frais E / P, remises R | CTL520 l. 1643-1707, NEG520 SPCAVT | Refusées aujourd'hui |
| ✅ | ~~Changement de date de livraison en masse~~ | NEG520 F19 (NEG520CW) | Fait le 31/10 : bouton « 📅 Reporter des dates » de la fiche commande ; lignes non soldées dont la date de livraison ou confirmée vaut X → Y (livraison ou confirmée), en une transaction, stock recalculé ; refusé si une ligne fait partie d'un OPR non livré (comme NEG520). La rétro-doc indiquait F20 : c'est F19 |
| 🟠 | Import de commande (Excel) | ImportCommande (WinDev), NEGIMP / NEGIMPS, NEG520 R3 | Décision de David (31/10) : moderniser et fluidifier (aujourd'hui Excel → WinDev → 5250) : import depuis la fiche commande, aperçu des anomalies avant écriture, lignes écrites directement dans la commande en une transaction (sans transit NEGIMP), modèles d'import en base. En attente : fichier ImportCde.Ini de production (David, sous quelques jours) et usage des planches / sous-références (type 2) |
| ✅ | ~~Contrôle de la norme~~ | CTL520, NOR010F | Fait le 31/10 : norme de la ligne cherchée dans NOR010F pour l'enseigne de la commande (clé RSOC / NOENS / NOCOD), message NOR0001 ; recherche F4 sur la zone Norme |
| ✅ | ~~Paramètres de facturation automatique (F13)~~ | DOS505 DOS50505, AB001FA | Fait le 31/10 : bouton « 🧾 Facturation auto » de la fiche commande (lecture seule si la commande n'est pas modifiable) ; contrôles ADR0002, FAC0068, GEN0080 ; ÉCART : n° d'adresse vérifié dans A500F (ADR0003), DOS505 ne le contrôlait qu'en F4 |
| ✅ | ~~Barre d'actions de la fiche commande~~ | — | Fait le 31/10 : menu « ⋯ Plus » (ligne d'après un devis, report de dates, facturation automatique, solder / réactiver, supprimer), actions non permises grisées avec leur raison |
| ✅ | ~~Changement de client d'une commande (F17)~~ | DOS505 R15 | Fait le 31/10 : « Changer le client » du menu Plus (client, date, enseigne) ; contrôles CPT0001, CLI0004, BL00022, CPT0019, enseignes ; ÉCART : avertissement à accepter si la commande garde des n° d'adresse (lignes, facture, ARC) ou un client facturé de l'ancien client ; écriture refusée si l'en-tête a changé ou si un BL / une facture est apparu entre-temps ; tracé dans EVE200 « MO » |
| ✅ | ~~Texte complémentaire~~ | DOS560 (option 3 de NEG510), AB0011 | Fait (30/10) : saisi avec l'en-tête (8 lignes de 40, cases ARC / BL / facture), imprimé sur l'ARC si la case est cochée, supprimé avec la commande ; AB0011 journalisée |
| ✅ | ~~Événements et commentaires~~ | EVE300 (EC de DOS500S, option 5 de NEG510, EC de NEG515) | Fait le 31/10 : composant commun `Evenements` (Web.Commun) et service `IEvenements` (Noyau, EVE100 / EVE105 / DOS030, compteur AB001.CCTCOM) ; onglet « Événements » de la fiche commande (CDEC) et fenêtre 💬 de l'écran de lancement (LAN) |
| 🟠 | Écarts des événements à confirmer | EVE300 | Liste du plus récent au plus ancien ; profils du type, du fabricant et du représentant ajoutés à chaque choix de type (EVE300 les oubliait quand un seul type était proposé) ; tous les profils prévenus modifiables (EVE300 : les 5 premiers, les suivants perdus) ; indicateurs de rappel d'un autre événement plus recopiés ; compteur de commentaires du dossier lancé aussi en modification (EVE300 prenait la commande) ; fenêtre restée ouverte après enregistrement (EVE300 sortait) ; heure saisie avec la date prévue (EVE0005) conservée |
| ⚪ | Événements : édition de la liste (F21, EVE301P) et autres objets (article, modèle, commandes fournisseur, ligne de commande) | EVE300 | Codes ART, MOD, CDES, CDEP, CDEM, CDCL, BAG, MAQ à brancher avec leurs écrans ; l'accès à l'objet est vérifié par l'écran appelant |
| ⛔ | ~~Copie / transfert de reliquat d'une ligne vers une autre commande (NEG510 F14)~~ | — | Abandonné (David, 30/10) |
| ⚪ | Transformation d'un dossier de fabrication en commande | DOS500S option TR | RAUCLEF absent des sources |
| ✅ | ~~Création depuis une proforma~~ | DOS505 R20 | Abandonnée (décision de David du 31/10) |
| ✅ | ~~Recherche d'article (F4) dans la saisie de ligne~~ | FEN_RecArticle | Fait le 31/10 : bouton « … » et F4 de la zone Article, articles du client de la commande (liste « Article » du Noyau, branche `noyau/liste-articles`) |

### 1.3 Techniques

| Prio | Sujet | Notes |
|---|---|---|
| ✅ | ~~Journalisation des tables satellites~~ | Fait par David (30/10) ; la suppression d'une commande les vide dans sa transaction. Test `JournalisationDb2Tests` : vérifie que toutes les tables écrites sont journalisées |
| 🟠 | Journaliser **EVE200** (audit des dossiers) | Seule table écrite encore non journalisée : l'audit est écrit juste après la validation. Une fois journalisée, l'écrire dans les transactions et l'ajouter au test |
| 🟠 | Moteur de prix non validé sur de vrais tarifs | La base de test n'a presque pas de tarifs (SPF011 : 2 lignes) ; comparaison à refaire sur une copie de production |
| 🟠 | STOCK03 appelé par QCMDEXC | Dépend de la liste de bibliothèques du travail ODBC (NP11F, NP11P) ; à recoder en C# ou à exposer proprement |
| ⚪ | Nombre de connexions par action | Paramètres société relus à chaque appel : cache de session à prévoir |
| 🟠 | SYN100 : heure en horodatage | HTHEUR est un TIMESTAMP ; le code écrivait un entier HHMMSS (demandes « CDE » des commandes sous-traitées, jamais exercé sur le client d'essai). Corrigé le 30/10 sans essai réel (pas de commande sous-traitée d'essai) : à vérifier sur une commande CCOP10 |
| ⚪ | Verrou applicatif PRTLCK | Remplacé par un contrôle d'empreinte (verrou optimiste) ; à confirmer pour la cohabitation avec le 5250 |

---

## 2. Socle et Noyau (communs, à voir avec le collègue)

| Prio | Sujet | Notes |
|---|---|---|
| 🔴 | Connexion IBM i impossible sur le poste de David | `BaseDb2.VerifierProfilAsync` reconstruit la chaîne ODBC : « Missing system name ». Contourné en mode démo |
| 🟠 | Fonctions ODBC (`Commande`, `Lire`, `Executer`) recopiées dans chaque classe `Donnees` | À regrouper dans `BaseDb2` |
| ✅ | ~~Liste de choix « Article »~~ | Ajoutée à `IListesDeChoix` (branche `noyau/liste-articles`, demande de fusion dédiée) : code, désignation, référence imprimée ; parent facultatif = « compte général compte client » |
| 🟠 | Service Compteurs (n° de dossier, GETDOSSIER) | Cible du cadrage : attribution atomique |
| 🟠 | Qualité client (CQUACL) | Règle transverse devis / commande / BL : à porter dans le Noyau |
| ✅ | ~~Éditions PDF~~ | Socle `Documents` (QuestPDF, page standard, aperçu, formats) et Noyau `ISocietes.LireIdentiteAsync` (en-tête, mentions légales, logo) : à réutiliser pour l'édition du devis (`docs/editions-pdf.md`) |
| 🟠 | Licence QuestPDF | Community gratuite sous 1 M$ de CA annuel ; sinon licence Professional, ou bascule sur PDFsharp/MigraDoc (MIT) |
| ⚪ | Droit du profil sur la société à la connexion (PRT000P1) | Déjà noté dans decisions.md |

## 3. Production

| Prio | Sujet | Notes |
|---|---|---|
| 🟠 | OF (type 2) et commandes simples (type 1) | Listés par HistoriqueDossiers, non repris (hors module Ventes) |
| 🟠 | Lancement en fabrication, création auto des OF | Voir 1.2 |
| ⚪ | Liens commande → OF (AB0020) supprimés par les Ventes | À la suppression d'une commande, les Ventes suppriment encore elles-mêmes ses liens AB0020 (dans leur transaction), puis la production nettoie les OF à ouvrir restés sans lien. À terme, la production devrait libérer elle-même les liens d'une commande (méthode du contrat) |

## 4. Logistique et EDI

| Prio | Sujet | Notes |
|---|---|---|
| 🔴 | Appels de livraison depuis le navigateur logistique et le tableau de bord EDI | Le moteur existe : `IAppelsLivraison` (Ventes.Contrats), canaux `Navigateur` / `Edi`. Remplace TNEG100N + NEG300NAV (C/V/S/A) : un seul appel, contrôles et avertissements explicites (plus de chaîne FRCCTR). L'EDI écrit EDI105 après la création ; une modification EDI devient `EnregistrerAsync` (plus de suppression / recréation) |
| ⚪ | BL, ordres de préparation (OPR) | Lus dans la fiche commande ; une ligne d'un OPR non livré n'est plus modifiable |

## 5. Organisation

| Prio | Sujet | Notes |
|---|---|---|
| 🟠 | Demande de fusion de `commandes/liste-dossiers` | La branche contient aussi `socle/outillage-equipe` (non fusionnée dans `main`) |
| 🟠 | `docs/donnees-de-test.md` | Société 8, client 411100 376 (Clarins) ; fabricant « LE » inexistant (« LEN » existe) ; compteurs de dossiers en retard sur les données |
| ⚪ | `docs/decisions.md` | Y reporter les décisions D1 à D9 une fois validées |
| ⚪ | Tests d'écriture | Lancés seulement avec `NETPRINT_TESTS_ECRITURE=1` ; ils nettoient ce qu'ils créent |
