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
| `Socle/Documents/MessageBrouillon.cs` | Message à envoyer par la messagerie de l'utilisateur, **sans serveur SMTP** : fichier `.eml` « non envoyé » (`X-Unsent: 1`) avec destinataires, objet, texte et pièces jointes ; Outlook l'ouvre comme un brouillon prêt à partir |
| `Noyau/Tiers` : `IContactsTiers.LireAsync(..., DocumentEnvoye)` | Contacts du client destinataires d'un document (PRT051F : ARC, offre, BL, facture, PV de contrôle, BAG), « par défaut » en premier |

**Logos.** Ceux des éditions IBM i sont des objets `*OVL`, inutilisables hors du 5250. On reprend les fichiers BMP ou
JPEG des éditions WinDev (répertoire de NetPrint sur les postes), copiés dans le dossier `src/NetPrint.Web/Logos`
(ou celui de la clé `Documents:Logos`), sous leurs noms : mode d'emploi dans `src/NetPrint.Web/Logos/LISEZMOI.md`. Ordre de recherche (`LogosSociete.Noms`, type de
document passé à `LireIdentiteAsync`, « Arc » pour l'ARC) : `<soc>-<enseigne>`, `<soc>` (sans ambiguïté), puis les noms
WinDev `Logo<type><soc><enseigne>`, `Logo<soc><enseigne>`, `Logo<type><soc>`, `Logo<soc>`, `Logo<type>` (`.png`, `.jpg`,
`.jpeg`, `.bmp`). Les noms WinDev n'ont pas de séparateur : `Logo12` peut être la société 12 ou la société 1 enseigne 2 ;
un fichier `12.png` ou `1-2.png` lève le doute. Sans logo, l'en-tête ne montre que le texte.

**Envoi direct (SMTP).** `Noyau/Messagerie` : `IMessagerie.EnvoyerAsync(societe, MessageBrouillon)` envoie par le
serveur de la société, paramètres FAC010 (PSMTPS, PSMTPP, PSMTPU / PSMTPM, PTLS, PEXPEN), comme EnvoideFactures, avec
accusé de lecture. Garde-fou `Messagerie:Mode` (`Desactive` par défaut, `Redirection` vers `Messagerie:Redirection`,
`Reel`) : la base de test contient les vrais serveurs et contacts. Voir le README. Dans les tests, une messagerie
factice garde les messages sans les envoyer.

**Expéditeur et trace.** L'expéditeur est l'utilisateur (`IUtilisateurs.EmailAsync` : AI00F.ACMELE ; plus tard le compte
Entra ID), à défaut la société (PEXPEN). Chaque envoi, et chaque brouillon préparé pour Outlook, est tracé par
`IJournalEnvois` (table ENV100, script `db/db2/20261030-2230_ENV100_journal_envois.sql`) : une ligne par document, avec
le PDF envoyé. Types de document : `TypesEnvoi` (ARC, à compléter : offres…).

**Envoi par e-mail (Outlook).** Comme EnvoiARC et VisuArc (Outlook sur le poste), l'application prépare le message et
l'utilisateur l'envoie : `MessageBrouillon.VersEml(nom)`, téléchargé par l'écran (`JS.TelechargerAsync`, Web.Commun) après avoir traité les erreurs. Outlook classique
l'ouvre en brouillon, pièces jointes comprises. Évolution possible pour le SaaS : créer le brouillon directement dans
la boîte Microsoft 365 de l'utilisateur (Microsoft Graph), sans téléchargement.

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
