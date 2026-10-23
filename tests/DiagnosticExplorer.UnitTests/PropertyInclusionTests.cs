using System.ComponentModel;
using AwesomeAssertions;

// Properties in the nested fixtures are consumed through reflection by DiagnosticManager.
// ReSharper disable UnusedMember.Local

namespace DiagnosticExplorer.UnitTests;

/// <summary>
///     DiagnosticManager.ShouldIncludeProperty (DiagnosticManager.cs:547-574) is the gate that
///     decides which public properties appear in a diagnostic bag. The rules, pinned here through
///     the public ObjectToPropertyBag surface: EventSink-typed properties are always excluded; a
///     [Property] attribute decides on its own (including overriding [Browsable(false)] and
///     honouring Ignore); [Browsable(false)] excludes; and under
///     [DiagnosticClass(AttributedPropertiesOnly)] a property needs an explicit [Browsable] (or
///     [Property]) to be included. (DE-25)
/// </summary>
public class PropertyInclusionTests
{
    private static string[] BagPropertyNames(object obj)
    {
        PropertyBag bag = DiagnosticManager.ObjectToPropertyBag(obj, "svc", null);
        return bag.Categories.SelectMany(c => c.Properties).Select(p => p.Name).Order().ToArray();
    }

    /// <summary>
    ///     Default (unattributed) class: plain properties are in, [Browsable(false)] and
    ///     [Property(Ignore = true)] are out, and an EventSink-typed property is always excluded —
    ///     sinks are event channels, not diagnostic state, so surfacing one would drag the whole
    ///     sink object graph into the bag.
    /// </summary>
    [Fact]
    public void UnattributedClass_AppliesBrowsableIgnoreAndEventSinkRules()
    {
        BagPropertyNames(new PlainPropertyCarrier()).Should().Equal("Included", "PropertyAttrBeatsBrowsable");
    }

    /// <summary>
    ///     AttributedPropertiesOnly flips the default: a plain property is excluded unless it
    ///     carries an explicit [Browsable] (a [Browsable(false)] is still excluded), while a
    ///     [Property] attribute short-circuits the whole gate and includes on its own.
    /// </summary>
    [Fact]
    public void AttributedPropertiesOnlyClass_RequiresAnExplicitAttribute()
    {
        BagPropertyNames(new AttributedOnlyPropertyCarrier()).Should().Equal("Attributed", "Browsable");
    }

    // Fixture properties are read through reflection by DiagnosticManager; they are instance
    // members by design (the walk is over instance properties), so S1144/S2325 do not apply.
#pragma warning disable S1144, S2325
    private sealed class PlainPropertyCarrier
    {
        public string Included => "yes";

        [Browsable(false)]
        public string Hidden => "no";

        [Property(Ignore = true)]
        public string Ignored => "no";

        // A [Property] attribute short-circuits the Browsable check: the property is included
        // even with [Browsable(false)], because propAttr is evaluated first and returns !Ignore.
        [Property]
        [Browsable(false)]
        public string PropertyAttrBeatsBrowsable => "yes";

        // Always excluded by type, with or without any attribute.
        public EventSink Sink { get; } = EventSinkRepo.Default.GetSink("DE25 Sink", "DE25");
    }

    [DiagnosticClass(AttributedPropertiesOnly = true)]
    private sealed class AttributedOnlyPropertyCarrier
    {
        public string Plain => "no";

        [Browsable(true)]
        public string Browsable => "yes";

        [Browsable(false)]
        public string Hidden => "no";

        [Property]
        public string Attributed => "yes";

        // Still excluded by type even under AttributedPropertiesOnly.
        public EventSink Sink { get; } = EventSinkRepo.Default.GetSink("DE25 Sink 2", "DE25");
    }
#pragma warning restore S1144, S2325
}
