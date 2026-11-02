# Compte rendu — Explications sur les données clés du devis (liste et détail)

*Inventaire du 02/11/2026, sans code. Les textes sont des **propositions** que Julien relit et corrige avant développement.*

## 1. Principe retenu (validé le 02/11/2026)

| Niveau | Où | Contenu |
|---|---|---|
| **ⓘ Définition** | icône discrète à côté d'un libellé, d'un titre de colonne ou d'une zone | 1 ou 2 phrases. Dans une grille, la définition est ajoutée en tête de la bulle de filtre existante, et non comme icône dans l'en-tête. |
| **Comment c'est calculé ?** | panneau dépliable sur les montants clés | la formule **avec les chiffres du devis affiché**, produite par le même code que le calcul |
| **Explication d'état** | couleur de ligne, badge, bouton grisé, liste vide | le pourquoi, dans la bulle ou dans un texte d'aide |

Tous les textes iraient dans un **glossaire unique** (terme → titre, définition, formule), lu par un composant commun `<Explication Terme="…" />` dans Web.Commun. Les autres modules (Commandes, Logistique, Facturation…) pourront l'utiliser aussi.

Sources : glossaire métier DEV100 (`Devis2017/doc/architecture_DEV100/glossaire-metier.md`), rétro-doc ConsultationDevis (R1 à R26), catalogue DB2, code du prototype.

Légende des colonnes « Niv. » des tableaux qui suivent : **D** = ⓘ définition, **C** = comment c'est calculé, **É** = explication d'état. ⚠ = texte à confirmer par Julien.

## 2. Liste des devis

### 2.1 Critères de recherche

| Donnée | Niv. | Texte proposé |
|---|---|---|
| Critères principaux | É | « Saisissez au moins un critère principal : n° de devis, étude, client, prospect, profil, catégorie, sous-catégorie, dates, fabricant, représentant, quantités ou n° d'OF. Les autres critères ne font qu'affiner. » Ce texte existe déjà sur la liste vide ; il serait repris en ⓘ à côté de « Rechercher ». |
| Client / Prospect | D | « Un devis est fait soit pour un client (compte général + compte client), soit pour un prospect, c'est-à-dire un client potentiel qui n'a pas encore de compte. Les deux critères ne se combinent pas. » |
| Devis (Tous / En cours / En commande) | D | « En commande : un dossier de fabrication (OF) a été créé à partir de ce devis. En cours : pas encore commandé. » ⚠ un devis en échec reste-t-il « en cours » ? |
| Type devis | D | « Devis client : établi pour un client ou un prospect. Devis type : modèle réutilisable, sans client, qui sert de point de départ pour chiffrer vite un travail courant. » |
| Représentant | D | « Commercial en charge du devis. Si votre profil est limité à un représentant, vous ne voyez que ses devis, même sans saisir ce critère. » |
| Modèle | D | « N° de modèle de devis : un canevas pré-paramétré, rangé dans une bibliothèque de modèles, d'où le devis a été créé. » ⚠ |
| N° OF / N° OF préc. | D | « OF : dossier de fabrication créé quand le devis a été commandé. OF précédent : dossier d'une fabrication antérieure du même travail (réimpression). » ⚠ |
| Étude | D | « N° de l'étude d'imposition (placement des poses sur la feuille) rattachée au devis. » |
| Nb élément/ex | D | ⚠ « Nombre d'éléments par exemplaire » — à préciser : différence avec « Nb éléments » ? |
| Format fini / ouvert | D | « Fini : dimensions du produit livré, après coupe et pliage. Ouvert : dimensions à plat, avant pliage. » |
| Fournisseur STR / Opération STR | D | « STR = sous-traitance : une opération confiée à un fournisseur extérieur (dorure, reliure…), et non faite sur nos machines. » |
| « Seuls les 200 premiers… » | É | déjà affiché ; ajouter « Ajoutez un critère (dates, client…) pour réduire la liste. » |

### 2.2 Colonnes de la grille

