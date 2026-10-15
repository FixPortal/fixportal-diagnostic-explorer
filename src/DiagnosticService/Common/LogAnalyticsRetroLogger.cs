#region Copyright

// Diagnostic Explorer, a .Net diagnostic toolset
// Copyright (C) 2010 Cameron Elliot
//
// This file is part of Diagnostic Explorer.
//
// Diagnostic Explorer is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Diagnostic Explorer is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public License
// along with Diagnostic Explorer.  If not, see <http://www.gnu.org/licenses/>.
//
// http://diagexplorer.sourceforge.net/

#endregion

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Ingestion;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Diagnostic.Service.Transport;
using DiagnosticExplorer;
using MongoDB.Bson;

namespace Diagnostic.Service.Common;

/// <summary>
///     Log Analytics implementation of <see cref="IRetroLogger" />. Writes via the Logs Ingestion API
///     (DCE + DCR) and reads via the Log Analytics Query API (KQL). Selected by RetroType=loganalytics;
///     the MongoDB backend remains the default. See docs/retro-log-analytics-backend-spec.md.
///     Delete is NOT supported — Log Analytics is append-only (retention/TTL replaces interactive
///     delete). <see cref="SupportsDelete" /> is false so callers can gate the UI.
/// </summary>
public class LogAnalyticsRetroLogger : IRetroLogger
{
    // Mirror the Mongo backend's filter-pattern guards: bound length and verify the regex compiles
    // before it reaches the service, so a crafted catastrophic pattern can't be shipped to KQL.
    private const int MaxFilterPatternLength = 256;

    // KQL `matches regex` runs RE2, which — unlike the .NET engine used to validate the pattern,
    // and unlike the PCRE the Mongo backend executes via $regex — rejects lookaround, atomic
    // groups, conditionals and backreferences. A pattern using these compiles under .NET (so the
    // guard below passes) but then fails at query time with an opaque RequestFailedException.
    // Reject them up front with a clear message instead. (Note: RE2 *does* support \p{...} Unicode
    // classes and (?<name>) named groups, so those are intentionally not rejected.)
    private static readonly Regex Re2Incompatible = new(
        @"\(\?<?[=!]"
            + // lookahead / lookbehind:  (?=) (?!) (?<=) (?<!)
            @"|\(\?>"
            + // atomic group:            (?>...)
            @"|\(\?\("
            + // conditional:             (?(...)...)
            @"|\\[1-9]"
            + // numeric backreference:   \1 .. \9
            @"|\\k<", // named backreference:     \k<name>
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(1)
    );

    private readonly Lazy<LogsIngestionClient> _ingestion;

    private readonly LogAnalyticsSettings _options;
    private readonly Lazy<LogsQueryClient> _query;

    public LogAnalyticsRetroLogger(LogAnalyticsSettings options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        Require(_options.DceEndpoint, nameof(_options.DceEndpoint));
        Require(_options.DcrImmutableId, nameof(_options.DcrImmutableId));
        Require(_options.StreamName, nameof(_options.StreamName));
        Require(_options.TableName, nameof(_options.TableName));
        Require(_options.WorkspaceId, nameof(_options.WorkspaceId));

        // DefaultAzureCredential: EnvironmentCredential (AZURE_TENANT_ID / AZURE_CLIENT_ID /
        // AZURE_CLIENT_SECRET) in the container, AzureCliCredential locally. Constructing the
        // clients does no network I/O, but keep them lazy so a misconfigured-but-unused LA
        // backend never blocks startup.
        TokenCredential credential = new DefaultAzureCredential();
        _ingestion = new Lazy<LogsIngestionClient>(() =>
            new LogsIngestionClient(new Uri(_options.DceEndpoint), credential)
        );
        _query = new Lazy<LogsQueryClient>(() => new LogsQueryClient(credential));
    }

    /// <summary>
    /// Uses caller-configured SDK clients for ingestion and queries. Hosts can supply their own
    /// credentials, transports and retry settings while using the same column and row mappings.
    /// The clients remain caller-owned; this logger does not dispose them.
    /// </summary>
    public LogAnalyticsRetroLogger(
        LogAnalyticsSettings options,
        LogsIngestionClient ingestionClient,
        LogsQueryClient queryClient
    )
        : this(options)
    {
        ArgumentNullException.ThrowIfNull(ingestionClient);
        ArgumentNullException.ThrowIfNull(queryClient);
        _ingestion = new Lazy<LogsIngestionClient>(() => ingestionClient);
        _query = new Lazy<LogsQueryClient>(() => queryClient);
    }

    /// <summary>Log Analytics is append-only; per-record delete is not available.</summary>
    public bool SupportsDelete => false;

    // Return a faulted task rather than throwing synchronously: this is a Task-returning method,
    // so a sync throw would bypass async catch blocks in callers that only observe the task. The
    // UI also gates this off via SupportsDelete (see WebHub.RetroSupportsDelete), so it should
    // never actually be invoked under the Log Analytics backend.
    public Task<long> Delete(string[] idList)
    {
        return Task.FromException<long>(
            new NotSupportedException(
                "Delete is not supported by the Log Analytics Retro backend (append-only store). "
                    + "Use workspace/table retention policies to age records out."
            )
        );
    }

