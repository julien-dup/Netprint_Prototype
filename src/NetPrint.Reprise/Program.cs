// =====================================================================
// NetPrint.Reprise : copie des tables NP11F de Db2 (IBM i, ODBC) vers PostgreSQL
//
//   * lit la liste des colonnes dans PostgreSQL (le script 01_create_tables_np11f.sql fait foi)
//   * vide les tables cibles puis les recharge (COPY binaire, rapide même pour les BLOB)
//   * tout se fait dans UNE transaction : en cas d'erreur, la base cible n'est pas modifiée
//
// Paramètres (user secrets, variables d'environnement ou ligne de commande) :
//   ConnectionStrings:Db2   chaîne ODBC IBM i (déjà dans les user secrets de GestionOutils)
//   ConnectionStrings:Pg    chaîne PostgreSQL cible, ou --cible="Host=...;Database=netprint;..."
//   --tables=fou010,fou010d  (facultatif) seulement ces tables
//   --oui=1                  (facultatif) pas de demande de confirmation
// =====================================================================
using System.Data;
using System.Data.Odbc;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var ebcdic = Encoding.GetEncoding(20297);   // EBCDIC France (CCSID 297), si une zone arrive en binaire

var config = new ConfigurationBuilder()
    .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

var csDb2 = config.GetConnectionString("Db2");
var csPg = config["cible"] ?? config.GetConnectionString("Pg");
if (string.IsNullOrWhiteSpace(csDb2) || string.IsNullOrWhiteSpace(csPg))
{
    Console.Error.WriteLine("Chaînes manquantes : ConnectionStrings:Db2 et ConnectionStrings:Pg (ou --cible=...).");
    return 1;
}

// Ordre de chargement : les tables « parents » avant celles qui les référencent (clés étrangères)
string[] ordre = ["saaspardef", "aa0007", "aa00081", "aa0003", "b500f", "fou010", "abcoutd", "fou010d", "saasparval",
                   "spf010", "spf025"];   // articles et liens articles / outils (script 06)
