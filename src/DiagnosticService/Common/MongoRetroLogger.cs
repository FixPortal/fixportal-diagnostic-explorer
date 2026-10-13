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

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Diagnostic.Service.Transport;
using DiagnosticExplorer;
using log4net;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

namespace Diagnostic.Service.Common;

public class MongoRetroLogger : IRetroLogger
{
    // Client-controlled filter patterns are run as server-side $regex; bound their length and
    // the per-query server time so a crafted catastrophic regex can't peg a Mongo core.
    private const int MaxFilterPatternLength = 256;
    private const int MaxDeleteBatch = 10_000;
    private static readonly ILog _log = LogManager.GetLogger(typeof(MongoRetroLogger));
    private static readonly TimeSpan QueryMaxTime = TimeSpan.FromSeconds(30);

    private readonly Lazy<MongoClient> _client;
    private readonly string _databaseName;
    private readonly string _collectionName;

    static MongoRetroLogger()
    {
        BsonClassMap<DiagnosticMsg> map = new();
        map.MapIdProperty(nameof(DiagnosticMsg.MsgId)).SetSerializer(new StringSerializer(BsonType.ObjectId));
        map.MapProperty(nameof(DiagnosticMsg.Category));
        map.MapProperty(nameof(DiagnosticMsg.Date));
        map.MapProperty(nameof(DiagnosticMsg.Level));
        map.MapProperty(nameof(DiagnosticMsg.Machine));
        map.MapProperty(nameof(DiagnosticMsg.Message));
        map.MapProperty(nameof(DiagnosticMsg.Process));
        map.MapProperty(nameof(DiagnosticMsg.User));
        map.MapProperty(nameof(DiagnosticMsg.Environment));
        map.SetIgnoreExtraElements(true);
        BsonClassMap.RegisterClassMap(map);

        BsonClassMap<RetroMsg> map2 = new();
        map2.MapIdProperty(nameof(RetroMsg.RecordId));
        map2.MapProperty(nameof(RetroMsg.Category));
        map2.MapProperty(nameof(RetroMsg.Date));
        map2.MapProperty(nameof(RetroMsg.Level));
        map2.MapProperty(nameof(RetroMsg.Machine));
        map2.MapProperty(nameof(RetroMsg.Message));
        map2.MapProperty(nameof(RetroMsg.Process));
        map2.MapProperty(nameof(RetroMsg.User));
        map2.SetIgnoreExtraElements(true);
        BsonClassMap.RegisterClassMap(map2);

        BsonClassMap<DeleteMsg> map3 = new();
        map3.MapIdProperty(nameof(DeleteMsg.RecordId));
        map3.SetIgnoreExtraElements(true);
        BsonClassMap.RegisterClassMap(map3);
    }

    /// <param name="connectionString">MongoDB connection string.</param>
    /// <param name="databaseName">Database holding the log. Production uses the default; tests pass their own so they never touch it.</param>
    /// <param name="collectionName">Collection holding the log. Production uses the default.</param>
    public MongoRetroLogger(string connectionString, string databaseName = "Diagnostics", string collectionName = "Log")
    {
        ConnectionString = connectionString;
        _databaseName = databaseName;
        _collectionName = collectionName;
        // MongoClient is meant to be a long-lived singleton (it owns a connection pool and
        // topology-monitoring threads); cache one and reuse it across all operations rather than
        // constructing a fresh one per call.
        _client = new Lazy<MongoClient>(() => new MongoClient(ConnectionString));

        // The retro query filters a Date range and sorts by Date descending. Without a Date
        // index that is a full collection scan, so on any non-trivial Log collection every
        // query trips the 30s MaxTime budget and the UI shows an empty "No events" result.
        // Ensure the index exists. Fire-and-forget: the initial build on a large existing
        // collection can take a long time, and it must not block construction or queries
        // (which keep working — if slowly — until the build completes). CreateOne is a no-op
        // when the index already exists, so this is safe to run on every startup.
        _ = Task.Run(EnsureIndexesAsync);
    }

    public string ConnectionString { get; set; }

    /// <summary>MongoDB supports interactive per-record delete (see <see cref="Delete" />).</summary>
    public bool SupportsDelete => true;

