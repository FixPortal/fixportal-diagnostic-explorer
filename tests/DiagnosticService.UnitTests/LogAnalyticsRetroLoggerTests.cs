using AwesomeAssertions;
using Diagnostic.Service.Common;
using Diagnostic.Service.Transport;
using Xunit;

namespace DiagnosticService.UnitTests;

/// <summary>
///     Covers the Log Analytics Retro backend's KQL generation, config validation, and the
///     delete-not-supported contract. LogAnalyticsWireContractTests exercises actual SDK wire
///     serialization and row decoding offline. The optional live integration test separately
///     qualifies workspace deployment, permissions and service behavior (spec §11).
/// </summary>
public class LogAnalyticsRetroLoggerTests
{
    private static RetroQuery BaseQuery()
    {
        return new RetroQuery
        {
            SearchId = 1,
            MaxRecords = 100,
            MinLevel = 2,
            StartDate = new DateTime(2026, 6, 7, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc),
        };
    }

    private static LogAnalyticsSettings ValidOptions()
    {
        return new LogAnalyticsSettings
        {
            DceEndpoint = "https://dce-diag-dev.eastus2-1.ingest.monitor.azure.com",
            DcrImmutableId = "dcr-0123456789abcdef0123456789abcdef",
            StreamName = "Custom-DiagRetro_CL",
            TableName = "DiagRetro_CL",
            WorkspaceId = "11111111-1111-1111-1111-111111111111",
        };
    }

    [Fact]
    public void BuildKql_EmitsTableTimeWindowLevelAndOrderedTop()
    {
        var kql = LogAnalyticsRetroLogger.BuildKql(BaseQuery(), "DiagRetro_CL");

        kql.Should().StartWith("DiagRetro_CL");
        kql.Should().Contain("where TimeGenerated >= datetime(2026-06-07T00:00:00.0000000Z)");
        kql.Should().Contain("and TimeGenerated < datetime(2026-06-07T12:00:00.0000000Z)");
        kql.Should().Contain("where Level >= 2");
        kql.Should().Contain("top 100 by TimeGenerated desc");
    }

    [Fact]
    public void BuildKql_WithNoTextFilters_EmitsNoRegexClause()
    {
        var kql = LogAnalyticsRetroLogger.BuildKql(BaseQuery(), "DiagRetro_CL");

        kql.Should().NotContain("matches regex");
    }

    [Theory]
    [InlineData("Machine", "srv01")]
    [InlineData("User", "alice")]
    [InlineData("Process", "ems.exe")]
    [InlineData("Message", "timeout")]
    public void BuildKql_WithTextFilter_EmitsCaseInsensitiveRegexClause(string field, string pattern)
    {
        var query = BaseQuery();
        switch (field)
        {
            case "Machine":
                query.Machine = pattern;
                break;
            case "User":
                query.User = pattern;
                break;
            case "Process":
                query.Process = pattern;
                break;
            case "Message":
                query.Message = pattern;
                break;
        }

        var kql = LogAnalyticsRetroLogger.BuildKql(query, "DiagRetro_CL");

        kql.Should().Contain($"where {field} matches regex \"(?i){pattern}\"");
    }

    [Fact]
    public void BuildKql_EscapesQuotesAndBackslashesInPattern()
    {
        var query = BaseQuery();
        query.Machine = "a\"b\\.c"; // a"b\.c — a valid regex containing a quote and a backslash

        var kql = LogAnalyticsRetroLogger.BuildKql(query, "DiagRetro_CL");

        // Quote -> \" and backslash -> \\ inside the KQL string literal.
        kql.Should().Contain("\"(?i)a\\\"b\\\\.c\"");
    }

    [Fact]
    public void BuildKql_WithInvalidRegex_Throws()
    {
        var query = BaseQuery();
        query.Message = "["; // not a valid regex

        Action act = () => LogAnalyticsRetroLogger.BuildKql(query, "DiagRetro_CL");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildKql_WithOverlongPattern_Throws()
    {
        var query = BaseQuery();
        query.Machine = new string('a', 257);

        Action act = () => LogAnalyticsRetroLogger.BuildKql(query, "DiagRetro_CL");

        act.Should().Throw<ArgumentException>();
    }

    // Patterns that compile under .NET but use constructs RE2 (KQL `matches regex`) rejects —
    // they must be caught up front, not deferred to a query-time RequestFailedException.
    [Theory]
    [InlineData("a(?=b)")] // lookahead
    [InlineData("a(?!b)")] // negative lookahead
    [InlineData("(?<=a)b")] // lookbehind
    [InlineData("(?<!a)b")] // negative lookbehind
    [InlineData("(?>ab)")] // atomic group
    [InlineData("(a)\\1")] // numeric backreference
    [InlineData("(?<g>a)\\k<g>")] // named backreference
    public void BuildKql_WithRe2IncompatiblePattern_Throws(string pattern)
    {
        var query = BaseQuery();
        query.Message = pattern;

        Action act = () => LogAnalyticsRetroLogger.BuildKql(query, "DiagRetro_CL");

        act.Should().Throw<ArgumentException>();
    }

    // Valid patterns that ARE supported by RE2 must not be over-rejected by the guard.
    [Theory]
    [InlineData("srv\\d+")] // ordinary regex
    [InlineData("a\\.b")] // escaped metacharacter
    [InlineData("\\p{Lu}")] // Unicode property class — RE2 supports these
    [InlineData("(?<name>abc)")] // named capture group (no backreference)
    public void BuildKql_WithRe2CompatiblePattern_DoesNotThrow(string pattern)
    {
        var query = BaseQuery();
        query.Message = pattern;

        Action act = () => LogAnalyticsRetroLogger.BuildKql(query, "DiagRetro_CL");

        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_WithMissingRequiredConfig_Throws()
    {
        var options = ValidOptions();
        options.DceEndpoint = "";

        Action act = () => _ = new LogAnalyticsRetroLogger(options);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SupportsDelete_IsFalse()
    {
        LogAnalyticsRetroLogger logger = new(ValidOptions());

        logger.SupportsDelete.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_Throws_NotSupported()
    {
        LogAnalyticsRetroLogger logger = new(ValidOptions());

        Func<Task> act = async () => await logger.Delete(["abc"]);

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*not supported*");
    }

    [Fact]
    public void Delete_ReturnsFaultedTask_RatherThanThrowingSynchronously()
    {
        LogAnalyticsRetroLogger logger = new(ValidOptions());

        // Invoking the method must not throw on the calling thread — a Task-returning method
        // surfaces failures via a faulted task so async callers can observe them.
        var task = logger.Delete(["abc"]);

        task.IsFaulted.Should().BeTrue();
        task.Exception!.InnerException.Should().BeOfType<NotSupportedException>();
    }
}
