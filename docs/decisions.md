# Prototype SaaS NetPrint — synthèse et décisions

_Mise à jour : 29/10/2026_

## Contexte
NetPrint (ERP imprimerie : RPG/DDS sur IBM i + DB2, écrans WinDev, MenuNetprint WinForms) est reconstruit en SaaS multi-tenant, module par module, sans big bang (2 à 3 ans). Références : PDF « Netprint SaaS — Cadrage stratégique avec Blazor et analyse financière » (C:\SAAS\Outils) et rétro-doc C:\SAAS\RetroDoc Netprint pour SAAS (cartographie DB2, carto_modules.csv, glossaire métier, modèle cible du noyau 28 tables).

## Décisions validées (29/10/2026, Julien)
- **PortailNetPrint et GestionOutilsPlats = essais**, pas le prototype. On réutilise leurs acquis (session, BaseNetPrint + TRI_AS400, Positionnel, NetPrint.Reprise, AppHost Aspire, charte d'interface).
- **Prototype démarré le 29/10/2026** dans **C:\SAAS\NetPrint_Prototype** (dépôt Git).
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
- **Pas encore repris** : documents, échec, tags, décomposition et prix remis, tableau des prix, coefficients, sous-totaux, texte libre, offre, récapitulatif PDF, valeurs par défaut, recherches F4, modification (DLL).
- Préférences d'affichage des grilles (colonnes masquées, largeurs, tri) : côté serveur, jamais dans le navigateur (charte). Prototype : `App_Data/preferences/<profil>-<société>.json` derrière `IPreferencesGrilles` ; table PostgreSQL quand le schéma sera en place.