| Colonne | Niv. | Texte proposé |
|---|---|---|
| N° devis (n°/v/h) | D | « N° du devis / version / hypothèse. Une **version** retrace une évolution du devis (nouvelle demande du client). Une **hypothèse** chiffre le même devis pour une autre quantité (1 000, 3 000, 5 000 ex.) afin que le client choisisse. » |
| Qté devisée | D | « Nombre d'exemplaires chiffrés pour cette hypothèse. » |
| **PV mini / PV conseillé / PV remis** | D + C | Voir § 2.3. **Point important** : ces prix sont exprimés **dans l'unité de vente du devis** (pour 1, pour 1000, le tout…), et la grille ne la montre pas aujourd'hui. Je propose de l'afficher dans la bulle (« PV remis : 45,00 € pour 1000 ») ou dans une colonne « Unité ». |
| Date prix | D | « Date à laquelle les prix du devis ont été calculés. Au-delà, les tarifs papier et les taux machines ont pu changer. » ⚠ |
| **Valeur devis** | C | « Montant total du devis pour la quantité devisée = PV remis × quantité ÷ unité de vente. Exemple : 45,00 € pour 1000 × 12 000 ex. ÷ 1000 = 540,00 €. Pour un prix « le tout » : valeur = PV remis. Vide tant qu'aucun prix remis n'est saisi. » (règle R7b, code existant) |
| Docs | D | « Nombre de documents joints au n° de devis (toutes versions). Double-clic pour les ouvrir. » |
| Échec / Date échec | D | « Le devis a été perdu : motif et date de clôture saisis avec ✖ Échec. L'échec vaut pour toutes les versions du n° de devis. » |
| Norme | D | ⚠ définition à fournir. |
| Réf. imprimé | D | ⚠ « Référence de l'imprimé chez le client » ? |
| Demandé le | D | « Date de la demande de prix du client. » ⚠ |
| Nb élt | D | « Nombre d'éléments du produit (couverture, intérieur, rabat…), chiffrés séparément. » |

### 2.3 Les trois prix de vente (à valider)

| Prix | Texte proposé | Question |
|---|---|---|
| PV mini | « Prix plancher : en dessous, le devis ne couvre plus ses coûts. » | ⚠ Comment DEV100 le calcule-t-il ? PR seul ? PR × coefficient mini ? |
| PV conseillé | « Prix proposé par le calcul : prix de revient × coefficient de vente de la société. » | ⚠ Quel coefficient : D50009, AA000Z ? |
| PV remis | « Prix réellement proposé au client, saisi par le commercial (ou dans la décomposition). C'est lui qui fait foi pour la valeur du devis et pour l'offre. » | — |

Pour ces trois prix, le « comment c'est calculé ? » ne pourra être fait qu'une fois le moteur DEV100 repris. D'ici là, je propose seulement la définition.

### 2.4 États et couleurs

| Élément | Texte proposé |
|---|---|
| Ligne bleue | « Une offre a été éditée pour ce devis. » |
| Ligne verte | « Devis en commande : un OF a été créé. » |
| Ligne rouge | « Devis en échec (perdu). » |
| Légende | une petite légende des trois couleurs sous la grille, comme celle de GestionOutils (`legende-articles`) |
| Boutons grisés | « Sélectionnez un devis dans la liste. » ; pour ceux pas encore repris : « Disponible dans une prochaine étape. » |

## 3. Détail d'un devis

### 3.1 En-tête et présentations

| Donnée | Niv. | Texte proposé |
|---|---|---|
| Par atelier / Par élément | D | « Par atelier : toutes les opérations regroupées par atelier (prépresse, impression, façonnage…), puis papier, matières, sous-traitance. Par élément : chaque partie du produit avec sa fabrication, son papier et son outil. » |
| Devis type (badge) | É | « Modèle réutilisable, sans client. » |
| 31 Prépresse / 32 Façonnage | D | « Opérations qui concernent tout le produit et pas un élément précis (préparation des fichiers, finition commune). » ⚠ |

### 3.2 Opérations (onglets ateliers, fabrication)

