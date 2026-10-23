using AwesomeAssertions;

// Properties in the nested fixtures are consumed through reflection by DiagnosticManager.
// ReSharper disable UnusedMember.Local

namespace DiagnosticExplorer.UnitTests;

/// <summary>
///     PropertyAttribute was renamed to DiagnosticPropertyAttribute, with the old name kept as a
///     subclass so the consuming estate's 300-odd call sites keep compiling. That only works because
///     discovery is <c>GetCustomAttributes(typeof(DiagnosticPropertyAttribute), false)</c>, which
///     matches any assignable attribute type rather than an exact one.
/// </summary>
/// <remarks>
///     These assert through the public ObjectToPropertyBag surface rather than against the discovery
///     helper, because the claim being protected is a behavioural one: a property marked with the
///     old name must be presented exactly as one marked with the new name. A unit test of the
///     reflection call alone would pass even if the pipeline branched on attribute type somewhere
///     downstream.
/// </remarks>
public class DiagnosticPropertyAttributeTests
{
    private static Property[] BagProperties(object obj)
    {
        PropertyBag bag = DiagnosticManager.ObjectToPropertyBag(obj, "svc", null);
        return bag.Categories.SelectMany(c => c.Properties).OrderBy(p => p.Name).ToArray();
    }

    /// <summary>
    ///     The load-bearing test. Two fixtures identical but for which spelling of the attribute
    ///     they use must produce indistinguishable bags — name, value, description and category.
    /// </summary>
    [Fact]
    public void BothAttributeNames_ProduceIdenticalProperties()
    {
        Property[] viaOldName = BagProperties(new LegacyAttributeCarrier());
        Property[] viaNewName = BagProperties(new CurrentAttributeCarrier());

        viaOldName
            .Select(p => new
            {
                p.Name,
                p.Value,
                p.Description,
            })
            .Should()
            .BeEquivalentTo(
                viaNewName.Select(p => new
                {
                    p.Name,
                    p.Value,
                    p.Description,
                })
            );
    }

    [Fact]
    public void LegacyAttribute_IsDiscoveredAndCarriesItsMetadata()
    {
        Property property = BagProperties(new LegacyAttributeCarrier()).Should().ContainSingle().Subject;

        property.Name.Should().Be("Renamed");
        property.Value.Should().Be("value");
        property.Description.Should().Be("described");
    }

    /// <summary>
    ///     The old name has to remain assignable to the new one, or every discovery query silently
    ///     stops seeing it — which is a compile-clean, test-clean, entirely invisible failure.
    /// </summary>
    [Fact]
    public void LegacyAttribute_IsAssignableToTheCurrentAttribute()
    {
        typeof(PropertyAttribute).Should().BeAssignableTo<DiagnosticPropertyAttribute>();
    }

    /// <summary>
    ///     The specialised attributes now derive from the renamed base directly rather than through
    ///     the compatibility shim, so the shim can eventually be deleted without taking them with it.
    /// </summary>
    [Theory]
    [InlineData(typeof(CollectionPropertyAttribute))]
    [InlineData(typeof(DatePropertyAttribute))]
    [InlineData(typeof(ExtendedPropertyAttribute))]
    [InlineData(typeof(RatePropertyAttribute))]
    public void SpecialisedAttributes_DeriveFromTheCurrentAttributeDirectly(Type attributeType)
    {
        attributeType.BaseType.Should().Be<DiagnosticPropertyAttribute>();
    }

    /// <summary>Every constructor overload of the old name still forwards to the renamed base.</summary>
    [Fact]
    public void LegacyAttributeConstructors_ForwardEveryArgument()
    {
        new PropertyAttribute().Name.Should().BeNull();
        new PropertyAttribute("n").Name.Should().Be("n");

        var withCategory = new PropertyAttribute("n", "c");
        withCategory.Category.Should().Be("c");

        var withDescription = new PropertyAttribute("n", "c", "d");
        withDescription.Description.Should().Be("d");
        withDescription.Category.Should().Be("c");
        withDescription.Name.Should().Be("n");
    }

    /// <summary>
    ///     Ignore and AllowSet are read off the base type, so they must behave the same whichever
    ///     spelling declared them.
    /// </summary>
    [Fact]
    public void LegacyAttribute_HonoursIgnoreAndAllowSetLikeTheCurrentAttribute()
    {
        BagProperties(new IgnoredByLegacyAttributeCarrier()).Should().BeEmpty();

        new PropertyAttribute { AllowSet = true }
            .AllowSet.Should()
            .BeTrue();
    }

    // Fixture properties are read through reflection by DiagnosticManager; they are instance
    // members by design, so S1144/S2325 do not apply.
#pragma warning disable S1144, S2325
    [DiagnosticClass(AttributedPropertiesOnly = true)]
    private sealed class LegacyAttributeCarrier
    {
        [Property("Renamed", null, "described")]
        public string Subject => "value";
    }

    [DiagnosticClass(AttributedPropertiesOnly = true)]
    private sealed class CurrentAttributeCarrier
    {
        [DiagnosticProperty("Renamed", null, "described")]
        public string Subject => "value";
    }

    [DiagnosticClass(AttributedPropertiesOnly = true)]
    private sealed class IgnoredByLegacyAttributeCarrier
    {
        [Property(Ignore = true)]
        public string Subject => "value";
    }
#pragma warning restore S1144, S2325
}
