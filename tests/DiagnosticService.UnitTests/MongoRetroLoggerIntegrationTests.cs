using AwesomeAssertions;
using Diagnostic.Service.Common;
using Diagnostic.Service.Transport;
using DiagnosticExplorer;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace DiagnosticService.UnitTests;

/// <summary>
///     C1/C2: <see cref="MongoRetroLoggerTests" /> only covers validation that runs before any
///     driver call. These tests exercise the real driver against a real MongoDB instance — the
///     one the CI "backend" job runs as a service container on the driver's default port, and
///     the native mongod already running on this box locally (see the repo's
///     <c>local-docker-stack</c> project note) — so insertion, stored-field mapping,
///     duplicate-key tolerance, non-duplicate failure propagation, cancellation, and query
///     execution/ordering/limit/batching are proven against MongoDB itself, not a mocked filter
///     or FindOptions. Every test tags its own documents with a unique correlation id and cleans
///     them up via <see cref="MongoRetroLogger.Delete" /> so the suite does not depend on — or
///     pollute — a dedicated database. (C1, C2)
/// </summary>
public sealed class MongoRetroLoggerIntegrationTests
{
    private const string ConnectionString = "mongodb://127.0.0.1:27017/?serverSelectionTimeoutMS=5000";

    private static MongoRetroLogger CreateLogger()
    {
        return new MongoRetroLogger(ConnectionString);
    }

    private static IMongoCollection<RetroMsg> GetRawCollection()
    {
        return new MongoClient(ConnectionString).GetDatabase("Diagnostics").GetCollection<RetroMsg>("Log");
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
    [Fact]
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
    [Fact]
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
    [Fact]
    public async Task WriteMessages_NonDuplicateWriteFailure_Propagates()
    {
        var id = NewObjectId();
        var msg = NewMessage(id, level: 1, DateTime.UtcNow);
        MongoRetroLogger logger = CreateLogger();
        IMongoDatabase database = new MongoClient(ConnectionString).GetDatabase("Diagnostics");

        await database.RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                { "collMod", "Log" },
                {
                    "validator",
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
                },
                { "validationAction", "error" },
            },
            cancellationToken: TestContext.Current.CancellationToken
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
            await database.RunCommandAsync<BsonDocument>(
                new BsonDocument { { "collMod", "Log" }, { "validator", new BsonDocument() } },
                cancellationToken: TestContext.Current.CancellationToken
            );
            await logger.Delete([id]);
        }
    }

    /// <summary>C1: an already-cancelled token must abort the write rather than silently insert.</summary>
    [Fact]
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
    ///     C2: date/level filtering, descending sort, MaxRecords capping and full batch
    ///     enumeration, proven against real query execution rather than rendered filters.
    /// </summary>
    [Fact]
    public async Task GetMessages_FiltersOrdersLimitsAndEnumeratesAllBatches()
    {
        var runTag = Guid.NewGuid().ToString("N");
        var windowStart = new DateTime(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc);
        var windowEnd = new DateTime(2026, 9, 25, 11, 0, 0, DateTimeKind.Utc);

        // Deliberately non-chronological insertion order, mixed levels, and records both inside
        // and outside the date range / below the level threshold.
        (string Id, int Level, DateTime Date, bool ExpectedInResult)[] seeds =
        [
            (NewObjectId(), 5, windowStart.AddMinutes(30), true), // in-range, above threshold
            (NewObjectId(), 5, windowStart.AddMinutes(10), true), // in-range, above threshold, earlier
            (NewObjectId(), 5, windowStart.AddMinutes(50), true), // in-range, above threshold, latest
            (NewObjectId(), 1, windowStart.AddMinutes(20), false), // in-range but below MinLevel
            (NewObjectId(), 5, windowStart.AddMinutes(-10), false), // before the window
            (NewObjectId(), 5, windowEnd.AddMinutes(10), false), // after the window (End is exclusive)
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
            await foreach (
                RetroMsg[] batch in logger
                    .GetMessages(query, TestContext.Current.CancellationToken)
                    .WithCancellation(TestContext.Current.CancellationToken)
            )
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
}