var demandees = (config["tables"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(t => t.ToLowerInvariant()).ToList();
var tables = demandees.Count == 0
    ? ordre.ToList()
    : ordre.Where(demandees.Contains).Concat(demandees.Except(ordre)).ToList();

var cible = new NpgsqlConnectionStringBuilder(csPg);
Console.WriteLine($"Source : Db2 IBM i (ODBC), schéma NP11F");
Console.WriteLine($"Cible  : PostgreSQL {cible.Host}:{cible.Port}  base {cible.Database}  utilisateur {cible.Username}");
Console.WriteLine($"Tables : {string.Join(", ", tables)}");
Console.WriteLine("Les tables cibles vont être VIDÉES puis rechargées.");
if (config["oui"] is null)
{
    Console.Write("Tapez OUI pour continuer : ");
    if (!string.Equals(Console.ReadLine()?.Trim(), "OUI", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Abandon.");
        return 0;
    }
}

var chrono = Stopwatch.StartNew();
try
{
    using var odbc = new OdbcConnection(csDb2);
    odbc.Open();
    await using var pg = new NpgsqlConnection(csPg);
    await pg.OpenAsync();

    // 1. Préparation : colonnes PostgreSQL et colonnes Db2 de chaque table
    var plans = new List<(string Table, List<Colonne> Colonnes)>();
    foreach (var t in tables)
    {
        var colsPg = await ColonnesPgAsync(pg, t);
        if (colsPg.Count == 0) throw new Exception($"Table np11f.{t} introuvable dans PostgreSQL.");

        HashSet<string> colsDb2;
        try { colsDb2 = ColonnesDb2(odbc, t); }
        catch (OdbcException ex)
        {
            Console.WriteLine($"  ! NP11F.{t.ToUpperInvariant()} illisible côté Db2, table ignorée ({ex.Errors[0].Message.Trim()})");
            continue;
        }

        var aCharger = colsPg.Where(c => colsDb2.Contains(c.Nom.ToUpperInvariant())).ToList();
        foreach (var c in colsPg.Except(aCharger))
        {
            if (!c.Nullable && !c.ADefaut)
                throw new Exception($"Colonne {t}.{c.Nom} absente côté Db2 et sans valeur par défaut dans PostgreSQL.");
            Console.WriteLine($"  ! {t}.{c.Nom} absente côté Db2 : valeur par défaut PostgreSQL");
        }
        plans.Add((t, aCharger));
    }
    if (plans.Count == 0) { Console.WriteLine("Rien à charger."); return 0; }

    // 2. Vidage + chargement dans une seule transaction
    await using var tx = await pg.BeginTransactionAsync();
    await ExecAsync(pg, $"TRUNCATE {string.Join(", ", plans.Select(p => "np11f." + p.Table))} CASCADE");

    var bilan = new List<(string Table, long Lues, long Db2)>();
    foreach (var (t, cols) in plans)
    {
        var tronques = new Dictionary<string, int>();
        var select = $"SELECT {string.Join(", ", cols.Select(c => c.Nom.ToUpperInvariant()))} FROM NP11F.{t.ToUpperInvariant()}";
        var copy = $"COPY np11f.{t} ({string.Join(", ", cols.Select(c => c.Nom))}) FROM STDIN (FORMAT BINARY)";
        long n = 0;

        using (var cmd = new OdbcCommand(select, odbc) { CommandTimeout = 0 })
        // Lecture séquentielle si la table contient un BLOB : le pilote ODBC IBM i lit mieux les LOB par morceaux
        using (var rd = cols.Any(c => c.Type == "bytea") ? cmd.ExecuteReader(CommandBehavior.SequentialAccess) : cmd.ExecuteReader())
        await using (var imp = await pg.BeginBinaryImportAsync(copy))
        {
            while (rd.Read())
            {
                await imp.StartRowAsync();
                for (var i = 0; i < cols.Count; i++)
                {
                    try
                    {
                        object? v = rd.IsDBNull(i) ? null
                                  : cols[i].Type == "bytea" ? LireOctets(rd, i)
                                  : rd.GetValue(i);
                        await EcrireAsync(imp, cols[i], v, tronques);
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"{t}.{cols[i].Nom}, ligne {n + 1} : {ex.GetType().Name} - {ex.Message}", ex);
                    }
                }
                if (++n % 5000 == 0) Console.Write($"\r  {t,-12} {n,10:N0} lignes...");
            }
            await imp.CompleteAsync();
        }
        Console.WriteLine($"\r  {t,-12} {n,10:N0} lignes chargées      ");
        foreach (var (col, nb) in tronques)
            Console.WriteLine($"  ! {t}.{col} : {nb} valeur(s) tronquée(s) à la longueur PostgreSQL");

        // Identités (ex. fou010d.id) : la prochaine valeur part après le plus grand ID repris
        foreach (var c in cols.Where(c => c.Identite))
            await ExecAsync(pg, $"SELECT setval(pg_get_serial_sequence('np11f.{t}', '{c.Nom}'), " +
                                $"COALESCE((SELECT MAX({c.Nom}) FROM np11f.{t}), 0) + 1, false)");

        using var cpt = new OdbcCommand($"SELECT COUNT(*) FROM NP11F.{t.ToUpperInvariant()}", odbc);
        bilan.Add((t, n, Convert.ToInt64(cpt.ExecuteScalar())));
    }

    await tx.CommitAsync();
    await ExecAsync(pg, "ANALYZE " + string.Join(", ", plans.Select(p => "np11f." + p.Table)));

    Console.WriteLine();
    Console.WriteLine($"{"Table",-12} {"Db2",10} {"PostgreSQL",12}");
    foreach (var (t, lues, db2) in bilan)
        Console.WriteLine($"{t,-12} {db2,10:N0} {lues,12:N0}{(lues == db2 ? "" : "   <-- écart")}");
    Console.WriteLine($"Terminé en {chrono.Elapsed:mm\\:ss}.");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.Error.WriteLine($"ERREUR : {ex.Message}");
    Console.Error.WriteLine("La transaction est annulée : la base PostgreSQL n'a pas été modifiée.");
    return 2;
}

// ---------------------------------------------------------------------

// BLOB lu par blocs de 64 Ko (GetValue sur un BLOB provoque un dépassement avec certains pilotes IBM i)
static byte[] LireOctets(System.Data.Common.DbDataReader rd, int i)
{
    using var ms = new MemoryStream();
    var tampon = new byte[64 * 1024];
    long position = 0, lus;
    while ((lus = rd.GetBytes(i, position, tampon, 0, tampon.Length)) > 0)
    {
        ms.Write(tampon, 0, (int)lus);
        position += lus;
    }
    return ms.ToArray();
}

async Task ExecAsync(NpgsqlConnection cnx, string sql)
{
    await using var cmd = new NpgsqlCommand(sql, cnx);
    await cmd.ExecuteNonQueryAsync();
}

async Task<List<Colonne>> ColonnesPgAsync(NpgsqlConnection cnx, string table)
{
    const string sql = """
        SELECT column_name, data_type, character_maximum_length,
               is_nullable = 'YES', column_default IS NOT NULL OR is_identity = 'YES', is_identity = 'YES'
        FROM information_schema.columns
        WHERE table_schema = 'np11f' AND table_name = @t
        ORDER BY ordinal_position
        """;
    await using var cmd = new NpgsqlCommand(sql, cnx);
    cmd.Parameters.AddWithValue("t", table);
    var liste = new List<Colonne>();
    await using var rd = await cmd.ExecuteReaderAsync();
    while (await rd.ReadAsync())
        liste.Add(new Colonne(rd.GetString(0), rd.GetString(1),
            rd.IsDBNull(2) ? null : Convert.ToInt32(rd.GetValue(2)),
            rd.GetBoolean(3), rd.GetBoolean(4), rd.GetBoolean(5)));
    return liste;
}

HashSet<string> ColonnesDb2(OdbcConnection cnx, string table)
{
    using var cmd = new OdbcCommand($"SELECT * FROM NP11F.{table.ToUpperInvariant()} WHERE 1 = 0", cnx);
    using var rd = cmd.ExecuteReader(CommandBehavior.SchemaOnly);
    return Enumerable.Range(0, rd.FieldCount).Select(i => rd.GetName(i).Trim().ToUpperInvariant()).ToHashSet();
}

async Task EcrireAsync(NpgsqlBinaryImporter imp, Colonne c, object? v, Dictionary<string, int> tronques)
{
    if (v is null && c.Nullable) { await imp.WriteNullAsync(); return; }

    switch (c.Type)
    {
        case "character":
        case "character varying":
        case "text":
        {
            var s = v switch
            {
                null => c.Type == "character" ? " " : "",
                byte[] b => ebcdic.GetString(b),          // zone CCSID 65535 renvoyée en binaire
                _ => Convert.ToString(v) ?? ""
            };
            s = s.Replace('\0', ' ');                     // x'00' accepté par Db2, refusé par PostgreSQL
            if (c.Longueur is int max && s.Length > max)
            {
                s = s.TrimEnd();
                if (s.Length > max)
                {
                    s = s[..max];
                    tronques[c.Nom] = tronques.GetValueOrDefault(c.Nom) + 1;
                }
            }
            var type = c.Type switch
            {
                "character" => NpgsqlDbType.Char,
                "character varying" => NpgsqlDbType.Varchar,
                _ => NpgsqlDbType.Text
            };
            await imp.WriteAsync(s, type);
            break;
        }
        case "numeric":
            await imp.WriteAsync(v is null ? 0m : Convert.ToDecimal(v), NpgsqlDbType.Numeric);
            break;
        case "bigint":
            await imp.WriteAsync(v is null ? 0L : Convert.ToInt64(v), NpgsqlDbType.Bigint);
            break;
        case "integer":
            await imp.WriteAsync(v is null ? 0 : Convert.ToInt32(v), NpgsqlDbType.Integer);
            break;
        case "smallint":
            await imp.WriteAsync(v is null ? (short)0 : Convert.ToInt16(v), NpgsqlDbType.Smallint);
            break;
        case "bytea":
            await imp.WriteAsync(v as byte[] ?? Array.Empty<byte>(),NpgsqlDbType.Bytea);
            break;
        case "timestamp without time zone":
        {
            var d = v is null ? DateTime.Now : Convert.ToDateTime(v);
            await imp.WriteAsync(DateTime.SpecifyKind(d, DateTimeKind.Unspecified), NpgsqlDbType.Timestamp);
            break;
        }
        case "date":
            await imp.WriteAsync(DateOnly.FromDateTime(v is null ? DateTime.Today : Convert.ToDateTime(v)), NpgsqlDbType.Date);
            break;
        default:
            throw new NotSupportedException($"Type PostgreSQL non géré : {c.Nom} ({c.Type}).");
    }
}

record Colonne(string Nom, string Type, int? Longueur, bool Nullable, bool ADefaut, bool Identite);
