using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using DiagnosticExplorer.Util;

namespace DiagnosticExplorer.UnitTests;

/// <summary>
///     The System.Text.Json converters define part of the diagnostic wire/JSON contract:
///     TimeSpans and enums are written as readable strings rather than numbers. These tests
///     pin the exact emitted form and the round-trip, since a silent change here would break
///     any consumer parsing the JSON.
/// </summary>
public class JsonConverterTests
{
    private static JsonSerializerOptions Options(JsonConverter converter)
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(converter);
        return options;
    }

    /// <summary>
    ///     JsonTimeSpanConverter writes a TimeSpan as its string form ("hh:mm:ss") and reads it
    ///     back to an equal value — the human-readable representation the UI/logs expect.
    /// </summary>
    [Fact]
    public void TimeSpanConverter_WritesStringForm_AndRoundTrips()
    {
        var options = Options(new JsonTimeSpanConverter());
        var value = TimeSpan.FromMinutes(90);

        var json = JsonSerializer.Serialize(value, options);

        json.Should().Be("\"01:30:00\"");
        JsonSerializer.Deserialize<TimeSpan>(json, options).Should().Be(value);
    }

    /// <summary>
    ///     JsonEnumConverter writes the enum member name (not its numeric value) and parses it
    ///     back, so payloads stay readable and resilient to numeric reordering of the enum.
    /// </summary>
    [Fact]
    public void EnumConverter_WritesMemberName_AndRoundTrips()
    {
        var options = Options(new JsonEnumConverter<EventSeverity>());

        var json = JsonSerializer.Serialize(EventSeverity.Medium, options);

        json.Should().Be("\"Medium\"");
        JsonSerializer.Deserialize<EventSeverity>(json, options).Should().Be(EventSeverity.Medium);
    }
}
