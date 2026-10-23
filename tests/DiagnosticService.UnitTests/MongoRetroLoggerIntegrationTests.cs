using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Diagnostic.Service.Common;
using Diagnostic.Service.Transport;
using DiagnosticExplorer;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace DiagnosticService.UnitTests;

/// <summary>
///     Skips at discovery when <c>DIAG_SKIP_MONGO_INTEGRATION=1</c>. Stryker discovers the
///     suite once and then runs that set for every mutant, so a skip inside the test body
///     still pays the Mongo connection timeout on each mutant.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SkipOnMutationLaneFactAttribute : FactAttribute
{
    public SkipOnMutationLaneFactAttribute(
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = -1
    )
        : base(sourceFilePath, sourceLineNumber)
    {
        if (Environment.GetEnvironmentVariable("DIAG_SKIP_MONGO_INTEGRATION") == "1")
        {
            Skip =
                "Skipped on the Stryker lane (DIAG_SKIP_MONGO_INTEGRATION=1). "
                + "These tests run in the CI backend job, which provides MongoDB.";
        }
    }
}

/// <summary>
///     <see cref="MongoRetroLoggerTests" /> only covers validation that runs before any
///     driver call. These tests exercise the real driver against a real MongoDB instance — the
///     one the CI "backend" job runs as a service container on the driver's default port, and
///     the native mongod already running on this box locally (see the repo's
///     <c>local-docker-stack</c> project note) — so insertion, stored-field mapping,
///     duplicate-key tolerance, non-duplicate failure propagation, cancellation, and query
///     execution/ordering/limit/batching are proven against MongoDB itself, not a mocked filter
///     or FindOptions. Every test tags its own documents with a unique correlation id and cleans
///     them up via <see cref="MongoRetroLogger.Delete" /> so the suite does not depend on — or
///     pollute — the data in it. All of it runs in a throwaway database named by
///     <c>TestDatabaseNames</c>, never in the real <c>Diagnostics</c> database.
///     <para>
///     The weekly Stryker lane sets <c>DIAG_SKIP_MONGO_INTEGRATION=1</c>, and
///     <see cref="SkipOnMutationLaneFactAttribute" /> skips these tests at discovery. That
///     job has no MongoDB, and Stryker.NET 4.16 aborts the run when the initial pass reports
///     any failure: its Microsoft.Testing.Platform adapter records a complete run as
///     <c>EveryTest</c>, whose count is 0, so the failing-test ratio divides by zero.
///     A skip inside the body would still run once per mutant. Five connection attempts at
///     5 seconds, across the lane's 1004 mutants (~105 minutes at concurrency 4), is what
///     blew the 60-minute cap twice on 2026-09-27. With the skip at discovery the lane
///     measures 81 minutes, which is what the 90-minute cap now covers. Skipping at
///     discovery also avoids a race on the shared <c>Log</c> validator.
///     </para>
/// </summary>
public sealed class MongoRetroLoggerIntegrationTests : IClassFixture<MongoRetroLoggerIntegrationTests.DatabaseFixture>
{
    private const string ConnectionString = "mongodb://127.0.0.1:27017/?serverSelectionTimeoutMS=5000";

    // One throwaway database for the whole class: these tests install collection validators and
    // the mutation lane rewrites delete filters, neither of which may reach a real Diagnostics.Log.
    private static readonly string DatabaseName = TestDatabaseNames.New();

