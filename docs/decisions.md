# Prototype SaaS NetPrint — synthèse et décisions

_Mise à jour : 29/10/2026_

## Contexte
NetPrint (ERP imprimerie : RPG/DDS sur IBM i + DB2, écrans WinDev, MenuNetprint WinForms) est reconstruit en SaaS multi-tenant, module par module, sans big bang (2 à 3 ans). Références : PDF « Netprint SaaS — Cadrage stratégique avec Blazor et analyse financière » (C:\SAAS\Outils) et rétro-doc C:\SAAS\RetroDoc Netprint pour SAAS (cartographie DB2, carto_modules.csv, glossaire métier, modèle cible du noyau 28 tables).

## Décisions validées (29/10/2026, Julien)
- **PortailNetPrint et GestionOutilsPlats = essais**, pas le prototype. On réutilise leurs acquis (session, BaseNetPrint + TRI_AS400, Positionnel, NetPrint.Reprise, AppHost Aspire, charte d'interface).
- **Prototype démarré le 29/10/2026** dans **C:\SAAS\NetPrint_Prototype** (dépôt Git, sauvegardé sur GitHub : **Netprint_Prototype**, 30/10/2026).
- **Front : Blazor** (Blazor Web App, Interactive Server), tout en C#.
- **Style graphique : celui de GestionOutilsPlats (nouvelle interface)** — décision du 29/10 après-midi. MudBlazor est retiré : HTML + `app.css` (base : `index.css` de GestionOutils) et petits composants maison (dialogues, notifications, titres de colonnes triables).
- **.NET 10**. **DbUp** pour les migrations PostgreSQL (scripts SQL numérotés).
- **Une seule solution, une seule application web** contenant les écrans de tous les modules (monolithe modulaire).
- **Un projet par module métier** (7 modules + Noyau, pas un projet par appli WinDev). Les ~99 applis WinDev deviennent des écrans dans `Ecrans/` de leur module.
- **Échanges entre modules par Contrats** (projet `NetPrint.Modules.Xxx.Contrats`) ; navigation libre entre écrans de modules (assistants pas à pas, ex. devis → commande → dossier).
- **Premier module : Ventes (pilote Devis)**, branché d'abord sur **DB2** (couche `Donnees/` isolée).
- **Référence de calcul : DEV100 (RPG) fait foi** (seul programme de devis en production).
- **Ordre : consultation d'abord** (reprise de ConsultationDevis WinDev, branche `C:\Mes Projets.Branches\DLLNew\ConsultationDevis`), puis moteur de calcul, puis saisie.
- **Fusion des deux branches ConsultationDevis** : saisie par la DLL (branche DLLNew) + tags (branche D:\Mes Projets).
- **Connexion : session simple** (profil IBM i + société) derrière `IAuthentificateur` / `IUtilisateurCourant` ; Entra ID plus tard.
  - Menu utilisateur → « ⚙ Paramètres » (`/parametres`) : changer de société, de compte général, de compte client par défaut sans se reconnecter (POST `/compte/parametres`, session rouverte, « Rester connecté » conservé ; changement de société = retour à l'accueil). À faire : droit du profil sur la société (PRT000P1), existence du compte client.
- **Commande et OF séparés** dans le modèle cible : Commande → Ventes, OF / dossier de fabrication → Production (AB001 aujourd'hui pour les deux).

## Modules (d'après carto_modules.csv, validé 28/10/2026)
| Ordre | Projet | Module | Applis WinDev | Préfixe URL |
|---|---|---|---|---|
| 1 | NetPrint.Noyau | Noyau et référentiels | 7 (Clients, DonnéesDeBase, Parametres, Securite, Sections, Tables, ComposantsExternes) | /referentiels |
| 2 | NetPrint.Modules.Ventes | Ventes (pilote) — appli affichée : « Consultation devis » | 12 | /ventes |
| 3 | NetPrint.Modules.Production | Production | 27 | /production |
| 4 | NetPrint.Modules.Logistique | Stocks et logistique | 30 | /logistique |
| 5 | NetPrint.Modules.Achats | Achats et sous-traitance | 4 | /achats |
| 6 | NetPrint.Modules.Facturation | Facturation et comptabilité | 6 | /facturation |
| 7 | NetPrint.Modules.Qualite | Qualité | 5 | /qualite |
| 8 | NetPrint.Modules.Pilotage | Pilotage | 3 | /pilotage |

Transverses : Éditions → service documents dans le Socle (+ agent d'impression) ; Technique (3 applis) disparaît.

## Points d'attention relevés dans la rétro-doc
- AB001 = commande ET dossier (CCTYPC 2 = fabrication/OF, 3 = négoce, 4 = produit fini ; FAC402P teste 1 = fabrication : divergence à clarifier).
- **Produits** partagé entre Ventes (commercial) et Production (fiche de fabrication) : propriétaire à décider.
- **ComposantsExternes** (143 tables) rangé dans le Noyau mais fourre-tout : à éclater par module.
- Bascule PostgreSQL : Production, Logistique et Facturation ensemble (dossiers, BL, mouvements partagés).
- Schéma noyau PostgreSQL (01_noyau_schema.sql) : futur script 0001 DbUp.
- Contrôle d'accès aux sociétés à la connexion (PRT000P1, code STE) : pas encore fait.

## État du prototype (29/10/2026)
- Squelette : NetPrint.Web (connexion, accueil, barre d'application), Socle, Noyau (sociétés), Web.Commun, AppHost Aspire, NetPrint.Reprise, tests d'architecture et du Socle. Compilation validée (10/10 projets).
- **Consultation des devis — étape 1** (lecture seule, DB2) :
  - `/ventes/devis` : reprise de FEN_ConsultationDevis — 45 critères (principaux + « Plus de critères » : commercial, formats, papier, fabrication), contrôles de la procédure Controle (mêmes messages), restriction représentant B000F.ILIMIT appliquée côté serveur, liste des 30 colonnes, couleurs offre / commande / échec, valeur du devis (TNUM 999999999 = « Le tout »), titres de colonnes (composant commun `EnteteColonne`) : bouton de tri toujours visible (▲ gris / ▲ / ▼ bleus, clic = tri direct puis ordre par défaut), clic sur le titre = bulle de filtre (contient, commence par… selon le type : texte / nombre / date / oui-non), filtrage en mémoire sur les lignes chargées, pastilles des filtres actifs ; colonnes masquables (menu « ▦ Colonnes ») et redimensionnables à la souris, affichage et tri mémorisés par utilisateur et société, menu contextuel.
  - `/ventes/devis/{n}/{v}/{h}/{type}` : reprise de FEN_DetailDevis — vue par atelier (onglets ateliers, Papier, Matières, Sous-traitance, Transport, Frais annexes) et vue par élément (fabrication, rubriques et réponses, support et gâche, papier en bobine, matières, sous-traitance, outil).
  - Écarts volontaires : offre comptée (évite l'erreur multi-langue), taux de gâche du devis dans l'onglet Papier (variable non initialisée dans le WinDev), restriction représentant vérifiée aussi à l'ouverture d'un devis, type de coût réel pour Prépresse/Façonnage (le WinDev forçait 'C').
- **Compléments de la consultation** (compte rendu `docs/compte-rendu-complements-consultation.md`, décisions du 30/10/2026) :
  - écriture **dans les mêmes tables que le WinDev**, bibliothèque NP11F (l'IP configurée est celle du serveur de test) ;
  - sources : ConsultationDevis DLLNew + `C:\Mes Projets\ConsultationDevis` (FEN_Tags) + `C:\Mes Projets\ComposantsExternes` (FEN_TableauPrix) ;
  - tags : on peut retirer un tag, plus de doublons ; récap PDF : DEV100NAV gardé, PDF lu par SQL dans l'IFS ; coefficients : « Le tout » (999999999) corrigé ;
  - **lot A fait** (lecture seule) : texte libre (devis et élément), récapitulatif des sous-totaux, tableau des coefficients (imprimable), valeurs par défaut (préférences serveur) ;
  - **lot B fait** (écriture NP11F, sans transaction comme le WinDev) : saisie / suppression de l'échec (D50006, un par n° de devis, motif choisi dans la liste ME), documents (D50008 : liste, ouverture par `/api/ventes/devis/documents/{id}`, ajout par sélection ou glisser-déposer, 7 Mo, suppression selon PRT000P1 PCDOCUMENT), tags (TAG_DEVIS + D50010 : poser / retirer, création, 50 tags, 20 caractères) ; la liste se met à jour (couleur échec, colonne Docs, double-clic sur Docs = documents) ;
  - **lot C fait** : tableau des prix (FEN_TableauPrix de ComposantsExternes : ateliers, support, matière consommable, sous-traitance, transport, valorisation méthode A ou globale, colonnes « 1000 + », impression) ; récapitulatif PDF (`/api/ventes/devis/{n}/{v}/{h}/{type}/recapitulatif` : CALL QSYS2.QCMDEXC de NP11P/DEV100NAV, PDF relu par QSYS2.IFS_READ_BINARY, ouvert dans un nouvel onglet ; bibliothèque des programmes : `Db2:BibliothequePgm`, NP11P par défaut) ;
  - **lot D fait** : recherches F4 / bouton « … » (style GestionOutils) sur les 21 zones de critères qui en avaient une dans le WinDev ; service commun `IListesDeChoix` dans le **Noyau** (référentiels partagés : clients, prospects, profils, catégories, sous-catégories, fabricants, représentants, normes, dossiers, papiers, sortes, sous-sortes, coloris, machines, opérations, matières, fournisseurs, opérations de sous-traitance, motifs d'échec ; SQL des FEN_Rec* de ComposantsExternes) ; composants `BoutonRecherche` + `FenetreChoix` (affichée par `ZoneChoix` dans la mise en page) dans Web.Commun, qui référence désormais le Noyau ; F4 dans une zone = clic sur son bouton « … » (saisie.js) ;
  - à faire : lot E (décomposition et prix remis) ; offre, étude, historique, modification restent grisés.
- Préférences d'affichage des grilles (colonnes masquées, largeurs, tri) : côté serveur, jamais dans le navigateur (charte). Prototype : `App_Data/preferences/<profil>-<société>.json` derrière `IPreferencesGrilles` ; table PostgreSQL quand le schéma sera en place.
- Saisie (tous les écrans, `NetPrint.Web.Commun/wwwroot/saisie.js` + app.css) : zones numériques sans flèches d'incrément (ni molette, ni touches haut / bas) ; double-clic dans une zone date = date du jour. Fenêtres internes déplaçables par leur barre de titre ; un fichier glissé hors d'une zone de dépôt est ignoré (le navigateur ne l'ouvre plus à la place de l'appli).
