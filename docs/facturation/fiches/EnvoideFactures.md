# EnvoideFactures — rétro-documentation (préparation SaaS)

> Projet WinDev 25 `D:\Mes Projets\EnvoideFactures` (fenêtres en texte, état au 06/10/2026).
> Complète, sans les refaire : `D:\docs\Netprint\Envoi-factures-FacturX\` (specs et plans Factur-X, SuperPDP OAuth,
> affacturage, correctif port/escompte) et `D:\docs\Netprint\Facture-electronique\` (FactureElectronique = **suivi**
> des factures émises et reçues ; l'émission reste ici).
> Extractions du code : `EF_*.txt` dans ce dossier (numéros de ligne cités = lignes de `EF_FEN_Envoifactures.txt`).

## 1. Rôle

Envoyer aux clients les factures **éditées** (FAC080.FDEDT = 'O') et **pas encore envoyées** (FDTAGE <> 'O'),
selon les paramètres d'envoi du client (table **PRT057F**) :

- **canal** `PRT057F.PFFTP` : `N` (ou vide) = **mail**, `O` = **FTP**, `X` = **webservice / PDP** ;
- **format** `PRT057F.PFFMT` : `PDF`/`PDF1` (PDF de la facture), `FACX` (Factur-X PDF/A-3 + XML CII EN16931),
  `XML1` (XML maison), `CSV1`, `CSV2`, `TXT1` (fichier plat), `EDI1`, `EDI2` (EDIFACT INVOIC), `EDIP` (EDIFACT + PDF) ;
- options : `PFOPT1 = 'O'` un envoi par facture (sinon un envoi groupé par client), `PFOPT2 = 'O'` joindre les PDF
  des **BL** de la facture, `PFREFIM` ignorer la réf. imprimée (EDI), `PFOBJM` objet du mail (variables
  `&NOFAC`, `&NOM`, `&NBFAC`), `PFDEST1..5` destinataires de l'envoi **automatique**, `PFADD/PFUSER/PFMDP/PFPORT`
  serveur FTP, `PMAILR` adresse d'annuaire PPF du client (Factur-X, schemeID 0225), `PFAUTO = 'O'` client inclus
  dans l'envoi automatique.

Pas de **relance** (aucune trace de relance/dunning dans le projet). Pas de signature électronique.

## 2. Fenêtres et traitements

| Élément | Rôle |
|---|---|
| `FEN_Envoifactures` | Fenêtre unique, 2 onglets. Titre « Envoi de factures par mail (Société n …) ». |
| Onglet 1 « manuel » | Période + client (compte général/aux. via composant `RechercheClient`) → `BTN_Chargement` → `chargetable()` → table des factures (case « Envoi », « Pour tous ») → `BTN_Suivant` (envoi). |
| Onglet 2 « automatique » | Période, plage de n° facture, client facultatif → `InitEnvoiAuto()` (une ligne par client avec nb de factures et canal) → `BTN_Envoyer` (boucle complète, sans intervention). Statut OK/KO par client ; clic sur statut → `FEN_Statut` (messages), clic sur nb → `FEN_Detail` (factures et date d'envoi). Onglet grisé si droit `PRT000P1 APGM='PCFACAUTO'` AOPT01 = `**` (groupe `AI00F.AGROUP` puis profil). |
| `FEN_Adrmail` | Choix des contacts destinataires en manuel (PRT051F, voir § 4). |
| `FEN_EnvoiFTP` | Fenêtre de progression FTP (manuel). |
| `FEN_ValeursParDefaut` | Onglet ouvert par défaut : `EnvoiFactures.Ini [GENERAL] TypeEnvoiDefaut = M|A`. |
| `FEN_Paramétrage` | (composant `Connexion`) changement de société → `InitVarglobale()`. |
| `Fen_OuvreOutlook` | Composant `ComposantsExternes\Fen_OuvreOutlook.wdw` : **brouillon Outlook (COM, `Display()`)**, jamais d'envoi automatique. |
| `FEN_EnvoifacturesXML.wdw` | Fichier binaire **hors projet** (absent du .WDP), mars 2026 : orphelin. |
| `ProcéduresGlobales.wdg` | `MAJFAC080`, `MAJFAC080_PDP_ID`, `TransfertToB2Brouter`, `PDP_RepTemp`. |
| Composants | `Connexion` (connexion DB2, `COL_ProcéduresGlobales.CodeSoc / BibPgm / CpteGen / gsProfil`), `RechercheClient`, `OuvreOutlook`, `Cl_Mapi32` (MAPI, plus utilisé : code commenté). |

Procédures de génération de fichiers (dans la fenêtre) : `fichierxml` (XML1), `FichierCSV1`, `FichierCSV2`,
`FichierTXT1`, `FichierEDI1`, `FichierEDI2` (+ `FichierEDI1SVGD` mort), `CreePDF`, `CreePDFBL`, `FacturX_*`
(génération XML, voir docs existantes), `TransfertSuperPdp`, `SuperPDP_Auth/Refresh/LitCacheToken`, `PDP_Log`,
`SuperPDP_Diag*` (diagnostic, écrit `diag_superpdp.txt` et l'ouvre).

## 3. Sélection des factures (requêtes exactes, concaténées dans le code)

**Manuel — paramètres du client** (`chargetable`, l. 2151) :
```sql
select pfftp, pfadd, pfuser, pfmdp, pffmt, cmodir, pfport, ctiers, cparte, pfrefim,
       pfdest1, pfdest2, pfdest3, pfdest4, pfdest5, pfobjm, pfopt2
