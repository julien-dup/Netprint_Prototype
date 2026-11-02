# Déploiement en recette — VM AXI-SAAS (IIS)

*30/10/2026. Notre application (`NetPrint.Web`) **remplace le Portail NetPrint** à la racine du site IIS. GestionOutils reste dans sa sous-application `/gestionoutils`.*

## 1. Ce qui change par rapport au Portail

| | Portail (avant) | NetPrint.Web (maintenant) |
|---|---|---|
| Racine du site `/` | NetPrint.Portail.Api (React) | **NetPrint.Web** (Blazor) : connexion, accueil, Consultation devis |
| `/gestionoutils` | sous-application | **inchangée** |
| Connexion unique | cookie `NetPrint.Session`, clés `C:\SAAS\ClesSession` | **identique** : même cookie, mêmes informations de session, même nom de clés `NetPrint` |
| Pool IIS de la racine | `NetPrintPortail` | on **garde ce pool** (et sa chaîne de connexion DB2) |

Fichiers ajoutés au projet pour la recette :

- `src/NetPrint.Web/web.config` :
  - `inheritInChildApplications="false"`, sinon `/gestionoutils` hériterait du module ASP.NET Core de la racine et ne démarrerait plus.
- `src/NetPrint.Web/appsettings.Production.json` :
  - dossier des clés `C:\SAAS\ClesSession`, indispensable pour partager la session avec GestionOutils ;
  - tuile « Gestion des outils plats » sur l'accueil.
- **Accueil** :
  - affiche les tuiles des applications externes ;
  - gère `?retour=…`. Quand GestionOutils n'a pas de session, il renvoie vers `/` ; une fois connecté, l'utilisateur revient dans GestionOutils.

## 2. Vérifications sur AXI-SAAS (une seule fois)

1. **Hosting Bundle .NET 10** : déjà installé, puisque le Portail et GestionOutils sont en .NET 10.
2. **Pilote ODBC IBM i Access** : déjà installé, puisque le Portail se connecte à l'IBM i.
3. **WebSocket** (indispensable pour Blazor Server, sinon l'écran est lent ou se reconnecte sans cesse) :
   - *Gestionnaire de serveur → Ajouter des rôles et fonctionnalités → Serveur Web (IIS) → Serveur Web → Développement d'applications → **Protocole WebSocket***.
4. **Chaîne de connexion DB2** du Portail, à retrouver avant d'écraser quoi que ce soit :
   - *Gestionnaire IIS → Pools d'applications → NetPrintPortail → Paramètres avancés*, ou *Éditeur de configuration → `system.applicationHost/applicationPools` → variables d'environnement*. Cherche `ConnectionStrings__Db2`.
   - Sinon, regarde dans le dossier du Portail s'il existe un `appsettings.Production.json` qui contient `ConnectionStrings`. **Copie son contenu avant l'étape 4** : notre publication contient son propre `appsettings.Production.json`, qui l'écraserait.
5. **Chemin physique du site** : *Gestionnaire IIS → Sites → (le site) → Paramètres de base*. C'est le dossier du Portail qui sera écrasé.

## 3. Publier depuis Visual Studio (poste de développement)

*Clic droit sur **NetPrint.Web** → Publier → Dossier* :

- configuration **Release** ;
- framework **net10.0** ;
- mode **dépendant du framework** ;
- runtime **win-x64**, ou Portable ;
- dossier `C:\SAAS\Publication\NetPrint`.

Ou en ligne de commande, depuis `C:\SAAS\NetPrint_Prototype` :

```powershell
dotnet publish src\NetPrint.Web -c Release -o C:\SAAS\Publication\NetPrint
```

Contrôle : le dossier contient `NetPrint.Web.dll`, `web.config`, `appsettings.json`, `appsettings.Production.json` et `wwwroot`.

## 4. Installer sur AXI-SAAS

1. **Arrêter l'application** : *Gestionnaire IIS → Pools d'applications → NetPrintPortail → Arrêter*. Cela libère les DLL verrouillées.
2. Facultatif, pour garder une porte de sortie : zipper le dossier du Portail.
3. **Vider le dossier du Portail**, sauf `logs` si tu veux garder l'historique, puis y copier tout le contenu de `C:\SAAS\Publication\NetPrint`.
   - **Ne pas toucher** au dossier de GestionOutils ni à `C:\SAAS\ClesSession`.
4. **Chaîne DB2** :
   - si elle était dans une variable du pool, il n'y a rien à faire ;
   - si elle était dans l'ancien `appsettings.Production.json`, ajoute la section `ConnectionStrings` dans le nouveau :
     ```json
     "ConnectionStrings": { "Db2": "<chaîne reprise du Portail>" }
     ```
5. **Droits d'écriture** :
   - créer le dossier `App_Data` dans le dossier du site et donner **Modifier** à `IIS AppPool\NetPrintPortail`. Les préférences d'affichage des grilles y sont enregistrées (`App_Data\preferences`).
   - Le droit **Modifier** sur `C:\SAAS\ClesSession` existe déjà pour ce pool.
6. **Démarrer** le pool `NetPrintPortail`.

## 5. Recette

1. `https://<AXI-SAAS>/` → écran de connexion NetPrint → accueil avec deux tuiles : **Consultation devis** et **Gestion des outils plats**.
2. Tuile Consultation devis : recherche, détail, compléments (tableau des prix, récapitulatif PDF, documents, décomposition…).
3. Tuile Gestion des outils plats : GestionOutils s'ouvre **sans nouvelle connexion**.
4. Ouvrir directement `https://<AXI-SAAS>/gestionoutils/` dans une fenêtre privée → écran de connexion NetPrint → après connexion, retour dans GestionOutils.
5. « Se déconnecter » dans l'une des applis ferme la session dans les deux.

## 6. En cas de problème

| Symptôme | Cause probable | Correction |
|---|---|---|
| Erreur 500.19 ou 500.30 au démarrage | web.config, ou chaîne DB2 absente | passer `stdoutLogEnabled="true"` dans `web.config`, créer `logs`, relancer, lire `logs\stdout_*.log` |
| `/gestionoutils` en erreur 500 depuis la mise à jour | sous-application qui hérite du module de la racine | vérifier que le `web.config` de la racine contient bien `inheritInChildApplications="false"` |
| GestionOutils redemande la connexion | clés différentes | même dossier `C:\SAAS\ClesSession` des deux côtés (appsettings.Production.json de NetPrint, appsettings.json de GestionOutils) |
| Bandeau « reconnexion » qui revient sans cesse | WebSocket absent | installer le protocole WebSocket (§ 2.3) |
| Préférences de grille non enregistrées | droits sur `App_Data` | § 4.5 |

**Retour arrière** : arrêter le pool, remettre le contenu du zip du Portail, puis redémarrer le pool.
