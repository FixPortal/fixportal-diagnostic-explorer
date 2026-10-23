using AwesomeAssertions;

namespace DiagnosticExplorer.UnitTests;

/// <summary>
///     Covers Property's ToString formatting (used in logs/diagnostics) and the
///     case-insensitive FindByName extension that the bag/category lookups rely on.
/// </summary>
public class PropertyTests
{
    /// <summary>
    ///     ToString composes optional segments (settable flag, description, operation set)
    ///     onto the name/value pair. Parameterizing the combinations guards the exact
    ///     rendering the diagnostics output depends on, in one place.
    /// </summary>
    [Theory]
    [InlineData("V", null, false, null, "N = [V]")]
    [InlineData("V", "D", false, null, "N = [V] (D)")]
    [InlineData("V", null, true, null, "N = [V] (SET)")]
    [InlineData("V", null, false, "OS", "N = [V] (OperationSet=OS)")]
    [InlineData("V", "D", true, "OS", "N = [V] (SET) (D) (OperationSet=OS)")]
    public void ToString_WithVariousOptionalSegments_RendersExpectedText(
        string value,
        string? description,
        bool canSet,
        string? operationSet,
        string expected
    )
    {
        var property = new Property("N", value, description) { CanSet = canSet, OperationSet = operationSet };

        property.ToString().Should().Be(expected);
    }

    /// <summary>
    ///     FindByName matches case-insensitively and returns null when absent — the contract
    ///     PropertyBag.GetProperty leans on. Parameterized to cover hit, case-variant hit,
    ///     and miss together.
    /// </summary>
    [Theory]
    [InlineData("Uptime", true)]
    [InlineData("UPTIME", true)]
    [InlineData("Missing", false)]
    public void FindByName_WithVariousNames_ReturnsExpectedMatch(string name, bool shouldFind)
    {
        var list = new[] { new Property("Uptime", "42"), new Property("Requests", "7") };

        var result = list.FindByName(name);

        (result is not null).Should().Be(shouldFind);
    }

    /// <summary>
    ///     FindByName guards against a null source list with ArgumentNullException rather
    ///     than a less-diagnosable NullReferenceException deeper in LINQ.
    /// </summary>
    [Fact]
    public void FindByName_WithNullList_Throws()
    {
        IEnumerable<Property> list = null!;

        var act = () => list.FindByName("Uptime");

        act.Should().Throw<ArgumentNullException>();
    }
}