from prt057f a join aa0003 b on a.rsoc = b.rsoc and a.rgen = b.rgen and a.rcli = b.rcli
where a.rsoc = <soc> and a.rgen = <gen> and a.rcli = <cli>
```
→ préselectionne le canal (radio `Sél_envoi` 1 FTP / 2 mail / 3 PDP) et le format (combo : `PDF` + `PFFMT`).

**Manuel — factures** (l. 2200) :
```sql
select rnfac, fdtype, fdanne, fdmois, fdjour, fdclid, fdclif,
       b.cnom as libclientdo, c.cnom as libclifac, c.cmodir as motdir
from fac080 a
join aa0003 b on a.rsoc = b.rsoc and a.fdgend = b.rgen and a.fdclid = b.rcli   -- client donneur d'ordre
join aa0003 c on a.rsoc = c.rsoc and a.fdgenf = c.rgen and a.fdclif = c.rcli   -- client facturé
where a.rsoc = <soc>
  and ((fdanne * 10000) + (fdmois * 100) + fdjour) between <datedeb> and <datefin>
  and fdgenf = <gen> and fdclif = <cli>
  and fdedt = 'O' and fdtage <> 'O' and fdnone = '' and fdedit <> 'S'
```
Si `PFFMT` ∈ {vide, PDF, PDF1, EDIP} le PDF est **généré dès le chargement** (FACEMANAV, § 5) pour afficher sa taille.

**Automatique — liste** (`InitEnvoiAuto`, l. 5654) et **envoi** (`BTN_Envoyer`, l. 939) :
```sql
select fdgenf, fdclif, cnom, pfftp, pffmt, rnfac, fdanne, fdmois, fdjour, cmodir,
       pfdest1, pfdest2, pfdest3, pfdest4, pfdest5, fdtype, pfadd, pfuser, pfmdp, pfport, pfrefim,
       pfopt1, pfobjm, pfopt2
from fac080 a
join prt057f b on a.rsoc = b.rsoc and a.FDGENF = b.rgen and a.FDCLiF = b.rcli and pfauto = 'O'
left outer join aa0003 c on a.rsoc = c.rsoc and a.fdgenf = c.rgen and a.fdclif = c.rcli
where a.rsoc = <soc>
  [and ((fdanne*10000)+(fdmois*100)+fdjour) between <deb> and <fin>]   -- si dates saisies
  [and rnfac between <n1> and <n2>]                                    -- si n° saisi
  [and fdgenf = <gen> and fdclif = <cli>]                              -- si client saisi
  and fdedt = 'O' and fdtage <> 'O' and fdnone = '' and fdedit <> 'S'
