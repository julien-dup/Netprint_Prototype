# NetPrint SaaS — consignes pour Claude Code

Reconstruction de NetPrint (ERP imprimerie : RPG/DDS sur IBM i + DB2, écrans WinDev, MenuNetprint WinForms)
en une application Blazor .NET 10, module par module. Lire d'abord `README.md` (organisation, règles, démarrage)
et `docs/decisions.md` (décisions validées et état du prototype).

## Langue
Tout en français : code métier (classes, méthodes, propriétés), interface, messages, commentaires, commits,
demandes de fusion.

## Commandes
- Compiler : `dotnet build NetPrint_Prototype.slnx`
- Tests : `dotnet test NetPrint_Prototype.slnx` (dont les règles d'architecture : à lancer avant chaque commit)
- Lancer : projet `src/NetPrint.Web` (http://localhost:5100) ou `src/NetPrint.AppHost` (tableau de bord Aspire)
- Secrets : `dotnet user-secrets` dans `src/NetPrint.Web` (`ConnectionStrings:Db2`). Jamais de mot de passe dans le dépôt.

## Règles non négociables (README, § Règles)
1. Dépendances : Module → Noyau → Socle. Deux modules ne se référencent jamais (seulement leurs `.Contrats`).
2. Écrans minces : règles dans `Services/`, SQL dans `Donnees/`.
3. La société vient toujours de la session (`IUtilisateurCourant`).
4. SQL paramétré (`?` + paramètres), jamais de valeur concaténée.
5. Charte d'interface : rien chargé à l'ouverture, pas de pagination (garde-fou 10 000 lignes), pas d'`alert`/`confirm`.

## Base de données
- Pilote sur **DB2**, bibliothèque **NP11F de l'AS400 de test** (jamais la production), partagée par les deux
  développeurs : ne pas vider ni recharger de table, ne modifier que des données d'essai.
- Écrire dans les **mêmes tables que le WinDev et le RPG**. Avant de modifier une table existante, vérifier qui
  l'utilise dans la cartographie (`carto_tables.csv`).
- Programmes et fonctions IBM i appelés (GETDOSSIER, NEG300NAV, DEV100NAV, STOCK03…) : bibliothèque programmes
  `Db2:BibliothequePgm` (NP11P par défaut).
- PostgreSQL viendra après le pilote : garder tout le SQL dans `Donnees/`.

## Référence métier (rétro-documentation)
Sur le poste de David : `D:\docs\Netprint\` (copie chez Julien : `C:\SAAS\RetroDoc Netprint pour SAAS`) :
- `retrodoc-saas\module-devis-ventes\REGLES-MODULE-DEVIS-VENTES.md` : règles du module Ventes, écrans cibles,
  anomalies à ne pas reproduire, décisions ouvertes ;
- `retrodoc-saas\module-devis-ventes\fiches\` (projets WinDev) et `retrodoc-saas\fiches\` (programmes RPG) ;
- `retrodoc-saas\glossaire-metier.md` : codes et statuts (CCTYPC, AREMPL, CQUACL, TNUM « Le tout »…) ;
- `cartographie-db2\` : tables, colonnes, programmes, projets WinDev, modules.
Sources : RPG dans `D:\dvp\sources AS400\NP11`, WinDev dans `D:\Mes Projets`.

Faits établis à respecter :
- **DEV100 (RPG) fait foi** pour le calcul des devis ; Devis2017 (C#) n'est pas en production (précalcul CAR : suivre le RPG).
- Unité de vente avec `TNUM = 999999999` = **« Le tout »** (prix forfaitaire).
- Autorisations PRT000P1 AOPT01-08 : Total, Création, Modification, Suppression, Visualisation, Calcul, Édition, MAJ ; `**` = interdit.
- Les compteurs d'usage IBM i (OBJECT_STATISTICS) sont faux pour de nombreux programmes : ne pas en déduire qu'un programme est inutilisé.

## Façon de travailler (deux développeurs)
- Une tâche = une branche (`ventes/...`, `noyau/...`, `correctif/...`) = une demande de fusion relue par l'autre.
- Jamais de commit ni d'envoi direct sur `main` (garde-fou : `git config core.hooksPath .githooks`).
- Versions NuGet uniquement dans `Directory.Packages.props`.
- Scripts SQL : nom préfixé par date et heure, appliqués une fois, par leur auteur, après la fusion.