| Colonne | Niv. | Texte proposé |
|---|---|---|
| Section / opération | D | « Section : la machine ou le poste de travail. Opération : le travail fait dessus (calage, roulage, pliage…). » |
| Base, Nombre, Temps unit., Cadence, Quantité, Diviseur | D | « Paramètres de la règle de calcul de l'opération : nombre (feuilles, tours…), temps unitaire en heures, cadence (unités par heure), quantité, diviseur (par exemple feuilles par tour). La règle de calcul dit lesquels servent. » ⚠ « Base » à définir. |
| Résultat | C | « Temps machine de l'opération, en heures. » ⚠ La formule dépend de la règle (calage = nombre × temps unitaire ; roulage = quantité ÷ diviseur ÷ cadence ?) : à confirmer avant d'afficher un détail chiffré. |
| Taux horaire | D | « Coût d'une heure : part machine + part main-d'œuvre. » |
| **Valeur** | C | « Coût de l'opération = résultat (h) × taux horaire. Exemple : 2,50 h × 85,00 €/h = 212,50 €. » ⚠ à vérifier sur quelques lignes de D60002 (DDVALO = DDRSQT × (DDPRMA + DDPRMO) ?). |
| **Valeur mille +** | D + C | « Coût de 1 000 exemplaires de plus, hors frais fixes (calage) : ce que coûte chaque mille supplémentaire. Opération : temps du mille supplémentaire × taux horaire. » ⚠ à confirmer. |
| Sous-total (ligne bleue) | É | « Total de la section quand elle compte plusieurs lignes. » |

### 3.3 Papier et support

| Donnée | Niv. | Texte proposé |
|---|---|---|
| Provenance | D | « Fabrication : papier fabriqué / Fournisseur : acheté pour ce travail / Stock : pris en stock, sans majoration de frais généraux / Client : fourni par le client, non valorisé. » ⚠ « Fabrication » à préciser. |
| Format papier / tirage / appro | D | « Papier : format de la feuille ou laize de la bobine achetée. Tirage : format passé en machine. Appro : format approvisionné. » ⚠ |
| Poses | D | « Nombre d'exemplaires imprimés sur une même feuille ou un même tour. » |
| **Gâche fixe / % gâche / Total gâche** | C | « Papier perdu au réglage et pendant le tirage. Total gâche = gâche fixe (calage) + % gâche × bonnes feuilles. Total = bonnes feuilles + total gâche. » Exemple chiffré avec les valeurs du devis. |
| Bonnes feuilles / tours bons | C | « Feuilles (ou tours) nécessaires pour la quantité devisée = quantité ÷ poses. » ⚠ |
| Métrage (bobine) | D | « Longueur de papier consommée, en mètres linéaires. » |
| **Valeur papier** | C | « Poids (ou quantité dans l'unité d'achat) × prix d'achat. Pour une bobine, la valeur tient compte des bobines entamées (voir Bobines). » |
| Bobines | C | « Nombre de bobines nécessaires = tours ÷ tours par bobine. La longueur utile d'une bobine est son conditionnement moins la perte de fin de bobine. » (R14, `CalculBobine`) |
| UA / Prix / Conditionnement | D | « UA : unité d'achat (kg, feuille, mille…). Conditionnement : quantité par unité d'achat (rame, bobine). » |

### 3.4 Matières, sous-traitance, transport, frais

| Donnée | Niv. | Texte proposé |
|---|---|---|
| Matière (999999) | D | « Matière non codée : saisie librement, sans fiche matière. » |
| Prix fixe / Prix prop. / Qté prop. | C | « Sous-traitance : prix fixe (forfait) + prix proportionnel × quantité ÷ quantité de référence. » ⚠ |
| Transport — Barème | D | ⚠ « Barème du transporteur appliqué selon le département et le poids. » |
| Frais annexes — Marge | C | « Prix de vente = prix de revient + marge. » ⚠ |

## 4. Fenêtre « Saisie » : une correction à faire

D'après le glossaire DEV100, le **Renouvellement** est la création d'un **nouveau devis à partir d'un devis déjà passé en commande** (ou d'un modèle), pour une réimpression ou un client fidèle. La mise à jour aux tarifs du jour s'appelle **Réactualisation**, et c'est une autre notion.

Le texte provisoire de la carte « Renouveler » est donc inexact. Je propose : « Créer un nouveau devis à partir de ce devis déjà commandé, pour une réimpression : il reprend sa fabrication, avec un nouveau n°. » La carte serait grisée si le devis n'est pas en commande, à confirmer lors de l'analyse de FEN_Modification.