order by fdgenf, fdclif, fdanne, fdmois, fdjour, rnfac
```
Règle : **éditée** (`FDEDT='O'`), **non envoyée** (`FDTAGE<>'O'`), **pas exclue de l'envoi auto**
(`FDNONE=''`, libellé DDL « NE PAS ENVOYER EN AUTO » — appliqué aussi en manuel), **pas `FDEDIT='S'`**.
Groupement : par client (FDGENF, FDCLIF) ; si `PFOPT1='O'` un envoi par facture.

## 4. Destinataires

**Manuel** (BTN_Suivant, l. 625) :
1. si un des `PRT057F.PFDEST1..5` est renseigné → ces adresses (séparées par `; `) ;
2. sinon contacts du client **PRT051F** (contacts clients, partagée avec ARC/BL/offres/BAG) :
   ```sql
   select cnemai, cnfac, cnfacd from prt051f
   where rsoc = <soc> and rgen = <gen> and rcli = <cli> and (cnfacd = 'O' or cnfac = 'O')
   ```
   `CNFAC` = « envoi de facture » (contact proposé), `CNFACD` = « défaut envoi facture » (coché d'office).
   Plus d'un contact, ou contact non défaut → `FEN_Adrmail` (même requête + `cntag <> '*'`, nom = CNCIV + CNNOM).
   ⚠ Avec un seul contact `CNFACD='O'`, l'adresse sert deux fois (variable `cnemai` + `adresmail`) — doublon sans effet.

**Automatique** : uniquement `PRT057F.PFDEST1..5` ; aucun → message « Un email destinataire doit être renseigné
dans les paramètres transferts du client » (mais l'envoi est quand même tenté).

**Expéditeur automatique** : `FAC010.PEXPEN` (société). Manuel : le compte Outlook de l'utilisateur.

**Factur-X** : adresse de routage acheteur `PRT057F.PMAILR` (repli SIREN+SIRET client), e-mail contact acheteur
`AA0003.CEMAIL`, vendeur `FAC010.PFXEMAIL` (repli SIREN société) et `AA0007.SEMAIL`.

## 5. Génération du PDF (AS400, fichier existant sur l'IFS)

Le PDF **n'est pas produit par WinDev** : il est demandé à l'IBM i puis relu sur le partage IFS.

- Appel : `call QSYS2.QCMDEXC('call <BibPgm>/facemanav parm(''SSSSNNNNNNAAAAMMJJ'')', long)`
  (société 4, n° facture 6, année 4, mois 2, jour 2).
- `FACEMANAV` (CL) → selon version OS `FACEMANA1` (V5, CVTSPLFDPF) ou **`FACEMANA2`** (V6+) :
  copie TFAC080/090/100 en QTEMP, `FACNAMEPGM` calcule répertoire et nom de fichier, `MKDIR`, `RMVLNK` de l'ancien
  fichier, `FAC199` compose la facture, `OVRPRTF FILE(FAC202P5) ... TOSTMF(&FILENAME) WSCST(*PDF)` puis `FAC202P5`
  imprime directement en PDF sur l'IFS. Les erreurs sont avalées (`MONMSG CPF0000`).
- Chemin attendu : `\\<Host>\home\factures_PDF\<AA0003.CMODIR nettoyé>\<AA0007.SNOMC>_<AAAA>_<NNNNNN>.PDF`
  (`Host` = `NetPrint.Ini [GUISys TN5250] Host` ; CMODIR = mot directeur client, caractères `|:\/*?"<>'` → `_`).
- Le WinDev **attend en boucle** `TANTQUE PAS fFichierExiste(...) Temporisation(250)` — **sans délai maximum**.
- Format `EDIP` : PDF renommé `\\Host\home\factures_PDF\AANNNNNN.PDF` + fichier `AANNNNNN.edi` à côté.
- **BL joints** (`PFOPT2='O'`, `CreePDFBL`) :
  ```sql
  select distinct a.hnobl, cmodir from exp010 a
  join exp009 b on a.rsoc = b.rsoc and a.hnobl = b.hnobl
  join aa0003 c on b.rsoc = c.rsoc and b.hgene = c.rgen and b.hclie = c.rcli
  where a.rsoc = <soc> and hnofac = <fac> and hdafaa = <an> and hdafam = <mois> and hdafaj = <jour>
  ```
  puis `<BibPgm>/expemanav parm('SSSSNNNNNNNN')` → `\\Host\home\bl_PDF\<CMODIR>\<SNOMC>_BL_<NNNNNN>.PDF`.

## 6. Factur-X

Détails XML : voir `Envoi-factures-FacturX\superpowers\specs\*` (factorisation `FacturX_GetXml`, lignes enrichies,
texte de ligne, acheteur étranger, affacturage). Complément sur l'état **actuel** du code :

- **Profil** : EN16931 (`GuidelineSpecifiedDocumentContextParameter = urn:cen.eu:en16931:2017`), syntaxe **CII** ;
  métadonnées XMP `fx:ConformanceLevel=EN16931`, `DocumentType=INVOICE` (même pour un avoir), `Version 1.0`.
- **PDF/A-3** : le PDF IFS est rouvert (`PDFOuvre`) et réimprimé par le moteur d'impression WinDev :
  `iParamètrePDF(iPDFA3b)`, `iDéfinitXMPSupplémentaire(FacturX_GetMetadata())`,
  `iAjoutePièceJointe(<temp>\EnvoiFacture\factur-x.xml, "factur-x.xml", text/xml)`, `iImprimePDF` →
  **le PDF Factur-X écrase le PDF d'origine sur l'IFS** (même chemin).
- Le XML est écrit dans `%TEMP%\EnvoiFacture\factur-x.xml` (UTF-8), **fichier unique réutilisé** par chaque facture.
- BL (`PFOPT2`) : embarqués **dans le XML** en base64 (`AdditionalReferencedDocument`, TypeCode 916) et,
  pour `CreationFactureXAvecBL` (procédure non appelée), aussi en pièces jointes PDF.
- Le correctif « port et escompte » du 31/07 (doc marquée « pas encore appliqué ») **est en place, et étendu** :
  `rBaseTva`/`rTaxBasisTotal`, port taxable **et port exonéré** en `SpecifiedTradeAllowanceCharge` (BG-21),
  escompte en remise (BG-20, taux BT-94), et **ventilation TVA multi-taux** (`FacturX_ChargeVentilationTVA`,
  regroupement par `FAC020.LTVAFX` / `FAC025.CTTVA2` / `FAC020.LVATEX`). La doc du 31/07 est donc périmée sur ce point.
- Tables lues pour le XML : FAC080, FAC100, FAC090, FAC020, FAC025, FAC120 (textes E1/E2/ML/M2), AA0003, AA0007,
  AA00081 (PY → code ISO pays, RG → `TTYPRG` → code UNTDID 4461), AA00083 (unités), PRT057F, FAC010 (`PPREFX`
  préfixe année du n° facture), PRT100F (IBAN/BIC/affacturage), AB001, NEG100, SPF010, SPF022, EXP009 (livraison).
- Mode de règlement : CH→20, VI→30, PR→49, BO→60, ZZ→42, TA→70, sinon 1.

## 7. Envoi

### 7.1 Mail — manuel : Outlook (COM)
`Fen_OuvreOutlook` / `OuvreOutlookOle` : `Outlook.Application` → `CreateItem(0)`, To, CC, **Body texte**
(le corps RTF est converti en texte), pièces jointes, **`Display()`** : l'utilisateur envoie lui-même.
Objet/corps : modèle RTF `<DOS004.PMDCHEM>\MailFacturesAuto.rtf`, balises `<<OBJET>>`, `<<CONTENU>>`, `<<FIN>>`.
Formats non PDF : un brouillon par fichier (XML1 : un par facture).

### 7.2 Mail — automatique : SMTP WinDev (`emailSessionSMTP`)
```sql
select pexpen, psmtps, psmtpp, psmtpu, psmtpm from fac010 where rsoc = <soc>
```
+ `FAC010.PTLS = 'O'` → `emailOptionSécuriséTLS`. Expéditeur `PEXPEN`, objet `PFOBJM` (variables) sinon `<<OBJET>>`
du modèle sinon « Factures du client <CNOM> » (CR/LF/TAB retirés), corps HTML = `RTFVersHTML` du modèle,
**accusé de réception demandé**, destinataires `PFDEST1..5`, pièces jointes = PDF (+ BL) ou fichier généré.
Un message par client (ou par facture si `PFOPT1='O'`). Paramètres saisis dans le programme **Paramètres.exe**.

### 7.3 FTP
`FTPConnecte(PFADD, PFUSER, PFMDP[, PFPORT])`, mode passif ; répertoire distant = partie de `PFADD` après le
premier `/`. Copie préalable dans `C:\temp`. **FTP en clair** (`PRT057F.PFSEC` « sécurisé » n'est pas lu).
EDIP : suppression préalable du fichier distant.

### 7.4 PDP (webservice) — `PFFTP = 'X'`, format FACX obligatoire
Aiguillage `FAC010.PPDP` : `SUPERPDP` (actif) ; `B2BROUTER` (`TransfertToB2Brouter` : URL **staging** et clé
`"apikey"` **en dur**, compte 265802 — non opérationnel) ; autre → erreur « PDP non géré ».
SuperPDP : `POST <PFXURLWS>/v1.beta/invoices?external_id=<AAAA>_<n°>`, `Content-Type: application/pdf`, corps =
le PDF Factur-X, `Authorization: Bearer`. Succès HTTP 200/201 avec `id` → `MAJFAC080_PDP_ID`.
Auth OAuth2 `refresh_token` (spec 2026-07-24) : `FAC010.PFCLIENTID/PFXMDP` = application Axilog,
`PFXCLEAPI` = refresh_token de la société, **cache partagé en base** `PFXACCTOK` / `PFXACCEXP` (AAAAMMJJHHMMSS),
rotation persistée en un seul UPDATE FAC010, 2 tentatives en cas de collision entre postes.
Pas d'envoi au client par mail en mode PDP (la PDP route la facture).

## 8. Traçage des envois (tables écrites)

| Table | Écriture | Quand |
|---|---|---|
| **FAC080** | `MAJFAC080` : `fdtage='O', fddatee=AAAAMMJJ, fdheure=HHMMSS, fdmanau='M'|'A', fdmethe='M'|'F'|'W', fdtypee=<format>` | voir § 10 (moments variables) |
| **FAC080** | `MAJFAC080_PDP_ID` : idem + `fdmethe='W', FDIDFACTX=<id PDP>, fdpdpred='SuperPDP'|'B2Brouter'` | après POST PDP réussi |
| **FAC010** | `pfxcleapi, pfxacctok, pfxaccexp` | à chaque refresh OAuth |
| **AA00082** | compteur d'interchange EDIFACT `rtable='ED'` (`tnum+1`, insert si absent) | chaque fichier EDI |
| Fichier | `%TEMP%\EnvoiFacture\log_superpdp.txt` (journal cumulatif HTTP PDP, corps de réponse inclus) | chaque appel PDP |

Aucune table de journal des messages (destinataires, objet, PJ) : seul l'état de la facture est conservé ; le
statut de livraison PDP est suivi par **FactureElectronique** (`FAC080.FDSTAT`, `FACTURES_STATUTS`).
Le résumé de l'envoi auto (`tabConnexion`, statut OK/KO) n'est qu'en mémoire.

## 9. Paramètres INI

| Fichier | Section / clé | Usage |
|---|---|---|
| `NetPrint.Ini` (dossier exe) | `[GUISys TN5250] Host` | nom du serveur IBM i pour le partage `\\Host\home\...` |
| `EnvoiFactures.Ini` | `[GENERAL] TypeEnvoiDefaut` (M/A) | onglet par défaut |
| `Parametre.Ini` | `[PARAM] NomSource, TypeConnexion=ODBC, Bibliotheque=NP11F` | connexion (composant Connexion) |
| `mail.Ini` | `[ENVOIAUTO] TLS` | **abandonné** (remplacé par `FAC010.PTLS`) |

## 10. Gestion des erreurs et anomalies (à ne pas reproduire)

1. **Facture marquée envoyée avant l'envoi** (manuel) : pour PDF/CSV/TXT/EDI/EDIP, `MAJFAC080` est appelé à la
   génération du fichier, avant l'ouverture d'Outlook ou le FTP, même si l'envoi échoue. FACX + mail : marquée à
   l'ouverture du **brouillon** Outlook, que l'utilisateur l'envoie ou non. En auto, en revanche, FAC080 n'est mis à
   jour qu'après succès SMTP/FTP (correct).
2. **Attente infinie** du PDF (`TANTQUE PAS fFichierExiste`) : si FACEMANAV échoue (erreurs avalées par MONMSG),
   l'application gèle. Idem BL.
3. **Envoi auto PDP groupé par client** (`PFOPT1 <> 'O'`) : `tabFacturesX` est vidé à **chaque** facture FACX →
   seule la **dernière** facture du client est transmise à la PDP ; les autres restent non envoyées (reproposées au
   prochain passage) alors que la ligne affiche « OK ».
4. Auto `PFOPT1 = 'O'` + FACX : le type de fichier passé est `MethEnv` (« W ») au lieu de « FACX » → `FDTYPEE='W'`.
5. Fichier `factur-x.xml` unique dans le temp utilisateur, PDF Factur-X qui **écrase** le PDF d'origine sur l'IFS
   (plus de version « simple » ; deux utilisateurs en même temps peuvent se gêner sur le même chemin IFS).
6. Messages d'erreur auto « destinataire absent / SMTP absent / expéditeur absent » ajoutés au statut mais l'envoi
   continue quand même.
7. **SQL concaténé** partout (valeurs non paramétrées) ; compteur EDI `AA00082` lu puis incrémenté sans verrou
   (deux postes → même n° d'interchange).
8. **Secrets en clair** en base : `PRT057F.PFMDP` (FTP), `FAC010.PSMTPM` (SMTP), `FAC010.PFXMDP` (secret OAuth),
   tokens ; journal PDP local avec corps de réponse.
9. B2Brouter : URL staging, clé et version d'API en dur → code à ignorer.
10. `TableauAjouteLigne(tabFichiersPDF, chemin, nom)` dans `CreePDFBL` appelé sur un tableau à **une** colonne
    (mail manuel) : ajoute aussi le nom seul, filtré ensuite par `fFichierExiste` — fragile.
11. Erreurs SQL : `Info(SQL.MesErreur)` bloquant dans la boucle ; requêtes `SQLExec` sans `SINON` à plusieurs endroits
    (échec silencieux, cf. pack portable).
12. Code mort : `FichierEDI1SVGD`, `InitVarglobaleSave`, `CreationFactureXAvecBL`, `FEN_EnvoifacturesXML.wdw`,
    `Cl_Mapi32`, branches commentées « SagePdp ».

**Variante « EnvoideFactures - test2026 »** : instantané d'août 2024 en format binaire (non lisible par l'extracteur),
antérieur à Factur-X/SuperPDP (aucune chaîne `FACX`/`SuperPdp`), seulement une trace B2Brouter. Pas de différence
fonctionnelle à reprendre.

## 11. Pistes SaaS

- **Sélection** : reprendre la règle FDEDT='O' / FDTAGE<>'O' / FDNONE='' / FDEDIT<>'S' et les paramètres PRT057F
  (mêmes tables que le WinDev ; PRT057F est aussi lue par FactureElectronique, Clients, ConsultationClients).
- **PDF** : solution native (Socle Documents / QuestPDF) au lieu de FACEMANAV + IFS — mais FAC202P5 fait foi pour la
  mise en page (à rétro-documenter avant de remplacer) ; à défaut, appel IBM i avec délai maximum.
- **Factur-X** : génération PDF/A-3 + XML CII côté .NET (bibliothèque dédiée), en reprenant les règles des specs
  existantes (ordre CII, conditionnels, ventilation multi-taux, port/escompte, affacturage, acheteur étranger).
- **Mail** : `IMessagerie` (SMTP FAC010) + brouillon .eml + journal ENV100 du prototype, type de document « FAC » ;
  marquer FAC080 **après** envoi effectif (ou à l'enregistrement du brouillon, explicitement).
- **PDP** : client SuperPDP (OAuth refresh_token, cache FAC010 partagé avec FactureElectronique, `external_id`,
  FDIDFACTX/FDPDPRED) ; envoi **une facture = un appel**, résultat par facture.
- **FTP/EDI** : SFTP, formats CSV/TXT/EDIFACT client par client — à inventorier (clients réellement en
  `PFFTP='O'` / `PFFMT` EDI) avant de les porter.
- **Lot automatique** : service d'arrière-plan (au lieu d'un poste ouvert), avec reprise et journal persistant.
