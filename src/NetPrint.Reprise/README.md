# NetPrint.Reprise : copie Db2 (IBM i) → PostgreSQL

Recopie les 9 tables de Gestion des outils plats (schéma `NP11F`) de Db2 vers PostgreSQL.
Les tables cibles sont **vidées puis rechargées**, dans une seule transaction :
si une erreur survient, la base PostgreSQL reste telle qu'elle était.

Ordre : saaspardef, aa0007, aa00081, aa0003, b500f, fou010, abcoutd, fou010d, saasparval, spf010, spf025.

spf010 (articles, 178 zones) et spf025 (liens articles / outils) : créer d'abord les tables avec
`06_articles_spf010_spf025.sql`, puis `--tables=spf010,spf025` pour ne charger qu'elles.

## Lancer depuis le PC de dev

La chaîne `ConnectionStrings:Db2` est déjà dans les user secrets (même UserSecretsId que GestionOutils).
Ajouter une fois la cible PostgreSQL (compte `postgres` : il faut le droit de vider les tables) :

```
cd C:\SAAS\MesProjets\NetPrint.Reprise
dotnet user-secrets set "ConnectionStrings:Pg" "Host=localhost;Port=5432;Database=netprint;Username=postgres;Password=..."
dotnet run
```

Vers la VM de recette (port 5432 ouvert pour le PC) sans toucher aux secrets :

```
dotnet run -- --cible="Host=<IP-AXI-SAAS>;Port=5432;Database=netprint;Username=postgres;Password=..."
```

Options : `--tables=fou010,fou010d` (seulement certaines tables), `--oui=1` (pas de confirmation).

⚠ `TRUNCATE ... CASCADE` : recharger `fou010` seule vide aussi `fou010d` (clé étrangère).
Recharger les deux ensemble : `--tables=fou010,fou010d`.

## Lancer sur la VM AXI-SAAS

Il faut le pilote ODBC IBM i Access (déjà présent si GestionOutils y tourne en Db2) et le runtime .NET 9.

```
dotnet publish -c Release -o publish
```

Copier `publish` sur la VM, puis :

```
NetPrint.Reprise.exe --ConnectionStrings:Db2="<chaîne ODBC IBM i>" --cible="Host=localhost;Database=netprint;Username=postgres;Password=..."
```

## À la fin

Le programme affiche le nombre de lignes Db2 et PostgreSQL de chaque table (un écart est signalé).
Les identités (`fou010d.id`) sont recalées automatiquement.