    public async Task<long> Delete(string[] idList)
    {
        if (idList == null || idList.Length == 0)
        {
            return 0;
        }

        if (idList.Length > MaxDeleteBatch)
        {
            throw new ArgumentException($"Delete batch of {idList.Length} exceeds the limit of {MaxDeleteBatch}");
        }

        // TryParse: a single malformed id previously threw an unhandled FormatException out of
        // the async method. Skip invalid ids instead.
        List<ObjectId> ids = new(idList.Length);
        foreach (var s in idList)
        {
            if (ObjectId.TryParse(s, out var id))
            {
                ids.Add(id);
            }
        }

        if (ids.Count == 0)
        {
            return 0;
        }

        var collection = GetLogCollection<RetroMsg>();

        FilterDefinition<RetroMsg> filter = new ExpressionFilterDefinition<RetroMsg>(msg => ids.Contains(msg.RecordId));

        var result = await collection.DeleteManyAsync(filter, CancellationToken.None).ConfigureAwait(false);

        return result.DeletedCount;
    }

    [SuppressMessage(
        "Security",
        "S6444:Regular expressions should specify a match timeout",
        Justification = "These expression trees are translated to MongoDB regex filters and bounded server-side by FindOptions.MaxTime."
    )]
    public async IAsyncEnumerable<RetroMsg[]> GetMessages(
        RetroQuery query,
        [EnumeratorCancellation] CancellationToken cancel
    )
    {
        ValidateFilterPattern(query.Machine, nameof(query.Machine));
        ValidateFilterPattern(query.User, nameof(query.User));
        ValidateFilterPattern(query.Process, nameof(query.Process));
        ValidateFilterPattern(query.Message, nameof(query.Message));

        var collection = GetLogCollection<RetroMsg>();

        FindOptions<RetroMsg> options = new()
        {
            Limit = query.MaxRecords,
            BatchSize = 250,
            // Server-side time budget: aborts a query (incl. a catastrophic $regex) on the Mongo
            // side rather than letting it run unbounded.
            MaxTime = QueryMaxTime,
            Sort = Builders<RetroMsg>.Sort.Descending(msg => msg.Date),
        };

        FilterDefinition<RetroMsg> filter = new ExpressionFilterDefinition<RetroMsg>(msg =>
            msg.Level >= query.MinLevel && msg.Date >= query.StartDate && msg.Date < query.EndDate
        );

        if (!string.IsNullOrWhiteSpace(query.Machine))
        {
            filter &= new ExpressionFilterDefinition<RetroMsg>(msg =>
                Regex.IsMatch(msg.Machine, query.Machine, RegexOptions.IgnoreCase)
            );
        }

        if (!string.IsNullOrWhiteSpace(query.User))
        {
            filter &= new ExpressionFilterDefinition<RetroMsg>(msg =>
                Regex.IsMatch(msg.User, query.User, RegexOptions.IgnoreCase)
            );
        }

        if (!string.IsNullOrWhiteSpace(query.Process))
        {
            filter &= new ExpressionFilterDefinition<RetroMsg>(msg =>
                Regex.IsMatch(msg.Process, query.Process, RegexOptions.IgnoreCase)
            );
        }

        if (!string.IsNullOrWhiteSpace(query.Message))
        {
            filter &= new ExpressionFilterDefinition<RetroMsg>(msg =>
                Regex.IsMatch(msg.Message, query.Message, RegexOptions.IgnoreCase)
            );
        }

        using var searchResult = await collection.FindAsync(filter, options, cancel).ConfigureAwait(false);

        while (await searchResult.MoveNextAsync(cancel))
        {
            yield return searchResult.Current.ToArray();
        }
    }

    public async Task WriteMessages(ICollection<DiagnosticMsg> msg, CancellationToken cancel)
    {
        var collection = GetLogCollection<DiagnosticMsg>();
        try
        {
            await collection.InsertManyAsync(msg, new InsertManyOptions { IsOrdered = false }, cancel);
        }
        catch (MongoBulkWriteException ex)
        {
            if (ex.WriteErrors.All(e => e.Category == ServerErrorCategory.DuplicateKey))
            {
                return;
            }

            throw;
        }
    }

    private IMongoCollection<T> GetLogCollection<T>()
    {
        return _client.Value.GetDatabase(_databaseName).GetCollection<T>(_collectionName);
    }

    private async Task EnsureIndexesAsync()
    {
        try
        {
            var collection = GetLogCollection<RetroMsg>();
            CreateIndexModel<RetroMsg> dateIndex = new(
                Builders<RetroMsg>.IndexKeys.Descending(msg => msg.Date),
                new CreateIndexOptions { Name = "Date_-1" }
            );
            await collection.Indexes.CreateOneAsync(dateIndex).ConfigureAwait(false);
            _log.InfoFormat("Ensured {0}.{1} Date index for retro queries", _databaseName, _collectionName);
        }
        catch (Exception ex)
        {
            // Never let index maintenance break logging/queries; the feature still functions
            // (just unindexed) and a later restart will retry.
            _log.Warn(string.Concat("Failed to ensure ", _databaseName, ".", _collectionName, " Date index"), ex);
        }
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
    }
}
