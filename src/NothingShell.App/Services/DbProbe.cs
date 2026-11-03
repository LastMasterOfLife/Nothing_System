using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MySqlConnector;
using Npgsql;
using NothingShell.Core.Connectors;

namespace NothingShell.App.Services;

/// <summary>
/// <c>NothingShell.exe --db-check engine=… host=… port=… database=… user=… kind=… query=…</c>: controllo di un database
/// PostgreSQL, MySQL o MariaDB per i connettori di <c>docs/18-connettori-diagnostica.md</c>. La password arriva in
/// <c>NOTHING_SECRET</c>, mai negli argomenti. Stampa le letture in JSON; la connessione è di sola lettura e la query passa
/// da <see cref="SqlGuard"/>.
/// </summary>
internal static class DbProbe
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        try
        {
            var values = args.Select(a => a.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
            var json = JsonSerializer.Serialize(await ProbeAsync(values, Environment.GetEnvironmentVariable("NOTHING_SECRET")));
            Console.Out.Write(json);
            Console.Out.Flush();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Describe(ex));
            return 1;
        }
    }

    private static async Task<Dictionary<string, object?>> ProbeAsync(Dictionary<string, string> a, string? password)
    {
        string Get(string key) => a.TryGetValue(key, out var v) ? v : string.Empty;
        var query = Get("query") is { Length: > 0 } q ? q : "SELECT 1";
        if (SqlGuard.Check(query) is { } problem)
            throw new InvalidOperationException($"query non ammessa: {problem}");
        var port = int.TryParse(Get("port"), NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : 0;
        var engine = Get("engine");

        await using DbConnection connection = engine switch
        {
            "postgres" => new NpgsqlConnection(new NpgsqlConnectionStringBuilder
            {
                Host = Get("host"),
                Port = port > 0 ? port : 5432,
                Database = Get("database").Length > 0 ? Get("database") : null,
                Username = Get("user").Length > 0 ? Get("user") : null,
                Password = password,
                Timeout = 8,
                CommandTimeout = 10,
                ApplicationName = "Nothing Shell",
            }.ConnectionString),
            "mysql" or "mariadb" => new MySqlConnection(new MySqlConnectionStringBuilder
            {
                Server = Get("host"),
                Port = (uint)(port > 0 ? port : 3306),
                Database = Get("database"),
                UserID = Get("user"),
                Password = password ?? string.Empty,
                ConnectionTimeout = 8,
                DefaultCommandTimeout = 10,
                ApplicationName = "Nothing Shell",
            }.ConnectionString),
            _ => throw new InvalidOperationException($"tipo di database sconosciuto: {engine}"),
        };

        var clock = Stopwatch.StartNew();
        await connection.OpenAsync();
        var connectMs = clock.ElapsedMilliseconds;

        // sola lettura anche se la query (o l'utente) volesse altro
        await using (var readOnly = connection.CreateCommand())
        {
            readOnly.CommandText = engine == "postgres" ? "SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY" : "SET SESSION TRANSACTION READ ONLY";
            await readOnly.ExecuteNonQueryAsync();
        }

        object? scalar;
        clock.Restart();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = query;
            command.CommandTimeout = 10;
            scalar = await command.ExecuteScalarAsync();
        }
        var queryMs = clock.ElapsedMilliseconds;
        if (scalar is DBNull)
            scalar = null;

        var result = new Dictionary<string, object?>
        {
            ["stato"] = "ONLINE",
            ["connessione"] = connectMs,
            ["query"] = queryMs,
            ["versione"] = connection.ServerVersion,
        };
        switch (Get("kind"))
        {
            case "valore":
                if (scalar is null)
                    throw new InvalidOperationException("la query non restituisce un valore");
                result["valore"] = Convert.ToDouble(scalar, CultureInfo.InvariantCulture);
                break;
            case "data":
                result["stato"] = scalar is null ? "NESSUN DATO" : "OK";
                if (scalar is null)
                {
                    result["ultimo"] = "—";
                    result["ore"] = "n.d.";
                    break;
                }
                var moment = scalar is DateTimeOffset offset ? offset.UtcDateTime : Convert.ToDateTime(scalar, CultureInfo.InvariantCulture);
                var utc = Get("utc") == "si";
                var now = utc ? DateTime.UtcNow : DateTime.Now;
                if (!utc && moment.Kind == DateTimeKind.Utc)
                    moment = moment.ToLocalTime();
                result["ultimo"] = moment.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
                result["ore"] = Math.Round((now - moment).TotalHours, 1);
                break;
            default:
                result["valore"] = scalar is null ? string.Empty : Convert.ToString(scalar, CultureInfo.InvariantCulture);
                break;
        }
        return result;
    }

    private static string Describe(Exception ex) => ex switch
    {
        PostgresException pg => $"{pg.MessageText} ({pg.SqlState})",
        MySqlException my => $"{my.Message} ({my.ErrorCode})",
        NpgsqlException { InnerException: { } inner } => inner.Message,
        _ => ex.Message,
    };
}