## 5. Questions pour Julien

1. **PV mini et PV conseillé** : comment sont-ils calculés (§ 2.3) ?
2. **Unité de vente** dans la liste : l'afficher dans la bulle des prix, ou dans une colonne « Unité » ?
3. **Valeur et mille +** des opérations : les formules du § 3.2 sont-elles justes ?
4. **Définitions à fournir** : Norme, Base, Nb élément/ex, Réf. imprimé, provenance « Fabrication », formats tirage / appro.
5. **Ordre de réalisation** : je propose de commencer par la liste (les ⓘ simples, la Valeur devis calculée, la légende des couleurs), puis le détail.

## 6. Suite

Après ta relecture :

1. glossaire + composant `<Explication>` (Web.Commun) ;
2. liste ;
3. détail ;
4. correction de la carte Renouveler.

Je ferai un compte rendu à chaque livraison, comme pour les lots précédents.

## 7. Réponses de Julien (02/11/2026)

1. **PV mini et PV conseillé** = prix de revient × un coefficient propre à chaque société. D'après la rétro-doc, ces coefficients sont dans les paramètres de valorisation **AA000Z (taux CKMA)**. La formule exacte de DEV100 ajoute les majorations : PV = base 100 ÷ (1 ÷ (1 + taux ÷ 100) − Σ majorations ÷ 100). Le détail chiffré viendra avec le moteur de calcul ; en attendant, la bulle donne la définition simple.
2. **Unité de vente** : affichée dans la **bulle** des prix.
3. **Valeur et mille +** des opérations : formules confirmées.
4. **Définitions données** :
   - **Base** : nombre d'opérations, ou de feuilles s'il s'agit d'un roulage ou d'une bobine ;
   - **Format de tirage** : le format dans lequel on imprime ;
   - **Format d'appro** : le format dans lequel on s'approvisionne (par exemple, on achète des feuilles plus grandes que l'on découpe avant impression).
5. **Fenêtre Saisie** : on corrige « Renouveler » et on ajoute une carte **« Réactualiser »**.

Restent sans texte, en attendant une définition : Norme, Nb élément/ex, Réf. imprimé, provenance « Fabrication ».

## 8. Livraison 1 (02/11/2026, à compiler) : composant commun + liste des devis

**Commun (Web.Commun)**
- `Glossaire.cs` : `Glossaire`, `TermeGlossaire` et `ISourceGlossaire`. Chaque module fournit ses termes. Le service est enregistré dans `AddInterfaceNetPrint`.
- `Explication.razor` : icône ⓘ et bulle (titre, définition, contenu « calcul » facultatif). Elle s'ouvre au survol ; un clic, Entrée ou Espace la garde ouverte ; Échap la ferme. Placée dans un `<label>`, elle ne lui vole pas le clic.
- `EnteteColonne.razor` : nouveau paramètre facultatif **`Explication`**. La définition s'affiche au survol du titre de colonne et en tête de la bulle de filtre. Les autres grilles (Commandes…) ne changent pas tant qu'elles ne le renseignent pas.

**Devis**
- `Ecrans/Devis/GlossaireDevis.cs` : tous les textes du devis (critères, colonnes, états).
- **Liste** :
  - ⓘ sur Client, Représentant, Devis, Type devis, Rechercher, Formats, Étude, Modèle, N° OF et Fournisseur STR ;
  - définition au survol de chaque titre de colonne ;
  - **bulle des PV** : montant + unité de vente (« 45,00 € pour 1 000 ») puis l'observation de prix ;
  - **bulle de la valeur du devis** : le calcul avec les chiffres de la ligne ;
  - bulles sur la légende des couleurs.
- `LigneListeDevis` : nouveau champ `Tnum`, l'unité de vente, avec une valeur par défaut. `Affichage.UniteVente(tnum)` donne le texte : « pour 1 000 », « à l'unité » ou « pour le tout ».
- **Fenêtre Saisie** : texte de Renouveler corrigé, et 5e carte **Réactualiser**.

**Suite** : le détail du devis (en-tête, opérations, papier et support, matières, sous-traitance).
