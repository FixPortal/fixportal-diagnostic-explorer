using AwesomeAssertions;

namespace DiagnosticExplorer.UnitTests;

/// <summary>
///     PropertyBag is the unit of diagnostic data the library emits and serializes
///     over the wire, so its category bookkeeping is core behaviour worth pinning.
/// </summary>
public class PropertyBagTests
{
    /// <summary>
    ///     Adding a property under a not-yet-seen category must create that category and
    ///     file the property in it — this is the primary way a bag is populated, and a
    ///     regression here would silently drop emitted diagnostics.
    /// </summary>
    [Fact]
    public void AddProperty_ForNewCategory_CreatesCategoryAndStoresProperty()
    {
        var bag = new PropertyBag("svc");

        bag.AddProperty(new Property("Uptime", "42"), "Stats");

        bag.Categories.Should().ContainSingle(c => c.Name == "Stats");
        bag.GetProperty("Uptime", "Stats")!.Value.Should().Be("42");
    }

    /// <summary>
    ///     Repeated adds for the same category (matched case-insensitively) must reuse the
    ///     existing category rather than creating duplicates, otherwise the viewer would
    ///     show the same category several times.
    /// </summary>
    [Fact]
    public void AddProperty_ForExistingCategoryDifferingByCase_ReusesSingleCategory()
    {
        var bag = new PropertyBag("svc");

        bag.AddProperty(new Property("Uptime", "42"), "Stats");
        bag.AddProperty(new Property("Requests", "7"), "stats");

        bag.Categories.Should().ContainSingle();
        bag.Categories[0].Properties.Should().HaveCount(2);
    }

    /// <summary>
    ///     A null property is a programming error; failing fast with ArgumentNullException
    ///     is preferable to storing a null that blows up later during serialization.
    /// </summary>
    [Fact]
    public void AddProperty_WithNullProperty_Throws()
    {
        var bag = new PropertyBag("svc");

        var act = () => bag.AddProperty(null!, "Stats");

        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    ///     FindOrCreateCategory is the dedupe primitive behind AddProperty; it must return
    ///     the same instance for names differing only by case so callers accumulate into
    ///     one category.
    /// </summary>
    [Fact]
    public void FindOrCreateCategory_CalledTwiceWithDifferentCase_ReturnsSameInstance()
    {
        var bag = new PropertyBag("svc");

        var first = bag.FindOrCreateCategory("Stats");
        var second = bag.FindOrCreateCategory("STATS");

        second.Should().BeSameAs(first);
        bag.Categories.Should().ContainSingle();
    }

    /// <summary>
    ///     GetProperty must return null (not throw) when the category is absent, so callers
    ///     can probe for optional properties without guarding every lookup.
    /// </summary>
    [Fact]
    public void GetProperty_WithUnknownCategory_ReturnsNull()
    {
        var bag = new PropertyBag("svc");
        bag.AddProperty(new Property("Uptime", "42"), "Stats");

        bag.GetProperty("Uptime", "Missing").Should().BeNull();
    }
}
