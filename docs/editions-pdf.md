# Produire un document PDF (ARC, devis, BL, facture…)

Les éditions sont produites par l'application, en .NET, **sans programme IBM i** (pas de PRTF, d'overlay ni de
QCMDEXC). Premier document fait ainsi : l'**ARC des commandes** (reprise d'ARC105P). L'édition du devis suit le même
schéma.

## Ce qui est commun

| Où | Quoi |
|---|---|
| `Socle/Documents/DocumentsPdf.cs` | Licence et polices QuestPDF, `PageStandard(apercu)` (A4, marges, police, filigrane « APERÇU »), formats français (`Montant`, `Prix`, `Quantite`, `Date`), `Gras(bool)`, couleurs, `Reponse(http, document)` (PDF affiché dans le navigateur), `DocumentGenere(Nom, Contenu)` |
| `Noyau/Societes` : `ISocietes.LireIdentiteAsync(societe, enseigne)` | Société émettrice (AA0007) : raison sociale, adresse, contacts, `MentionsLegales` (forme, capital, SIRET, APE), logo |
| `Directory.Packages.props` | QuestPDF (licence Community : gratuite sous 1 M$ de chiffre d'affaires annuel, sinon licence Professional) |

**Logos.** Les logos de l'IBM i sont des overlays d'impression (PRT003F : PRINTOR, ADEM2…), inutilisables hors du
5250. Ils sont remplacés par des images : dossier `Documents:Logos` (configuration ou user-secrets de NetPrint.Web),
fichiers `{société}-{enseigne}.png`, puis `{société}.png` (`.jpg` accepté). Sans logo, l'en-tête ne montre que le texte.

## Ce que fait chaque module

Exemple de l'ARC (module Ventes) :

1. **Contrat** (`Ventes.Contrats/EditionArc.cs`) : `IEditionArc.EditerAsync(...)` renvoie un `DocumentGenere`.
2. **Données** (`Donnees/ArcCommandeDb2.cs`) : lecture seule, SQL paramétré, tout ce que le document affiche.
3. **Règles** (`Services/ArcCommande.cs`) : fonctions pures qui transforment les données en **modèle** (`ModeleArc`) :
   lignes retenues, regroupements, montants, textes décodés. C'est là que portent les tests (`ArcCommandeTests`).
4. **Mise en page** (`Impressions/DocumentArc.cs`) : une classe `IDocument` QuestPDF qui ne fait qu'afficher le modèle,
   en commençant par `page.PageStandard(apercu)`.
5. **Service** (`Services/EditionArcService.cs`) : droits (PRT000P1 « Édition », sauf aperçu), données, modèle,
   `DocumentsPdf.Generer(nom, new DocumentArc(...))`.
6. **Point d'API** (`VentesModule.MapModuleVentes`) : `GET /api/ventes/commandes/{n}/{clé}/arc?apercu=1` →
   `DocumentsPdf.Reponse(http, doc)`. L'écran ouvre cette adresse dans un onglet ou dans un `<iframe>`.

**Aperçu.** Même document, avec `apercu: true` : filigrane, aucune écriture. Dans la fiche commande, le panneau
« Aperçu ARC » recharge l'`<iframe>` à chaque modification (paramètre `v` incrémenté au rechargement de la fiche).

**Tests.** Les règles (étape 3) se testent sans base ni PDF. Un test `[FactDb2]` produit le PDF d'une commande réelle
dans le dossier temporaire (`EditionArcDb2Tests`) pour le regarder.

## Textes paramétrés des canevas (ARC010F / ARC015F)

Les canevas d'ARC sont des lignes de 74 caractères avec des variables `$CODE` (ou `£CODE` selon le CCSID) et le
repositionnement `§`. `ArcCommande.Decoder` reproduit le décodage d'ARC105P ; les canevas sont imprimés en chasse fixe
pour garder leurs colonnes. Quand un canevas place lui-même l'adresse du client (`$CRS`), il remplace les blocs
« références » et « destinataire » du document.
