using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Monitor.Ingestion;
using Azure.Monitor.Query;
using Diagnostic.Service.Common;
using Diagnostic.Service.Transport;
using DiagnosticExplorer;
using Xunit;

namespace DiagnosticService.UnitTests;

/// <summary>C3: real Azure SDK serialization and row decoding, with only the HTTP service substituted.</summary>
public sealed class LogAnalyticsWireContractTests
{
    private static readonly string[] Columns =
    [
        "TimeGenerated",
        "Level",
        "Machine",
        "Process",
        "User",
        "Category",
        "Message",
        "Environment",
    ];

    private sealed class FixtureCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("offline-fixture-token", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class WireService : HttpMessageHandler
    {
        public List<JsonElement> Uploaded { get; } = [];
        public List<string> UploadPaths { get; } = [];
        public string? QueryPath { get; private set; }
        public JsonElement QueryBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            request.Method.Should().Be(HttpMethod.Post);
            var uri = request.RequestUri ?? throw new InvalidOperationException("SDK request has no URI.");
            var content = request.Content ?? throw new InvalidOperationException("SDK request has no body.");
            var bytes = await content.ReadAsByteArrayAsync(cancellationToken);
            using var raw = new MemoryStream(bytes);
            using var gzip = content.Headers.ContentEncoding.Contains("gzip")
                ? new GZipStream(raw, CompressionMode.Decompress)
                : null;
            using var body = await JsonDocument.ParseAsync(
                gzip is null ? raw : gzip,
                cancellationToken: cancellationToken
            );
            if (uri.Host == "ingestion.example.test")
            {
                UploadPaths.Add(uri.AbsolutePath);
                Uploaded.AddRange(body.RootElement.EnumerateArray().Select(row => row.Clone()));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            uri.Host.Should().Be("api.loganalytics.io");
            QueryPath = uri.AbsolutePath;
            QueryBody = body.RootElement.Clone();
            // The substitute service uses the externally declared column schema. It does not call
            // the logger's mapper or construct RetroMsg: the real query SDK must decode these rows.
            var rows = Uploaded.Select(row => Columns.Select(column => row.GetProperty(column)).ToArray()).ToArray();
            var columns = Columns.Select(name => new
            {
                name,
                type = name switch
                {
                    "TimeGenerated" => "datetime",
                    "Level" => "int",
                    _ => "string",
                },
            });
            var json = JsonSerializer.Serialize(
                new
                {
                    tables = new[]
                    {
                        new
                        {
                            name = "PrimaryResult",
                            columns,
                            rows,
                        },
                    },
                }
            );
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(251)]
    public async Task Upload_and_query_preserve_every_schema_field_through_the_real_sdk(int count)
    {
        using var service = new WireService();
        using var http = new HttpClient(service);
        using var transport = new HttpClientTransport(http);
        var credential = new FixtureCredential();
        var ingestionOptions = new LogsIngestionClientOptions { Transport = transport };
        ingestionOptions.Retry.MaxRetries = 0;
        var queryOptions = new LogsQueryClientOptions { Transport = transport };
        queryOptions.Retry.MaxRetries = 0;
        var settings = new LogAnalyticsSettings
        {
            DceEndpoint = "https://ingestion.example.test",
            DcrImmutableId = "dcr-offline-fixture",
            StreamName = "Custom-DiagRetro_CL",
            TableName = "DiagRetro_CL",
            WorkspaceId = "11111111-1111-1111-1111-111111111111",
        };
        var logger = new LogAnalyticsRetroLogger(
            settings,
            new LogsIngestionClient(new Uri(settings.DceEndpoint), credential, ingestionOptions),
            new LogsQueryClient(credential, queryOptions)
        );
        var date = new DateTime(2026, 10, 11, 12, 13, 14, DateTimeKind.Utc);
        var messages = Enumerable
            .Range(0, count)
            .Select(index => new DiagnosticMsg
            {
                Date = date.AddSeconds(index),
                Level = 4,
                Machine = $"machine-{index}",
                Process = $"process-{index}",
                User = $"user-{index}",
                Category = $"category-{index}",
                Message = $"message-{index}",
                Environment = "offline-environment",
            })
            .ToArray();

        await logger.WriteMessages(messages, TestContext.Current.CancellationToken);

        service.Uploaded.Should().HaveCount(count);
        service
            .UploadPaths.Should()
            .OnlyContain(path => path == "/dataCollectionRules/dcr-offline-fixture/streams/Custom-DiagRetro_CL");
        for (var index = 0; index < count; index++)
        {
            var uploaded = service.Uploaded[index];
            uploaded.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(Columns);
            uploaded.GetProperty("TimeGenerated").GetDateTime().Should().Be(messages[index].Date);
            uploaded.GetProperty("Level").GetInt32().Should().Be(4);
            foreach (var field in new[] { "Machine", "Process", "User", "Category", "Message" })
            {
                uploaded.GetProperty(field).GetString().Should().Be($"{field.ToLowerInvariant()}-{index}");
            }
            uploaded.GetProperty("Environment").GetString().Should().Be("offline-environment");
        }
        var query = new RetroQuery
        {
            SearchId = 17,
            StartDate = date,
            EndDate = date.AddHours(1),
            MinLevel = 2,
            MaxRecords = count,
        };
        var batches = new List<RetroMsg[]>();
        await foreach (var batch in logger.GetMessages(query, TestContext.Current.CancellationToken))
        {
            batches.Add(batch);
        }

        service.QueryPath.Should().Be($"/v1/workspaces/{settings.WorkspaceId}/query");
        service
            .QueryBody.GetProperty("query")
            .GetString()
            .Should()
            .Contain("DiagRetro_CL")
            .And.Contain($"top {count} by TimeGenerated desc");
        batches.Select(batch => batch.Length).Should().Equal(count == 1 ? [1] : new[] { 250, 1 });
        var returned = batches.SelectMany(batch => batch).ToArray();
        returned.Should().HaveCount(count);
        returned.Select(message => message.MsgId).Should().OnlyHaveUniqueItems();
        for (var index = 0; index < count; index++)
        {
            returned[index]
                .Should()
                .BeEquivalentTo(
                    new
                    {
                        messages[index].Date,
                        messages[index].Level,
                        messages[index].Machine,
                        messages[index].Process,
                        messages[index].User,
                        messages[index].Category,
                        messages[index].Message,
                    }
                );
        }
    }
}
