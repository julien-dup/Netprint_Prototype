using System.Data.Odbc;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Ventes.Donnees;

// =====================================================================
// Lot B : échec (D50006), documents (D50008), tags (TAG_DEVIS, D50010), dans les mêmes tables que le WinDev.
// Requêtes reprises de FEN_SaisieEchec, FEN_Documents, FEN_Tags et InitVarGlobales, en SQL paramétré.
// Pas de transaction (comme le WinDev) : les fichiers NP11F ne sont pas forcément journalisés.
// =====================================================================
internal sealed partial class ComplementsDevisDb2
{
    // ---------- Échec ----------
    public async Task<List<CodeLibelle>> LireMotifsEchecAsync(int societe, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var cmd = Commande(cnx,
            $"SELECT RARG, TLIB1 FROM {Bib}.AA00081 WHERE RSOC = ? AND RTABLE = 'ME' AND TTAG <> '*' ORDER BY RARG", societe);
        var motifs = new List<CodeLibelle>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) motifs.Add(new CodeLibelle(rd.Texte(0), rd.Texte(1)));
        return motifs;
    }

    public async Task<(string Motif, int Date, string Texte)?> LireEchecAsync(int societe, int devis, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var cmd = Commande(cnx,
            $"SELECT F6MOTIF, F6DATEE, F6TXT FROM {Bib}.D50006 WHERE RSOC = ? AND RDEV = ?", societe, devis);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? (rd.Texte(0), rd.Entier(1), rd.Texte(2)) : null;
    }

    // Insertion si aucun échec pour ce n° de devis, sinon mise à jour (BTN_Valider)
    public async Task EnregistrerEchecAsync(int societe, int devis, string motif, int date, string texte, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        await using (var maj = Commande(cnx,
            $"UPDATE {b}.D50006 SET F6MOTIF = ?, F6TXT = ?, F6DATEE = ? WHERE RSOC = ? AND RDEV = ?",
            motif, texte, date, societe, devis))
            if (await maj.ExecuteNonQueryAsync(ct) > 0) return;
        await using var ajout = Commande(cnx,
            $"INSERT INTO {b}.D50006 (RSOC, RDEV, F6MOTIF, F6TXT, F6DATEE) VALUES (?, ?, ?, ?, ?)",
            societe, devis, motif, texte, date);
        await ajout.ExecuteNonQueryAsync(ct);
    }

    public async Task SupprimerEchecAsync(int societe, int devis, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var cmd = Commande(cnx, $"DELETE FROM {Bib}.D50006 WHERE RSOC = ? AND RDEV = ?", societe, devis);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------- Documents ----------
    public async Task<List<DocumentDevis>> LireDocumentsAsync(int societe, int devis, CancellationToken ct)
    {
        // ÉCART : le WinDev lisait aussi le contenu (BLOB) de tous les documents à l'ouverture ;
        // ici, seulement la liste, le contenu est lu à l'ouverture d'un document.
        await using var cnx = await OuvrirAsync(ct);
        await using var cmd = Commande(cnx,
            $"SELECT ID, DEDNOM, DEDATE FROM {Bib}.D50008 WHERE RSOC = ? AND RDEV = ? ORDER BY DEDATE, ID", societe, devis);
        var documents = new List<DocumentDevis>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            documents.Add(new DocumentDevis(rd.Entier(0), rd.Texte(1), rd.IsDBNull(2) ? null : Convert.ToDateTime(rd.GetValue(2))));
        return documents;
    }

    // Droit de suppression (InitVarGlobales) : PRT000P1, programme PCDOCUMENT, AOPT01 = « ** » interdit.
    // Le paramétrage du profil prime sur celui de son groupe (AI00F.AGROUP) ; sans paramétrage, autorisé.
    public async Task<bool> PeutSupprimerDocumentsAsync(string profil, CancellationToken ct)
    {
        var b = Bib;
        profil = profil.ToUpperInvariant();
        await using var cnx = await OuvrirAsync(ct);
        await using (var cmd = Commande(cnx, $"SELECT AOPT01 FROM {b}.PRT000P1 WHERE APGM = 'PCDOCUMENT' AND AUSR = ? FETCH FIRST 1 ROW ONLY", profil))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            if (await rd.ReadAsync(ct)) return rd.Texte(0) != "**";

        string groupe = "";
        await using (var cmd = Commande(cnx, $"SELECT AGROUP FROM {b}.AI00F WHERE RPRF = ? FETCH FIRST 1 ROW ONLY", profil))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            if (await rd.ReadAsync(ct)) groupe = rd.Texte(0);
        if (groupe == "") return true;

        await using (var cmd = Commande(cnx, $"SELECT AOPT01 FROM {b}.PRT000P1 WHERE APGM = 'PCDOCUMENT' AND AGROUP = ? FETCH FIRST 1 ROW ONLY", groupe))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            if (await rd.ReadAsync(ct)) return rd.Texte(0) != "**";
        return true;
    }

    // Insertion d'un document ; l'identifiant est relu dans la même instruction.
    // ÉCART : le WinDev relisait « select max(id) from d50008 », faux si deux postes ajoutent en même temps.
    public async Task<int> AjouterDocumentAsync(int societe, int devis, string nom, string extension, byte[] contenu, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"""
            SELECT ID FROM FINAL TABLE (
                INSERT INTO {Bib}.D50008 (RSOC, RDEV, DEDNOM, DEDEXT, DEFILE, DEDATE, DECOMM)
                VALUES (?, ?, ?, ?, ?, CURRENT_TIMESTAMP, ''))
            """;
        cmd.Parameters.Valeur(societe);
        cmd.Parameters.Valeur(devis);
        cmd.Parameters.Valeur(nom);
        cmd.Parameters.Valeur(extension);
        cmd.Parameters.Binaire(contenu);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task SupprimerDocumentAsync(int societe, int devis, int id, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var cmd = Commande(cnx, $"DELETE FROM {Bib}.D50008 WHERE RSOC = ? AND RDEV = ? AND ID = ?", societe, devis, id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(int Devis, string Nom, byte[] Contenu)?> LireFichierAsync(int societe, int id, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var cmd = Commande(cnx, $"SELECT RDEV, DEDNOM, DEFILE FROM {Bib}.D50008 WHERE RSOC = ? AND ID = ?", societe, id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return (rd.Entier(0), rd.Texte(1), rd.IsDBNull(2) ? Array.Empty<byte>() : (byte[])rd.GetValue(2));
    }

    // Un devis (toutes versions) est-il visible par le représentant imposé ?
    public async Task<bool> DevisDuRepresentantAsync(int societe, int devis, int representant, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var cmd = Commande(cnx,
            $"SELECT 1 FROM {Bib}.D5000F WHERE RSOC = ? AND RDEV = ? AND DEREPR = ? FETCH FIRST 1 ROW ONLY", societe, devis, representant);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    // ---------- Tags ----------
    public async Task<List<TagDevis>> LireTagsAsync(CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var cmd = Commande(cnx, $"SELECT IDTAG, TAG FROM {Bib}.TAG_DEVIS ORDER BY TAG");
        var tags = new List<TagDevis>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) tags.Add(new TagDevis(rd.Entier(0), rd.Texte(1)));
        return tags;
    }

    // Tags posés sur le devis / version / hypothèse. Comme le WinDev, le type de coût n'est pas filtré
    // (le WinDev enregistrait toujours « C »).
    public async Task<List<int>> LireTagsDuDevisAsync(int societe, CleDevis cle, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var cmd = Commande(cnx,
            $"SELECT DISTINCT IDTAG FROM {Bib}.D50010 WHERE RSOC = ? AND RDEV = ? AND RVERS = ? AND RHYPO = ?",
            societe, cle.Numero, cle.Version, cle.Hypothese);
        var ids = new List<int>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) ids.Add(rd.Entier(0));
        return ids;
    }

    public async Task<TagDevis> CreerTagAsync(string libelle, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var cmd = Commande(cnx, $"SELECT IDTAG FROM FINAL TABLE (INSERT INTO {Bib}.TAG_DEVIS (TAG) VALUES (?))", libelle);
        return new TagDevis(Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)), libelle);
    }

    // Remplace les tags du devis / version / hypothèse : suppression puis insertion des tags choisis.
    // ÉCART : le WinDev ajoutait sans jamais supprimer (impossible de retirer un tag, doublons) ;
    // le type de coût enregistré est celui du devis (le WinDev forçait « C »).
    public async Task EnregistrerTagsAsync(int societe, CleDevis cle, IEnumerable<int> tags, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        await using (var suppr = Commande(cnx, $"DELETE FROM {b}.D50010 WHERE RSOC = ? AND RDEV = ? AND RVERS = ? AND RHYPO = ?",
            societe, cle.Numero, cle.Version, cle.Hypothese))
            await suppr.ExecuteNonQueryAsync(ct);
        foreach (var id in tags.Distinct())
        {
            await using var ajout = Commande(cnx,
                $"INSERT INTO {b}.D50010 (RSOC, RCOUT, RDEV, RVERS, RHYPO, IDTAG) VALUES (?, ?, ?, ?, ?, ?)",
                societe, cle.TypeCout, cle.Numero, cle.Version, cle.Hypothese, id);
            await ajout.ExecuteNonQueryAsync(ct);
        }
    }
}
