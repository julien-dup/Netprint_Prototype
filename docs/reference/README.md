# Référentiels extraits de l'AS400 de test

Extraits le 30/10/2026 (lecture seule) pour reprendre dans le SaaS les mêmes libellés que le 5250.

| Fichier | Source | Contenu |
|---|---|---|
| `PRTMSG.csv` | fichier de messages `NP11P/PRTMSG` (vue `QSYS2.MESSAGE_FILE_DATA`) | 1 220 messages : identifiant, texte, aide (second niveau). Les RPG y font référence par identifiant (GEN0001, CPT0018, NEG0029…) |
| `DDVLLSP.csv` | table `NP11F/DDVLLSP` (listes fixes, gérées par LST010S) | 112 listes : une ligne `CNDTYP = 'LST'` par liste (n° dans `ààCND`, nom dans `EXTVAL`), puis ses valeurs `CNDTYP = 'VAL'` (même n°, valeur saisie `EXTVAL`, valeur stockée `INTVAL`, libellé `CNDTXT`) |

Séparateur `;` (les `;` des textes sont remplacés par des virgules), UTF-8.
À réextraire si les messages ou les listes changent sur l'IBM i.
