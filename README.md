# NetPrint — prototype SaaS

Reconstruction de NetPrint en SaaS : **une seule application Blazor** (.NET 10, Interactive Server, style de la charte NetPrint repris de GestionOutils)
découpée en **modules métier**, données DB2 pendant la cohabitation puis PostgreSQL.
Décisions et choix : [`docs/decisions.md`](docs/decisions.md). Cadrage : PDF « Netprint SaaS — Cadrage stratégique ».

## Démarrer

1. Ouvrir `NetPrint_Prototype.slnx` dans Visual Studio 2026.
2. Projet de démarrage : **NetPrint.AppHost** (tableau de bord Aspire) ou **NetPrint.Web** (http://localhost:5100).
3. F5.

La chaîne `ConnectionStrings:Db2` (ODBC IBM i) est lue dans les *user secrets* : NetPrint.Web utilise le même
`UserSecretsId` que les essais GestionOutils, elle est donc déjà là sur le PC de dev. Sinon :

```
cd src\NetPrint.Web
dotnet user-secrets set "ConnectionStrings:Db2" "Driver={IBM i Access ODBC Driver};System=...;UID=...;PWD=..."
```

**Sans IBM i** : mettre `"Authentification": { "ModeDemo": true }` dans `appsettings.Development.json`.
Toute connexion est alors acceptée (développement uniquement) ; les écrans qui lisent DB2 afficheront une erreur.

Connexion : profil et mot de passe IBM i, code société (AA0007), compte général (411000 par défaut).

**Logos des éditions PDF** (ARC, devis…) : copier les fichiers BMP / JPEG des éditions WinDev (répertoire NetPrint
d'un poste) dans `src/NetPrint.Web/Logos`, sans les renommer. Mode d'emploi : `src/NetPrint.Web/Logos/LISEZMOI.md` ;
production des PDF : `docs/editions-pdf.md`.

## Organisation

```
src/
  NetPrint.Web/             la seule application : accueil, connexion, menu, Program.cs
  NetPrint.Web.Commun/      composants d'interface partagés (dialogues, notifications, titres de colonnes triables)
  NetPrint.Socle/           technique : session, utilisateur courant, accès DB2 / PostgreSQL
  NetPrint.Noyau/           métier partagé : sociétés, tiers, utilisateurs, référentiels
  Modules/
    NetPrint.Modules.Ventes/            module Ventes (pilote) : appli « Consultation devis » (ex-ConsultationDevis WinDev)
      Ecrans/  Services/  Donnees/  (Api/  Impressions/  au besoin)
    NetPrint.Modules.Ventes.Contrats/   ce que les autres modules ont le droit d'utiliser
  NetPrint.AppHost/         lanceur Aspire (développement)
  NetPrint.Reprise/         copie DB2 → PostgreSQL (repris des essais)
db/migrations/              scripts SQL numérotés (DbUp), quand un module passera sur PostgreSQL
tests/                      règles d'architecture, tests du Socle (et un projet de tests par module)
```

Modules prévus (ordre du cadrage) : Noyau · **Ventes** (pilote) · Production · Logistique · Achats · Facturation · Qualite · Pilotage.

## Règles

1. **Dépendances dans un seul sens** : Module → Noyau → Socle. Le Socle ne contient aucune règle métier.
2. **Deux modules ne se référencent jamais** : un module n'utilise que les **Contrats** d'un autre
   (ex. Production appelle `IConsultationDevis` de `NetPrint.Modules.Ventes.Contrats`). La navigation entre écrans
   de modules différents, elle, est libre (liens, assistants pas à pas).
3. **Écrans minces** : un écran appelle les services du module, jamais la base. Règles dans `Services/`, SQL dans `Donnees/`.
4. **La société vient toujours de la session** (`IUtilisateurCourant`), jamais d'un paramètre de l'écran ou de l'API.
5. **SQL paramétré** (`?` + `Parameters.Valeur`), jamais de valeur saisie concaténée.
6. **Charte d'interface** (CHARTE-INTERFACE.md) : rien n'est chargé à l'ouverture, recherche sans critère = confirmation,
   pas de pagination (garde-fou 10 000 lignes), tout en français, pas de `alert` / `confirm` (services `Dialogues` et `Notifications` de Web.Commun).
7. Les règles 1 à 3 sont vérifiées par `tests/NetPrint.Architecture.Tests` : lancer les tests avant chaque commit.

## Ajouter un module (ex. Production)

1. Créer `src/Modules/NetPrint.Modules.Production` (copier le `.csproj` de Ventes) et
   `src/Modules/NetPrint.Modules.Production.Contrats`.
2. Écrire `ProductionModule.cs` avec `AddModuleProduction()` (déclare sa `DescriptionModule` et ses services)
   et `MapModuleProduction()` (ses points d'API éventuels).
3. Dans `NetPrint.Web` : référence au projet + `builder.Services.AddModuleProduction();` + `app.MapModuleProduction();`.
   Le menu, l'accueil et le routage des écrans se mettent à jour tout seuls.
4. Ajouter l'assemblage dans `Modules` de `tests/NetPrint.Architecture.Tests/ReglesArchitecture.cs`.
5. Ajouter les projets à `NetPrint_Prototype.slnx`.

## Préfixes d'adresses

Écrans : `/ventes/...`, `/production/...` ; API : `/api/ventes/...`, `/api/production/...`.

## Travailler à deux

Méthode complète : document « Netprint SaaS — Travailler à deux sur la solution ».

- **Une tâche = une branche = une demande de fusion** (pull request), relue par l'autre développeur.
  Branches nommées par module et sujet : `ventes/decomposition`, `noyau/compteurs`, `correctif/arrondi-proforma`.
- **Jamais d'envoi direct sur `main`.** Le plan GitHub gratuit n'applique pas les règles de protection :
  activer une fois par poste le garde-fou local, qui refuse un `git push` vers `main` :

  ```
  git config core.hooksPath .githooks
  ```
- **Fusion par le bouton de la demande de fusion sur GitHub**, une fois la relecture faite et la vérification
  automatique au vert (compilation + tests, `.github/workflows/build.yml`).
- **Versions des paquets NuGet** : uniquement dans `Directory.Packages.props` (les `.csproj` n'ont pas de `Version`).
- **Style** : `.editorconfig` (Visual Studio l'applique au formatage, Ctrl+K, Ctrl+D).
- **Base commune** : les deux postes travaillent sur NP11F de l'AS400 de test. Chacun ses données d'essai ;
  pas de vidage de table sans prévenir ; un script SQL de `db/` s'applique une fois, par son auteur, après la fusion.
