-- =====================================================================
-- Journal des envois de documents (ARC, puis offres, BL, factures…) : qui, quand, à qui, par quel canal, avec le PDF.
-- Nouvelle table (aucune table existante ne trace les envois). Utilisée par NetPrint.Noyau.Messagerie.JournalEnvois.
-- Application : une fois, par son auteur, après la fusion (CLAUDE.md). Remplacer NP11F par la bibliothèque visée.
-- Sans cette table, l'application envoie quand même ; le journal est simplement ignoré.
-- =====================================================================

CREATE TABLE NP11F.ENV100 (
    ENID     BIGINT        NOT NULL GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1),
    RSOC     NUMERIC(4, 0) NOT NULL,
    ENMSG    CHAR(32)      NOT NULL,                 -- identifiant du message (plusieurs documents dans un même message)
    ENDATE   TIMESTAMP     NOT NULL DEFAULT CURRENT TIMESTAMP,
    ENPROF   CHAR(10)      NOT NULL DEFAULT ' ',     -- profil de l'utilisateur
    ENTYPE   CHAR(3)       NOT NULL,                 -- document : ARC (puis OFR, BL, FAC…)
    RDOSS    NUMERIC(5, 0) NOT NULL DEFAULT 0,       -- dossier (commande, devis…)
    ENDCLE   CHAR(1)       NOT NULL DEFAULT ' ',     -- clé du dossier
    RGEN     NUMERIC(6, 0) NOT NULL DEFAULT 0,       -- client
    RCLI     NUMERIC(7, 0) NOT NULL DEFAULT 0,
    ENCANAL  CHAR(1)       NOT NULL,                 -- S envoi direct (SMTP), O brouillon préparé pour Outlook
    ENSTAT   CHAR(1)       NOT NULL,                 -- E envoyé, T envoi de test (redirigé), X échec, P préparé (Outlook)
    ENEXP    VARCHAR(100)  NOT NULL DEFAULT '',      -- expéditeur
    ENDEST   VARCHAR(1000) NOT NULL DEFAULT '',      -- destinataires (séparés par « ; »)
    ENCOPI   VARCHAR(1000) NOT NULL DEFAULT '',      -- copies
    ENREDI   VARCHAR(100)  NOT NULL DEFAULT '',      -- adresse de test qui a reçu le message (redirection)
    ENOBJ    VARCHAR(300)  NOT NULL DEFAULT '',      -- objet
    ENERR    VARCHAR(500)  NOT NULL DEFAULT '',      -- erreur (échec)
    ENNOM    VARCHAR(100)  NOT NULL DEFAULT '',      -- nom du PDF
    ENPDF    BLOB(20M),                              -- PDF envoyé (tel que le client l'a reçu)
    PRIMARY KEY (ENID)
);

CREATE INDEX NP11F.ENV100L1 ON NP11F.ENV100 (RSOC, ENTYPE, RDOSS, ENDCLE, ENDATE);

LABEL ON TABLE NP11F.ENV100 IS 'Journal des envois de documents';

-- Journalisation (comme les autres tables écrites par l'application) :
-- STRJRNPF FILE(NP11F/ENV100) JRN(NP11JRN/JRN) IMAGES(*BOTH)