    public async Task WriteMessages(ICollection<DiagnosticMsg> msg, CancellationToken cancel)
    {
        if (msg == null || msg.Count == 0)
        {
            return;
        }

        // Keys MUST match the DCR stream declaration columns in infra/retro-loganalytics.bicep.
        // The appender supplies UTC clock values. SpecifyKind labels them for the SDK's
        // UTC serialization without shifting the clock a second time.
        var entries = msg.Select(m =>
            {
                return new Dictionary<string, object?>
                {
                    ["TimeGenerated"] = DateTime.SpecifyKind(m.Date, DateTimeKind.Utc),
                    ["Level"] = m.Level,
                    ["Machine"] = m.Machine,
                    ["Process"] = m.Process,
                    ["User"] = m.User,
                    ["Category"] = m.Category,
                    ["Message"] = m.Message,
                    ["Environment"] = m.Environment,
                };
            })
            .ToList();

        // UploadAsync batches + gzips internally and throws an aggregate on partial failure.
        await _ingestion
            .Value.UploadAsync(_options.DcrImmutableId, _options.StreamName, entries, cancellationToken: cancel)
            .ConfigureAwait(false);
    }

    public async IAsyncEnumerable<RetroMsg[]> GetMessages(
        RetroQuery query,
        [EnumeratorCancellation] CancellationToken cancel
    )
    {
        var kql = BuildKql(query, _options.TableName);

        var start = DateTime.SpecifyKind(query.StartDate, DateTimeKind.Utc);
        var end = DateTime.SpecifyKind(query.EndDate, DateTimeKind.Utc);

        Response<LogsQueryResult> response = await _query
            .Value.QueryWorkspaceAsync(
                _options.WorkspaceId,
                kql,
                new QueryTimeRange(start, end),
                cancellationToken: cancel
            )
            .ConfigureAwait(false);

        var table = response.Value.Table;

        // Honour the streaming contract the Mongo backend uses (batches of 250) so the
        // RetroSearchProcess / SignalR streaming UX is unchanged.
        const int batchSize = 250;
        List<RetroMsg> batch = new(batchSize);
        foreach (var row in table.Rows)
        {
            cancel.ThrowIfCancellationRequested();
            batch.Add(MapRow(row));
            if (batch.Count == batchSize)
            {
                yield return batch.ToArray();
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            yield return batch.ToArray();
        }
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"LogAnalytics:{name} is required for the Log Analytics Retro backend.");
        }
    }

    private static RetroMsg MapRow(LogsTableRow row)
    {
        return new RetroMsg
        {
            Level = row.GetInt32("Level") ?? 0,
            Date = row.GetDateTimeOffset("TimeGenerated")?.UtcDateTime ?? default,
            Machine = row.GetString("Machine") ?? "",
            Process = row.GetString("Process") ?? "",
            User = row.GetString("User") ?? "",
            Category = row.GetString("Category") ?? "",
            Message = row.GetString("Message") ?? "",
            // DiagnosticMsg carries no app-level id and Log Analytics rows have no addressable key,
            // so synthesise a unique id per row. MsgId (= RecordId.ToString()) only needs to be
            // unique within a result set for UI selection; delete is unsupported here anyway.
            RecordId = ObjectId.GenerateNewId(),
        };
    }

    /// <summary>
    ///     Builds the KQL for a Retro query. Public + static for unit testing. Filter patterns are
    ///     validated (length + compilable regex) and escaped into KQL string literals; the regex
    ///     filters mirror the Mongo backend's case-insensitive <c>Regex.IsMatch</c> via a (?i) prefix.
    /// </summary>
    public static string BuildKql(RetroQuery query, string tableName)
    {
        ValidateFilterPattern(query.Machine, nameof(query.Machine));
        ValidateFilterPattern(query.User, nameof(query.User));
        ValidateFilterPattern(query.Process, nameof(query.Process));
        ValidateFilterPattern(query.Message, nameof(query.Message));

        var start = Dt(query.StartDate);
        var end = Dt(query.EndDate);

        StringBuilder sb = new();
        sb.Append(tableName);
        // Half-open window to match the Mongo backend (Date >= start && Date < end).
        sb.Append($"\n| where TimeGenerated >= datetime({start}) and TimeGenerated < datetime({end})");
        sb.Append($"\n| where Level >= {query.MinLevel}");
        AppendRegexFilter(sb, "Machine", query.Machine);
        AppendRegexFilter(sb, "User", query.User);
        AppendRegexFilter(sb, "Process", query.Process);
        AppendRegexFilter(sb, "Message", query.Message);
        sb.Append($"\n| top {query.MaxRecords} by TimeGenerated desc");
        return sb.ToString();
    }

    private static void AppendRegexFilter(StringBuilder sb, string column, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return;
        }

        // (?i) = case-insensitive, matching the Mongo backend's RegexOptions.IgnoreCase.
        sb.Append($"\n| where {column} matches regex {KqlString("(?i)" + pattern)}");
    }

    private static string KqlString(string value)
    {
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string Dt(DateTime value)
    {
        return DateTime
            .SpecifyKind(value, DateTimeKind.Utc)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    }

    private static void ValidateFilterPattern(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (value.Length > MaxFilterPatternLength)
        {
            throw new ArgumentException($"{field} search pattern exceeds {MaxFilterPatternLength} characters");
        }

        try
        {
            _ = new Regex(value, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"{field} search is not a valid regular expression: {ex.Message}", ex);
        }

        if (Re2Incompatible.IsMatch(value))
        {
            throw new ArgumentException(
                $"{field} search uses a regular-expression construct (lookaround, atomic group, "
                    + "conditional or backreference) that the Log Analytics backend does not support. "
                    + "Rewrite the pattern without it."
            );
        }
    }
}