    /// <summary>Drops the class's throwaway database once every test in it has run.</summary>
    public sealed class DatabaseFixture : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await TestDatabaseNames.DropAsync(new MongoClient(ConnectionString), DatabaseName, CancellationToken.None);
        }
    }

    private static MongoRetroLogger CreateLogger()
    {
        return new MongoRetroLogger(ConnectionString, DatabaseName);
    }

    private static IMongoCollection<RetroMsg> GetRawCollection()
    {
        return new MongoClient(ConnectionString).GetDatabase(DatabaseName).GetCollection<RetroMsg>("Log");
    }

    private static string NewObjectId()
    {
        return ObjectId.GenerateNewId().ToString();
    }

    private static DiagnosticMsg NewMessage(
        string msgId,
        int level,
        DateTime date,
        string machine = "test-machine",
        string process = "test-process",
        string user = "test-user",
        string category = "test-category",
        string message = "test-message"
    )
    {
        return new DiagnosticMsg
        {
            MsgId = msgId,
            Level = level,
            Date = date,
            Machine = machine,
            Process = process,
            User = user,
            Category = category,
            Message = message,
            Environment = "test-env",
        };
    }

    /// <summary>
    ///     C1: a written message is stored with every field intact and readable back through the
    ///     real driver, not just accepted without error.
    /// </summary>
    [SkipOnMutationLaneFact]
    public async Task WriteMessages_InsertsDocument_WithAllFieldsIntact()
    {
        var id = NewObjectId();
        var date = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        var msg = NewMessage(
            id,
            level: 3,
            date,
            machine: "m-1",
            process: "p-1",
            user: "u-1",
            category: "c-1",
            message: "hello world"
        );
        MongoRetroLogger logger = CreateLogger();

        try
        {
            await logger.WriteMessages([msg], TestContext.Current.CancellationToken);

            var stored = await GetRawCollection()
                .Find(Builders<RetroMsg>.Filter.Eq(m => m.RecordId, ObjectId.Parse(id)))
                .SingleOrDefaultAsync(TestContext.Current.CancellationToken);

            stored.Should().NotBeNull();
            stored.MsgId.Should().Be(id);
            stored.Level.Should().Be(3);
            stored.Date.Should().Be(date);
            stored.Machine.Should().Be("m-1");
            stored.Process.Should().Be("p-1");
            stored.User.Should().Be("u-1");
            stored.Category.Should().Be("c-1");
            stored.Message.Should().Be("hello world");
        }
        finally
        {
            await logger.Delete([id]);
        }
    }

    /// <summary>
    ///     C1: a batch whose only write errors are duplicate-key must be tolerated as a no-op,
    ///     leaving the existing document untouched.
    /// </summary>
    [SkipOnMutationLaneFact]
    public async Task WriteMessages_DuplicateKeyOnlyBatch_IsTolerated()
    {
        var id = NewObjectId();
        var date = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        var original = NewMessage(id, level: 1, date, message: "original");
        var duplicate = NewMessage(id, level: 9, date, message: "duplicate-should-not-apply");
        MongoRetroLogger logger = CreateLogger();

        try
        {
            await logger.WriteMessages([original], TestContext.Current.CancellationToken);

            Func<Task> act = () => logger.WriteMessages([duplicate], TestContext.Current.CancellationToken);

            await act.Should().NotThrowAsync("a duplicate-only batch must be tolerated, not surfaced as a failure");

            var stored = await GetRawCollection()
                .Find(Builders<RetroMsg>.Filter.Eq(m => m.RecordId, ObjectId.Parse(id)))
                .SingleOrDefaultAsync(TestContext.Current.CancellationToken);
            stored.Should().NotBeNull();
            stored.Message.Should().Be("original", "the duplicate-only batch must not overwrite the existing document");
            (
                await GetRawCollection()
                    .CountDocumentsAsync(
                        Builders<RetroMsg>.Filter.Eq(m => m.RecordId, ObjectId.Parse(id)),
                        cancellationToken: TestContext.Current.CancellationToken
                    )
            )
                .Should()
                .Be(1);
        }
        finally
        {
            await logger.Delete([id]);
        }
    }

    /// <summary>
    ///     C1: a genuine (non-duplicate-key) write failure must propagate rather than being
    ///     swallowed by the duplicate-key tolerance in <see cref="MongoRetroLogger.WriteMessages" />.
    ///     Forced with a temporary server-side <c>$jsonSchema</c> validator requiring a field the
    ///     logger's mapped document never contains, so the server always rejects the write with a
    ///     <see cref="MongoBulkWriteException{TDocument}" /> whose <c>WriteErrors</c> category is
    ///     <see cref="ServerErrorCategory.Uncategorized" /> (DocumentValidationFailure) — not
    ///     DuplicateKey. A too-large document is not used here: the driver rejects it client-side
    ///     during BSON serialization before any request reaches the server, so it never reaches
    ///     <see cref="MongoRetroLogger" />'s catch block at all and would not red-proof this path.
    /// </summary>
    [SkipOnMutationLaneFact]
    public async Task WriteMessages_NonDuplicateWriteFailure_Propagates()
    {
        var id = NewObjectId();
        var msg = NewMessage(id, level: 1, DateTime.UtcNow);
        MongoRetroLogger logger = CreateLogger();
        IMongoDatabase database = new MongoClient(ConnectionString).GetDatabase(DatabaseName);

        await using var validator = await ValidatorScope.InstallAsync(
            database,
            new BsonDocument(
                "$jsonSchema",
                new BsonDocument
                {
                    { "bsonType", "object" },
                    {
                        "required",
                        new BsonArray { "FieldNoDocumentEverHas" }
                    },
                }
            )
        );

        try
        {
            Func<Task> act = () => logger.WriteMessages([msg], TestContext.Current.CancellationToken);

            var thrown = await act.Should()
                .ThrowAsync<MongoBulkWriteException<DiagnosticMsg>>(
                    "a non-duplicate-key write failure must not be swallowed"
                );
            thrown.Which.WriteErrors.Should().OnlyContain(e => e.Category != ServerErrorCategory.DuplicateKey);
        }
        finally
        {
            await logger.Delete([id]);
        }
    }

    /// <summary>
    ///     C1: the insert is unordered, so a duplicate that comes FIRST must not stop the valid
    ///     document behind it from being stored. With an ordered insert the batch halts at the
    ///     duplicate and the second document is lost while the duplicate-only tolerance still
    ///     reports success. Every earlier write test submitted one document, so neither the
    ///     ordering nor the position of the duplicate was exercised.
    /// </summary>
    [SkipOnMutationLaneFact]
    public async Task WriteMessages_DuplicateFirstInABatch_StillStoresTheValidDocumentBehindIt()
    {
        var existingId = NewObjectId();
        var freshId = NewObjectId();
        var date = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        MongoRetroLogger logger = CreateLogger();

        try
        {
            await logger.WriteMessages(
                [NewMessage(existingId, level: 1, date, message: "original")],
                TestContext.Current.CancellationToken
            );

            await logger.WriteMessages(
                [
                    NewMessage(existingId, level: 9, date, message: "duplicate"),
                    NewMessage(freshId, level: 2, date, message: "fresh"),
                ],
                TestContext.Current.CancellationToken
            );

            var fresh = await GetRawCollection()
                .Find(Builders<RetroMsg>.Filter.Eq(m => m.RecordId, ObjectId.Parse(freshId)))
                .SingleOrDefaultAsync(TestContext.Current.CancellationToken);
            fresh.Should().NotBeNull("the valid document behind a duplicate must still be inserted");
            fresh.Message.Should().Be("fresh");
        }
        finally
        {
            await logger.Delete([existingId, freshId]);
        }
    }

    /// <summary>
    ///     C1: a batch whose write errors are a duplicate key AND a genuine failure must surface
    ///     the failure. Tolerating a batch when ANY error is a duplicate, instead of when ALL
    ///     are, would swallow the rejected document. The genuine failure is forced with a
    ///     query-expression validator that rejects one message by text and still passes the
    ///     duplicate document, so the server reports one DuplicateKey error and one validation
    ///     failure in the same batch; a duplicate plus a valid document cannot tell the two
    ///     predicates apart.
    /// </summary>
    [SkipOnMutationLaneFact]
    public async Task WriteMessages_DuplicatePlusRejectedDocument_Propagates()
    {
        var existingId = NewObjectId();
        var rejectedId = NewObjectId();
        var date = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        MongoRetroLogger logger = CreateLogger();
        IMongoDatabase database = new MongoClient(ConnectionString).GetDatabase(DatabaseName);

        await using var validator = await ValidatorScope.InstallAsync(
            database,
            new BsonDocument("Message", new BsonDocument("$ne", "reject-me"))
        );

        try
        {
            await logger.WriteMessages(
                [NewMessage(existingId, level: 1, date, message: "original")],
                TestContext.Current.CancellationToken
            );

            Func<Task> act = () =>
                logger.WriteMessages(
                    [
                        NewMessage(existingId, level: 9, date, message: "duplicate"),
                        NewMessage(rejectedId, level: 2, date, message: "reject-me"),
                    ],
                    TestContext.Current.CancellationToken
                );

            var thrown = await act.Should()
                .ThrowAsync<MongoBulkWriteException<DiagnosticMsg>>(
                    "a batch holding a non-duplicate failure must not be tolerated because one error is a duplicate"
                );
            thrown
                .Which.WriteErrors.Select(e => e.Category)
                .Should()
                .Contain(ServerErrorCategory.DuplicateKey)
                .And.Contain(c => c != ServerErrorCategory.DuplicateKey);
        }
        finally
        {
            await logger.Delete([existingId, rejectedId]);
        }
    }

    /// <summary>C1: the environment a message was logged from is stored, not just accepted.</summary>
    [SkipOnMutationLaneFact]
    public async Task WriteMessages_StoresTheEnvironment()
    {
        var id = NewObjectId();
        MongoRetroLogger logger = CreateLogger();

        try
        {
            await logger.WriteMessages(
                [NewMessage(id, level: 1, new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc))],
                TestContext.Current.CancellationToken
            );

            var stored = await new MongoClient(ConnectionString)
                .GetDatabase(DatabaseName)
                .GetCollection<BsonDocument>("Log")
                .Find(Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(id)))
                .SingleOrDefaultAsync(TestContext.Current.CancellationToken);
            stored.Should().NotBeNull();
            stored["Environment"].AsString.Should().Be("test-env");
        }
        finally
        {
            await logger.Delete([id]);
        }
    }

    /// <summary>
    ///     A logger given its own database and collection writes there and nowhere else, which is
    ///     what lets every other test in this class run (including the ones that install a
    ///     validator or delete without a narrow filter) without touching <c>Diagnostics.Log</c>.
    /// </summary>
    [SkipOnMutationLaneFact]
    public async Task WriteMessages_UsesTheConfiguredDatabaseAndCollection()
    {
        var id = NewObjectId();
        var databaseName = TestDatabaseNames.New();
        var client = new MongoClient(ConnectionString);
        MongoRetroLogger logger = new(ConnectionString, databaseName, "IsolatedLog");

        try
        {
            await logger.WriteMessages(
                [NewMessage(id, level: 1, new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc))],
                TestContext.Current.CancellationToken
            );

            var filter = Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(id));
            var isolated = await client
                .GetDatabase(databaseName)
                .GetCollection<BsonDocument>("IsolatedLog")
                .CountDocumentsAsync(filter, cancellationToken: TestContext.Current.CancellationToken);
            var shared = await client
                .GetDatabase("Diagnostics")
                .GetCollection<BsonDocument>("Log")
                .CountDocumentsAsync(filter, cancellationToken: TestContext.Current.CancellationToken);

            isolated.Should().Be(1, "the message belongs in the collection the logger was configured with");
            shared
                .Should()
                .Be(
                    0,
                    $"a configured logger must never write to the production Diagnostics.Log (stray row _id {id} needs removing by hand)"
                );
        }
        finally
        {
            // Deliberately no cleanup against Diagnostics.Log: this test must never write to or
            // delete from it, even to tidy up after its own failure.
            await TestDatabaseNames.DropAsync(client, databaseName, CancellationToken.None);
        }
    }

    /// <summary>
    ///     Test databases are always named with this prefix and are dropped by name only after the
    ///     prefix is checked, so a mistyped name can never drop a real database.
    /// </summary>
    private static class TestDatabaseNames
    {
        private const string Prefix = "Diagnostics_it_";

        internal static string New()
        {
            return Prefix + Guid.NewGuid().ToString("N");
        }

        internal static Task DropAsync(MongoClient client, string databaseName, CancellationToken cancellationToken)
        {
            if (!databaseName.StartsWith(Prefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Refusing to drop '{databaseName}': not a test database.");
            }

            return client.DropDatabaseAsync(databaseName, cancellationToken);
        }
    }

    /// <summary>
    ///     Installs a server-side validator on the <c>Log</c> collection of this class's throwaway
    ///     database and clears it on dispose. collMod needs the collection to exist, and on a fresh mongod (as
    ///     CI's service container is) nothing has written to it yet if the calling test runs
    ///     first, so it is created defensively; NamespaceExists (code 48) means a concurrent
    ///     write beat us to it, which is fine. Only tests that use this scope touch the
    ///     validator, and a class's tests run sequentially, so there is no race for the window.
    /// </summary>
    private sealed class ValidatorScope : IAsyncDisposable
    {
        private readonly IMongoDatabase _database;

        private ValidatorScope(IMongoDatabase database)
        {
            _database = database;
        }

        internal static async Task<ValidatorScope> InstallAsync(IMongoDatabase database, BsonDocument validator)
        {
            try
            {
                await database.CreateCollectionAsync("Log", cancellationToken: TestContext.Current.CancellationToken);
            }
            catch (MongoCommandException ex) when (ex.Code == 48)
            {
                // NamespaceExists: already created by an earlier test in this run.
            }

            await database.RunCommandAsync<BsonDocument>(
                new BsonDocument
                {
                    { "collMod", "Log" },
                    { "validator", validator },
                    { "validationAction", "error" },
                },
                cancellationToken: TestContext.Current.CancellationToken
            );
            return new ValidatorScope(database);
        }

        public async ValueTask DisposeAsync()
        {
            await _database.RunCommandAsync<BsonDocument>(
                new BsonDocument { { "collMod", "Log" }, { "validator", new BsonDocument() } },
                cancellationToken: TestContext.Current.CancellationToken
            );
        }
    }

    /// <summary>C1: an already-cancelled token must abort the write rather than silently insert.</summary>
    [SkipOnMutationLaneFact]
    public async Task WriteMessages_WithCancelledToken_ThrowsOperationCanceled()
    {
        var id = NewObjectId();
        var msg = NewMessage(id, level: 1, DateTime.UtcNow);
        MongoRetroLogger logger = CreateLogger();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        try
        {
            Func<Task> act = () => logger.WriteMessages([msg], cancelled.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();

            (
                await GetRawCollection()
                    .CountDocumentsAsync(
                        Builders<RetroMsg>.Filter.Eq(m => m.RecordId, ObjectId.Parse(id)),
                        cancellationToken: TestContext.Current.CancellationToken
                    )
            )
                .Should()
                .Be(0, "a cancelled write must not have inserted the document");
        }
        finally
        {
            await logger.Delete([id]);
        }
    }

    /// <summary>
    ///     C2: date/level filtering, descending sort and MaxRecords capping, proven against real
    ///     query execution rather than rendered filters. Filter edges and multi-batch enumeration
    ///     are pinned by the two tests that follow the sort/limit one.
    /// </summary>
    [SkipOnMutationLaneFact]
    public async Task GetMessages_FiltersOrdersAndLimits()
    {
        var runTag = Guid.NewGuid().ToString("N");
        var windowStart = new DateTime(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc);
        var windowEnd = new DateTime(2026, 9, 25, 11, 0, 0, DateTimeKind.Utc);

        // Deliberately non-chronological insertion order, mixed levels, and records both inside
        // and outside the date range / below the level threshold.
        (string Id, int Level, DateTime Date)[] seeds =
        [
            (NewObjectId(), 5, windowStart.AddMinutes(30)), // in-range, above threshold
            (NewObjectId(), 5, windowStart.AddMinutes(10)), // in-range, above threshold, earlier
            (NewObjectId(), 5, windowStart.AddMinutes(50)), // in-range, above threshold, latest
            (NewObjectId(), 1, windowStart.AddMinutes(20)), // in-range but below MinLevel
            (NewObjectId(), 5, windowStart.AddMinutes(-10)), // before the window
            (NewObjectId(), 5, windowEnd.AddMinutes(10)), // after the window (End is exclusive)
        ];

        MongoRetroLogger logger = CreateLogger();
        var allIds = seeds.Select(s => s.Id).ToArray();

        try
        {
            foreach (var seed in seeds)
            {
                await logger.WriteMessages(
                    [NewMessage(seed.Id, seed.Level, seed.Date, process: runTag)],
                    TestContext.Current.CancellationToken
                );
            }

            RetroQuery query = new()
            {
                SearchId = 1,
                MinLevel = 2,
                StartDate = windowStart,
                EndDate = windowEnd,
                MaxRecords = 2, // fewer than the 3 qualifying records, to exercise the cap
                Process = runTag,
            };

            List<RetroMsg> collected = [];
            await foreach (RetroMsg[] batch in logger.GetMessages(query, TestContext.Current.CancellationToken))
            {
                collected.AddRange(batch);
            }

            collected.Should().HaveCount(2, "MaxRecords must cap the result even though 3 records qualify");
            collected.Select(m => m.MsgId).Should().OnlyHaveUniqueItems();
            collected.Select(m => m.Level).Should().OnlyContain(level => level >= query.MinLevel);
            collected.Select(m => m.Date).Should().OnlyContain(date => date >= windowStart && date < windowEnd);
            collected.Select(m => m.Date).Should().BeInDescendingOrder("GetMessages sorts newest-first");
            // The two newest qualifying records (at +50 and +30 minutes) must win the cap over the
            // earliest qualifying one (+10 minutes).
            collected.Select(m => m.Date).Should().Equal(windowStart.AddMinutes(50), windowStart.AddMinutes(30));
        }
        finally
        {
            await logger.Delete(allIds);
        }
    }

    private static async Task<(List<RetroMsg> Rows, int Batches)> ReadAll(
        MongoRetroLogger logger,
        RetroQuery query,
        CancellationToken cancel
    )
    {
        List<RetroMsg> rows = [];
        var batches = 0;
        await foreach (RetroMsg[] batch in logger.GetMessages(query, cancel))
        {
            batches++;
            rows.AddRange(batch);
        }

        return (rows, batches);
    }

    /// <summary>
    ///     baseline:C2: the filter edges. MinLevel and StartDate are inclusive, EndDate is
    ///     exclusive, and a MaxRecords larger than the qualifying count returns every qualifying
    ///     row (and nothing outside the filters).
    /// </summary>
    [SkipOnMutationLaneFact]
    public async Task GetMessages_FilterBoundaries_AreInclusiveStartAndMinLevel_ExclusiveEnd()
    {
        var runTag = Guid.NewGuid().ToString("N");
        var start = new DateTime(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 9, 25, 11, 0, 0, DateTimeKind.Utc);
        (string Id, int Level, DateTime Date, string Name)[] seeds =
        [
            (NewObjectId(), 2, start, "at-start-at-minlevel"), // both edges inclusive: returned
            (NewObjectId(), 2, end.AddSeconds(-1), "just-before-end"), // returned
            (NewObjectId(), 2, end, "at-end"), // End is exclusive
            (NewObjectId(), 2, start.AddSeconds(-1), "just-before-start"), // before the window
            (NewObjectId(), 1, start.AddMinutes(5), "below-minlevel"), // one under MinLevel
        ];
        MongoRetroLogger logger = CreateLogger();
        var ids = seeds.Select(s => s.Id).ToArray();

        try
        {
            await logger.WriteMessages(
                [.. seeds.Select(s => NewMessage(s.Id, s.Level, s.Date, process: runTag, message: s.Name))],
                TestContext.Current.CancellationToken
            );

            (List<RetroMsg> rows, _) = await ReadAll(
                logger,
                new RetroQuery
                {
                    SearchId = 1,
                    MinLevel = 2,
                    StartDate = start,
                    EndDate = end,
                    MaxRecords = 100,
                    Process = runTag,
                },
                TestContext.Current.CancellationToken
            );

            rows.Select(r => r.Message)
                .Should()
                .Equal(
                    ["just-before-end", "at-start-at-minlevel"],
                    "newest first, and only the inclusive/exclusive edges"
                );
        }
        finally
        {
            await logger.Delete(ids);
        }
    }

    /// <summary>
    ///     baseline:C2: more rows than the 250-row driver batch come back complete, across more
    ///     than one batch, in newest-first order.
    /// </summary>
    [SkipOnMutationLaneFact]
    public async Task GetMessages_MoreRowsThanOneBatch_AreAllEnumeratedAcrossBatches()
    {
        const int total = 300; // > BatchSize (250)
        var runTag = Guid.NewGuid().ToString("N");
        var start = new DateTime(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc);
        var seeds = Enumerable
            .Range(0, total)
            .Select(i => NewMessage(NewObjectId(), level: 3, start.AddSeconds(i), process: runTag))
            .ToArray();
        MongoRetroLogger logger = CreateLogger();

        try
        {
            await logger.WriteMessages(seeds, TestContext.Current.CancellationToken);

            (List<RetroMsg> rows, int batches) = await ReadAll(
                logger,
                new RetroQuery
                {
                    SearchId = 1,
                    MinLevel = 0,
                    StartDate = start,
                    EndDate = start.AddHours(1),
                    MaxRecords = 1000,
                    Process = runTag,
                },
                TestContext.Current.CancellationToken
            );

            rows.Should().HaveCount(total);
            rows.Select(r => r.MsgId).Should().OnlyHaveUniqueItems();
            rows.Select(r => r.Date).Should().BeInDescendingOrder();
            batches.Should().BeGreaterThan(1, "300 rows exceed the 250-row batch size");
        }
        finally
        {
            await logger.Delete(seeds.Select(m => m.MsgId).ToArray());
        }
    }

    /// <summary>
    ///     C7: <see cref="MongoRetroLogger.Delete" /> removes exactly the named records, skips a
    ///     malformed id rather than throwing, reports the true count, and leaves unrelated rows.
    /// </summary>
    [SkipOnMutationLaneFact]
    public async Task Delete_RemovesOnlyNamedRecords_SkipsMalformedIds_AndReportsCount()
    {
        var runTag = Guid.NewGuid().ToString("N");
        var date = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        var doomedA = NewObjectId();
        var doomedB = NewObjectId();
        var survivor = NewObjectId();
        MongoRetroLogger logger = CreateLogger();

        try
        {
            await logger.WriteMessages(
                [
                    NewMessage(doomedA, 1, date, process: runTag, message: "a"),
                    NewMessage(doomedB, 1, date.AddSeconds(1), process: runTag, message: "b"),
                    NewMessage(survivor, 1, date.AddSeconds(2), process: runTag, message: "survivor"),
                ],
                TestContext.Current.CancellationToken
            );

            var deleted = await logger.Delete([doomedA, "not-an-object-id", doomedB, NewObjectId()]);

            deleted.Should().Be(2, "only the two existing, well-formed ids were removed");
            (List<RetroMsg> rows, _) = await ReadAll(
                logger,
                new RetroQuery
                {
                    SearchId = 1,
                    MinLevel = 0,
                    StartDate = date.AddHours(-1),
                    EndDate = date.AddHours(1),
                    MaxRecords = 100,
                    Process = runTag,
                },
                TestContext.Current.CancellationToken
            );
            rows.Select(r => r.Message).Should().Equal("survivor");
        }
        finally
        {
            await logger.Delete([doomedA, doomedB, survivor]);
        }
    }
}
