# Logos des éditions (ARC, devis, BL, factures…)

Les documents PDF produits par l'application (ARC des commandes, puis devis…) prennent le logo de la société dans
**ce dossier** (`src/NetPrint.Web/Logos`, à côté de l'application ; sur un serveur, le dossier `Logos` du répertoire
de publication). Un autre dossier peut être indiqué par la clé de configuration `Documents:Logos`
(`appsettings.json` ou user-secrets de NetPrint.Web).

## Quels fichiers

Les logos des éditions IBM i sont des objets `*OVL`, inutilisables ici. On reprend les fichiers **BMP ou JPEG des
éditions WinDev**, présents dans le répertoire de l'application NetPrint de chaque poste : les **copier ici sans les
renommer**. Formats acceptés : `.png`, `.jpg`, `.jpeg`, `.bmp`.

Ordre de recherche (société 8, enseigne 1, document ARC) :

1. `8-1` puis `8` : noms sans ambiguïté, à utiliser pour un nouveau logo ;
2. noms WinDev : `LogoArc81`, `Logo81`, `LogoArc8`, `Logo8`, `LogoArc`.

Les noms WinDev n'ont pas de séparateur : `Logo12` peut être la société 12 ou la société 1 enseigne 2. En cas de doute,
renommer en `12.png` ou `1-2.png`.

Sans logo, l'en-tête du document ne montre que le texte (raison sociale, adresse).

## Versionnement

Les images de ce dossier ne sont pas versionnées (logos des clients) : chaque poste et chaque serveur a les siens.
La prise en compte est immédiate : pas besoin de redémarrer l'application.
