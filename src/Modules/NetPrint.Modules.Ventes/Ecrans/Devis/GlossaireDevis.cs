using NetPrint.Web.Commun;

namespace NetPrint.Modules.Ventes.Ecrans.Devis;

// =====================================================================
// Glossaire des écrans du devis (bulles ⓘ, titres de colonnes, légende).
// Textes : compte rendu docs/compte-rendu-explications.md, réponses de Julien du 02/11/2026, glossaire métier DEV100
// (Devis2017/doc/architecture_DEV100/glossaire-metier.md). Les notions encore à définir par le métier (norme,
// réf. imprimé, nb élément/ex…) n'ont volontairement pas de texte.
// Colonnes de la liste : clé « devis.colonne.<clé de la colonne> ».
// =====================================================================
internal sealed class GlossaireDevis : ISourceGlossaire
{
    public IEnumerable<TermeGlossaire> Termes { get; } =
    [
        // ---------- Critères de recherche ----------
        new("devis.criteres", "Critères principaux",
            "Saisissez au moins un critère principal : n° de devis, étude, client, prospect, profil, catégorie, sous-catégorie, " +
            "dates, fabricant, représentant, quantités ou n° d'OF. Les autres critères ne font qu'affiner la recherche."),
        new("devis.client-prospect", "Client ou prospect",
            "Un devis est fait soit pour un client (compte général + compte client), soit pour un prospect : un client " +
            "potentiel qui n'a pas encore de compte. Les deux critères ne se combinent pas."),
        new("devis.etat", "Devis en cours / en commande",
            "En commande : un dossier de fabrication (OF) a été créé à partir de ce devis. En cours : il n'a pas encore été commandé."),
        new("devis.type", "Devis client / devis type",
            "Devis client : établi pour un client ou un prospect. Devis type : modèle réutilisable, sans client, qui sert de " +
            "point de départ pour chiffrer rapidement un travail courant."),
        new("devis.representant", "Représentant",
            "Commercial en charge du devis. Si votre profil est limité à un représentant, vous ne voyez que ses devis, " +
            "même sans saisir ce critère."),
        new("devis.formats", "Format fini / format ouvert",
            "Fini : dimensions du produit livré, après coupe et pliage. Ouvert : dimensions à plat, avant pliage."),
        new("devis.sous-traitance", "Sous-traitance (STR)",
            "Opération confiée à un fournisseur extérieur (dorure, reliure…) au lieu d'être faite sur nos machines. " +
            "Elle est identifiée par le couple fournisseur + opération."),
        new("devis.etude", "Étude d'imposition",
            "N° de l'étude d'imposition rattachée au devis : le placement des poses sur la feuille ou la bobine."),
        new("devis.modele", "Modèle",
            "N° du modèle de devis : un canevas pré-paramétré, rangé dans une bibliothèque de modèles, qui permet de créer " +
            "rapidement un nouveau devis."),
        new("devis.of", "OF / OF précédent",
            "OF : dossier de fabrication créé quand le devis a été commandé. OF précédent : dossier d'une fabrication " +
            "antérieure du même travail (réimpression)."),
        new("devis.formats-papier", "Format papier / tirage / appro",
            "Tirage : format dans lequel on imprime. Appro : format dans lequel on s'approvisionne. Par exemple, on achète " +
            "des feuilles plus grandes que l'on découpe avant impression."),

        // ---------- Colonnes de la liste ----------
        new("devis.colonne.client", "Client / prospect",
            "Compte général et compte client, ou n° de prospect (client potentiel sans compte)."),
        new("devis.colonne.devis", "N° devis / version / hypothèse",
            "Une version retrace une évolution du devis (nouvelle demande du client). Une hypothèse chiffre le même devis " +
            "pour une autre quantité (1 000, 3 000, 5 000 ex.) afin que le client choisisse."),
        new("devis.colonne.qte", "Quantité devisée", "Nombre d'exemplaires chiffrés pour cette hypothèse."),
        new("devis.colonne.fini", "Format fini", "Dimensions du produit livré, après coupe et pliage."),
        new("devis.colonne.ouvert", "Format ouvert", "Dimensions à plat, avant pliage."),
        new("devis.colonne.nbelt", "Nombre d'éléments",
            "Parties du produit chiffrées séparément : couverture, intérieur, rabat…"),
        new("devis.colonne.representant", "Représentant", "Commercial en charge du devis."),
        new("devis.colonne.categorie", "Catégorie", "Premier niveau de classement du travail d'imprimerie."),
        new("devis.colonne.souscategorie", "Sous-catégorie", "Deuxième niveau de classement, à l'intérieur de la catégorie."),
        // PV mini / conseillé : coefficients de la société (paramètres de valorisation AA000Z, taux CKMA)
        new("devis.colonne.pvmini", "Prix de vente mini",
            "Prix de revient multiplié par le coefficient « PV mini » de la société : le prix plancher du devis. " +
            "Exprimé dans l'unité de vente du devis (voir la bulle de la cellule)."),
        new("devis.colonne.pvconseille", "Prix de vente conseillé",
            "Prix de revient multiplié par le coefficient « PV conseillé » de la société. " +
            "Exprimé dans l'unité de vente du devis (voir la bulle de la cellule)."),
        new("devis.colonne.pvremis", "Prix de vente remis",
            "Prix réellement proposé au client, saisi par le commercial (ou dans la décomposition). C'est lui qui fait foi " +
            "pour la valeur du devis. Exprimé dans l'unité de vente du devis (voir la bulle de la cellule)."),
        new("devis.colonne.dateprix", "Date prix",
            "Date à laquelle les prix du devis ont été calculés. Depuis, les tarifs papier et les taux machines ont pu changer."),
        new("devis.colonne.modele", "Modèle", "N° du modèle de devis (canevas pré-paramétré) d'où le devis a été créé."),
        new("devis.colonne.of", "OF", "Dossier de fabrication créé quand le devis a été commandé."),
        new("devis.colonne.ofprec", "OF précédent", "Dossier d'une fabrication antérieure du même travail (réimpression)."),
        new("devis.colonne.etude", "N° étude", "Étude d'imposition : le placement des poses sur la feuille ou la bobine."),
        new("devis.colonne.dateechec", "Date d'échec", "Date de clôture du devis perdu, saisie avec ✖ Échec."),
        new("devis.colonne.echec", "Échec",
            "Motif de perte du devis. L'échec vaut pour toutes les versions du n° de devis."),
        new("devis.colonne.docs", "Documents",
            "Nombre de documents joints au n° de devis (toutes versions). Double-clic sur la cellule pour les ouvrir."),
        new("devis.colonne.valeur", "Valeur du devis",
            "Montant total pour la quantité devisée = PV remis × quantité ÷ unité de vente (prix « le tout » : PV remis). " +
            "Vide tant qu'aucun prix remis n'est saisi. Le détail du calcul est dans la bulle de la cellule."),
        new("devis.colonne.datedemande", "Demandé le", "Date de la demande de prix du client."),

        // ---------- États (couleurs, badges) ----------
        new("devis.etat.offre", "Offre", "Une offre a été éditée pour ce devis (ligne en bleu)."),
        new("devis.etat.commande", "En commande", "Un dossier de fabrication (OF) a été créé à partir de ce devis (ligne en vert)."),
        new("devis.etat.echec", "Échec", "Le devis a été perdu : motif et date de clôture saisis (ligne en rouge)."),
    ];
}
