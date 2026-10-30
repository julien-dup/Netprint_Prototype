# Migrations PostgreSQL (DbUp)

Chaque modification du schéma PostgreSQL est un script SQL numéroté, jamais modifié une fois appliqué :

```
0001_noyau_schema.sql
0002_ventes_devis.sql
...
```

DbUp applique au démarrage les scripts pas encore passés (table `schemaversions`).

Le pilote Ventes lit d'abord DB2 : aucun script pour l'instant. Le premier sera le schéma du noyau
proposé dans `RetroDoc Netprint pour SAAS\modele-cible\01_noyau_schema.sql` (28 tables), une fois validé.
Le projet d'exécution DbUp sera ajouté à ce moment-là.
