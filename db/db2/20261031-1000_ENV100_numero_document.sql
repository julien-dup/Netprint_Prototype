-- =====================================================================
-- Journal des envois (ENV100) : n° du document envoyé quand il n'a pas de dossier (BL : jusqu'à 6 chiffres).
-- Colonne à part : RDOSS garde le format dossier de NetPrint (5 chiffres, qui forme avec la clé le n° de lot) ; elle reste
-- à 0 pour ces documents. Les lignes existantes (ARC) ne changent pas.
-- Application : une fois, par son auteur, après la fusion (CLAUDE.md), après 20261030-2230_ENV100_journal_envois.sql.
-- Tant que ce script n'est pas passé, un BL envoyé part quand même ; seule sa trace manque.
-- =====================================================================

ALTER TABLE NP11F.ENV100 ADD COLUMN ENNDOC NUMERIC(9, 0) NOT NULL DEFAULT 0;

LABEL ON COLUMN NP11F.ENV100 (ENNDOC IS 'N° document sans dossier');

CREATE INDEX NP11F.ENV100L2 ON NP11F.ENV100 (RSOC, ENTYPE, ENNDOC, ENDATE);
