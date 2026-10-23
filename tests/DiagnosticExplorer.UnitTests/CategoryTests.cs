using AwesomeAssertions;

namespace DiagnosticExplorer.UnitTests;

/// <summary>
///     The Category FindByName extension is the case-insensitive lookup behind
///     PropertyBag.FindOrCreateCategory/GetProperty, so its matching rules matter.
/// </summary>
public class CategoryTests
{
    /// <summary>
    ///     Confirms case-insensitive matching and a null (not throwing) miss — the exact
    ///     behaviour FindOrCreateCategory relies on to decide whether to create a category.
    /// </summary>
    [Theory]
    [InlineData("Stats", true)]
    [InlineData("STATS", true)]
    [InlineData("Other", false)]
    public void FindByName_WithVariousNames_ReturnsExpectedMatch(string name, bool shouldFind)
    {
        var list = new[] { new Category("Stats"), new Category("Health") };

        var result = list.FindByName(name);

        (result is not null).Should().Be(shouldFind);
    }

    /// <summary>
    ///     A null source list is a caller error; surfacing ArgumentNullException keeps the
    ///     failure close to its cause.
    /// </summary>
    [Fact]
    public void FindByName_WithNullList_Throws()
    {
        IEnumerable<Category> list = null!;

        var act = () => list.FindByName("Stats");

        act.Should().Throw<ArgumentNullException>();
    }
}
