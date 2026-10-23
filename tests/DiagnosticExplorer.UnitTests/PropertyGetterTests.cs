using System.Collections;
using AwesomeAssertions;
using MessagePack;
using MessagePack.Resolvers;

// Properties in the nested fixtures are consumed through reflection by DiagnosticManager.
// ReSharper disable UnusedMember.Local

namespace DiagnosticExplorer.UnitTests;

/// <summary>
///     The property-getter pipeline (driven through DiagnosticManager.ObjectToPropertyBag) must be
///     resilient: a single throwing property degrades to an error string rather than aborting the
///     whole diagnostic walk, and a collection source is enumerated only once.
/// </summary>
public class PropertyGetterTests
{
    private readonly ITestOutputHelper _output;

    public PropertyGetterTests(ITestOutputHelper output) => _output = output;

    private static IEnumerable<string?> AllValues(PropertyBag bag)
    {
        return bag.Categories.SelectMany(c => c.Properties).Select(p => p.Value);
    }

    private static IEnumerable<string?> AllNames(PropertyBag bag)
    {
        return bag.Categories.SelectMany(c => c.Properties).Select(p => p.Name);
    }

    /// <summary>TimeSpan formatting preserves every component selected by its display branch.</summary>
    [Theory]
    [InlineData(3_723_000, "01:02:03")]
    [InlineData(93_784_000, "1.02:03:04")]
    [InlineData(-50, "-00:00:00.50")]
    public void TimeSpanValue_IsRenderedWithoutDroppingComponents(long milliseconds, string expected)
    {
        PropertyGetterProbe.Format(TimeSpan.FromMilliseconds(milliseconds)).Should().Be(expected);
    }

    /// <summary>
    ///     RateGetter read the RateCounter via the raw getter, outside the guarded GetValue path, so a
    ///     throwing rate property aborted the entire walk. It now degrades to an error-string property. (M18)
    /// </summary>
    [Fact]
    public void RateProperty_ThatThrows_DegradesToErrorProperty_WithoutAbortingTheWalk()
    {
        var bag = DiagnosticManager.ObjectToPropertyBag(new ThrowingRate(), "svc", null);

        AllValues(bag).Should().Contain(v => v != null && v.Contains("rate boom"));
    }

    /// <summary>
    ///     DateGetter's elapsed/until sub-path called the raw getter outside any try/catch; a throwing
    ///     date property now degrades to an error string instead of aborting the walk. (M18)
    /// </summary>
    [Fact]
    public void DateProperty_ElapsedPath_ThatThrows_DegradesToErrorProperty()
    {
        var bag = DiagnosticManager.ObjectToPropertyBag(new ThrowingDate(), "svc", null);

        AllValues(bag).Should().Contain(v => v != null && v.Contains("date boom"));
    }

    /// <summary>
    ///     CollectionGetter counted the source then re-enumerated it (concatenate mode up to three
    ///     passes via FormatEnumerable's Count()+Take()), re-running stateful/expensive sequences. It
    ///     now materializes once, so the source is enumerated exactly once. (M19)
    /// </summary>
    [Fact]
    public void ConcatenateCollection_EnumeratesTheSourceOnce()
    {
        var obj = new HasConcatCollection();

        DiagnosticManager.ObjectToPropertyBag(obj, "svc", null);

        obj.Items.Enumerations.Should().Be(1);
    }

    /// <summary>
    ///     CollectionGetter.AppendSeparateCategories must render a &lt;cycle&gt; placeholder for an
    ///     already-visited object instead of recursing into it. ObjectToPropertyBag seeds
    ///     VisitedObjects with the root, so a Categories-mode collection containing the root itself
    ///     trips the guard on the first item. Without the guard the walk recurses unboundedly and
    ///     dies to an uncatchable StackOverflowException — CollectionGetter's own catch at
    ///     CollectionGetter.cs:217-221 cannot mitigate that. The assertion is on Property.Name: the
    ///     placeholder carries no Value, so the AllValues probe cannot see it. (DE-7)
    /// </summary>
    [Fact]
    public void CategoriesCollection_ContainingSelf_RendersCyclePlaceholder_InsteadOfRecursing()
    {
        var bag = DiagnosticManager.ObjectToPropertyBag(new SelfContainingChildren(), "svc", null);

        Property? cycle = bag.GetProperty("<cycle>", "Item 0");
        cycle.Should().NotBeNull();
        cycle.Name.Should().Be("<cycle>");
    }

    /// <summary>
    ///     CollectionGetter.AppendSeparateCategories must render a &lt;max depth&gt; placeholder once
    ///     more than 50 objects are on the VisitedObjects stack, instead of recursing deeper. A chain
    ///     longer than 50 nodes trips the guard; without it the walk would follow arbitrarily deep
    ///     object graphs. (DE-7)
    /// </summary>
    [Fact]
    public void CategoriesCollection_DeeperThan50_RendersMaxDepthPlaceholder_InsteadOfRecursing()
    {
        var bag = DiagnosticManager.ObjectToPropertyBag(BuildCollectionChain(60), "svc", null);

        AllNames(bag).Should().Contain("<max depth>");
    }

    /// <summary>
    ///     ExtendedPropertyGetter duplicates the cycle guard: an [ExtendedProperty] whose value is
    ///     already visited (here the root itself) must render a &lt;cycle&gt; placeholder instead of
    ///     recursing. (DE-7)
    /// </summary>
    [Fact]
    public void ExtendedProperty_ReferencingSelf_RendersCyclePlaceholder_InsteadOfRecursing()
    {
        var bag = DiagnosticManager.ObjectToPropertyBag(new ExtendedSelfReference(), "svc", null);

        Property? cycle = bag.GetProperty("<cycle>", "Self");
        cycle.Should().NotBeNull();
        cycle.Name.Should().Be("<cycle>");
    }

    /// <summary>
    ///     ExtendedPropertyGetter duplicates the depth guard: an [ExtendedProperty] chain deeper than
    ///     50 visited objects must render a &lt;max depth&gt; placeholder instead of recursing. (DE-7)
    /// </summary>
    [Fact]
    public void ExtendedProperty_ChainDeeperThan50_RendersMaxDepthPlaceholder_InsteadOfRecursing()
    {
        var root = BuildExtendedChain(60);
        root.Next.Should().NotBeNull();
        var bag = DiagnosticManager.ObjectToPropertyBag(root, "svc", null);

        AllNames(bag).Should().Contain("<max depth>");
    }

    private static CollectionChainNode BuildCollectionChain(int depth)
    {
        CollectionChainNode? node = null;
        for (int i = 0; i < depth; i++)
        {
            node = new CollectionChainNode(node);
        }
        return node!;
    }

    private static ExtendedChainNode BuildExtendedChain(int depth)
    {
        ExtendedChainNode? node = null;
        for (int i = 0; i < depth; i++)
        {
            node = new ExtendedChainNode(node);
        }
        return node!;
    }

    /// <summary>
    ///     DateGetter's elapsed path normalizes every kind to UTC before diffing against
    ///     DateTime.UtcNow. Four properties hold the same instant in different kinds — Utc, Local,
    ///     Unspecified-declared-UTC, and Unspecified-meaning-local — and all four must render the
    ///     same elapsed value of ~90 seconds. The equality assertion pins the normalization; the
    ///     magnitude assertion pins that no offset was wrongly applied, since a missed conversion
    ///     shifts one value by the local UTC offset while leaving the others right. The two-string
    ///     tolerance absorbs a second boundary crossing between the fixture's clock read and the
    ///     getter's. (DE-29)
    /// </summary>
    /// <remarks>
    ///     The arithmetic was local-time until the getter-layer merge. UTC is correct because local
    ///     wall-clock readings are discontinuous: subtracting two of them across a daylight-saving
    ///     transition understates or overstates the true elapsed time by an hour.
    /// </remarks>
    [Fact]
    public void DateProperty_EveryDateTimeKind_IsNormalizedToUtcBeforeElapsed()
    {
        var bag = DiagnosticManager.ObjectToPropertyBag(new NormalizableDates(), "svc", null);

        var values = AllValues(bag).ToList();
        values.Should().HaveCount(4);
        values.Distinct().Should().ContainSingle();
        values[0].Should().BeOneOf("00:01:30", "00:01:31");
    }

    private sealed class ThrowingRate
    {
        [RateProperty]
        public RateCounter Boom => throw new InvalidOperationException("rate boom");
    }

    private sealed class ThrowingDate
    {
        [DateProperty(ExposeDate = false, ExposeElapsed = true)]
        public DateTime Boom => throw new InvalidOperationException("date boom");
    }

    private sealed class NormalizableDates
    {
        private readonly DateTime _instantUtc = DateTime.UtcNow.AddSeconds(-90);

        [DateProperty(ExposeDate = false, ExposeElapsed = true)]
        public DateTime UtcValue => _instantUtc;

        [DateProperty(ExposeDate = false, ExposeElapsed = true, IsUTC = true)]
        public DateTime UnspecifiedAsUtc => DateTime.SpecifyKind(_instantUtc, DateTimeKind.Unspecified);

        [DateProperty(ExposeDate = false, ExposeElapsed = true)]
        public DateTime LocalValue => _instantUtc.ToLocalTime();

        // Unspecified without IsUTC is taken at face value (already local) — must render the same.
        [DateProperty(ExposeDate = false, ExposeElapsed = true)]
        public DateTime UnspecifiedLocal => DateTime.SpecifyKind(_instantUtc.ToLocalTime(), DateTimeKind.Unspecified);
    }

    private sealed class CountingEnumerable : IEnumerable<int>
    {
        public int Enumerations;

        public IEnumerator<int> GetEnumerator()
        {
            Enumerations++;
            return Enumerable.Range(1, 3).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    private sealed class HasConcatCollection
    {
        // Public field (not a property) so the diagnostic walk ignores it but the test can read it.
        public readonly CountingEnumerable Items = new();

        [CollectionProperty(CollectionMode.Concatenate)]
        public CountingEnumerable Numbers => Items;
    }

    private sealed class SelfContainingChildren
    {
        [CollectionProperty(CollectionMode.Categories)]
        public List<SelfContainingChildren> Children => [this];
    }

    private sealed class CollectionChainNode
    {
        private readonly CollectionChainNode? _next;

        public CollectionChainNode(CollectionChainNode? next)
        {
            _next = next;
        }

        [CollectionProperty(CollectionMode.Categories)]
        public List<CollectionChainNode> Children => _next is null ? [] : [_next];
    }

    private sealed class ExtendedSelfReference
    {
        [ExtendedProperty]
        public ExtendedSelfReference Self => this;
    }

    private sealed class ExtendedChainNode
    {
        public ExtendedChainNode(ExtendedChainNode? next)
        {
            Next = next;
        }

        [ExtendedProperty]
        public ExtendedChainNode? Next { get; }
    }

    private sealed class PropertyGetterProbe : PropertyGetter
    {
        public static string Format(TimeSpan value) => FormatTimeSpan(value);
    }

    /// <summary>
    ///     A getter needs either a PropertyInfo or a configuration to read from. With neither, the
    ///     value function stays null and the failure used to surface much later as a
    ///     NullReferenceException from GetValue, on whichever poll first reached the property —
    ///     far from the construction that caused it.
    /// </summary>
    [Fact]
    public void PropertyGetter_WithNeitherPropertyInfoNorConfiguration_ThrowsAtConstruction()
    {
        Action construct = () => _ = new PropertyGetter(null, null, null, isStatic: false);

        construct.Should().Throw<ArgumentException>();
    }

    // ---------------------------------------------------------------------
    // DE-28: CollectionGetter render modes (CollectionGetter.cs:157-215) and
    // PropertyGetter.FormatEnumerable's counted/uncounted wording
    // (PropertyGetter.cs:228-313). All driven through ObjectToPropertyBag.
    // ---------------------------------------------------------------------

    private static string? SoleValue(object obj, string propName)
    {
        PropertyBag bag = DiagnosticManager.ObjectToPropertyBag(obj, "svc", null);
        return bag
            .Categories.SelectMany(c => c.Properties)
            .Where(p => p.Name == propName)
            .Select(p => p.Value)
            .Single();
    }

    // A yield-based iterator exposes no public Count property, forcing CollectionGetter /
    // FormatEnumerable down their uncounted paths deterministically.
    private static IEnumerable<int> Stream(int items)
    {
        for (int i = 1; i <= items; i++)
        {
            yield return i;
        }
    }

    /// <summary>
    ///     (DE-28) Count mode on a counted collection renders FormatValue(count) — the item total
    ///     as a plain number, no enumeration.
    /// </summary>
    [Fact]
    public void CountMode_CountedCollection_RendersTheItemCount()
    {
        SoleValue(new CountedCollection(), nameof(CountedCollection.Items)).Should().Be("3");
    }

    /// <summary>
    ///     (DE-28) Count mode on an uncounted enumerable past the 10,000-item cap renders the
    ///     "10000+ items" sentinel rather than a number — the getter takes 10,001 items to detect
    ///     truncation and must not pretend to know the real total.
    /// </summary>
    [Fact]
    public void CountMode_UncountedOverTruncationCap_RendersSentinel()
    {
        SoleValue(new UncountedBigCollection(), nameof(UncountedBigCollection.Items)).Should().Be("10000+ items");
    }

    /// <summary>
    ///     (DE-28) List mode renders each item as its own property named "{Property} {index}"
    ///     with the formatted item value.
    /// </summary>
    [Fact]
    public void ListMode_RendersEachItemAsItsOwnProperty()
    {
        var bag = DiagnosticManager.ObjectToPropertyBag(new ListedCollection(), "svc", null);

        bag.GetProperty("Items 0")!.Value.Should().Be("a");
        bag.GetProperty("Items 1")!.Value.Should().Be("b");
        AllNames(bag).Should().Equal("Items 0", "Items 1");
    }

    /// <summary>
    ///     (DE-28) List mode truncates at 10,000 items and appends a "..." marker property with
    ///     the truncation wording. The items are plain objects so 10k formatting stays cheap.
    /// </summary>
    [Fact]
    public void ListMode_OverTruncationCap_Renders10000ItemsPlusTruncationMarker()
    {
        var bag = DiagnosticManager.ObjectToPropertyBag(new TruncatedListCollection(), "svc", null);

        AllNames(bag).Where(n => n != "...").Should().HaveCount(10000);
        bag.GetProperty("...")!.Value.Should().Be("Truncated at 10000 items");
        bag.GetProperty("Items 9999").Should().NotBeNull();
        bag.GetProperty("Items 10000").Should().BeNull();
    }

    /// <summary>
    ///     (DE-28) Categories mode walks each item into its own category (keyed by the
    ///     CategoryProperty) and renders the item's properties inside it.
    /// </summary>
    [Fact]
    public void CategoriesMode_RendersEachItemUnderItsOwnCategory()
    {
        var source = new CategorizedCollection();
        source.Items.Select(item => item.Value).Should().Equal(1, 2);
        var bag = DiagnosticManager.ObjectToPropertyBag(source, "svc", null);

        bag.GetProperty("Value", "alpha")!.Value.Should().Be("1");
        bag.GetProperty("Value", "beta")!.Value.Should().Be("2");
    }

    /// <summary>
    ///     (DE-28) Categories mode shares the 10,000-item cap: the 10,001st item is dropped and a
    ///     "..." property is added under a "..." category with the truncation wording.
    /// </summary>
    [Fact]
    public void CategoriesMode_OverTruncationCap_RendersTruncationMarkerCategory()
    {
        var bag = DiagnosticManager.ObjectToPropertyBag(new TruncatedCategoriesCollection(), "svc", null);

        bag.GetProperty("Value", "item-9999").Should().NotBeNull();
        bag.GetProperty("Value", "item-10000").Should().BeNull();
        bag.GetProperty("...", "...")!.Value.Should().Be("Truncated at 10000 items");
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Performance")]
    public void PropertySnapshotWireQualificationProcessesCollectionLimit()
    {
        const int itemCount = 10_000;
        // Serialised exactly as the agent channel does it: MessagePack with the contractless
        // resolver. The transfer types carry no serialization attributes any more, so this is
        // the only thing proving they still survive a wire round-trip at scale.
        MessagePackSerializerOptions wireOptions = MessagePackSerializerOptions
            .Standard.WithResolver(ContractlessStandardResolver.Instance)
            .WithSecurity(MessagePackSecurity.UntrustedData);
        var source = new TruncatedCategoriesCollection();
        var warmup = DiagnosticManager.ObjectToPropertyBag(source, "svc", null);
        var payload = MessagePackSerializer.Serialize(warmup, wireOptions, TestContext.Current.CancellationToken);
        var restored = MessagePackSerializer.Deserialize<PropertyBag>(
            payload,
            wireOptions,
            TestContext.Current.CancellationToken
        );

        var elapsed = new List<TimeSpan>();
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var snapshot = DiagnosticManager.ObjectToPropertyBag(source, "svc", null);
            payload = MessagePackSerializer.Serialize(snapshot, wireOptions, TestContext.Current.CancellationToken);
            restored = MessagePackSerializer.Deserialize<PropertyBag>(
                payload,
                wireOptions,
                TestContext.Current.CancellationToken
            );
            stopwatch.Stop();
            elapsed.Add(stopwatch.Elapsed);
        }

        var median = elapsed.Order().ElementAt(elapsed.Count / 2);
        _output.WriteLine(
            $"items={itemCount}; measuredIterations={elapsed.Count}; medianMs={median.TotalMilliseconds:F1}; "
                + $"itemsPerSecond={itemCount / median.TotalSeconds:F1}; wireBytes={payload.Length}; "
                + $"runsMs=[{string.Join(", ", elapsed.Select(run => run.TotalMilliseconds.ToString("F1")))}]"
        );

        payload.Should().NotBeEmpty("the wire workload exercises the MessagePack framing path");
        restored.Categories.Where(category => category.Name != "...").Should().HaveCount(itemCount);
        restored.GetProperty("Value", "item-9999").Should().NotBeNull();
        restored.GetProperty("Value", "item-10000").Should().BeNull();
        restored.GetProperty("...", "...").Value.Should().Be("Truncated at 10000 items");
    }

    /// <summary>
    ///     (DE-28) Concatenate mode delegates to FormatEnumerable: a counted collection under the
    ///     10-item cap renders "{n} item(s): " plus the separator-joined values.
    /// </summary>
    [Theory]
    [InlineData(1, "1 item: 42")]
    [InlineData(3, "3 items: 1, 2, 3")]
    public void ConcatenateMode_CountedUnderCap_RendersCountPrefixAndValues(int items, string expected)
    {
        SoleValue(new ConcatCountedCollection(items), nameof(ConcatCountedCollection.Items)).Should().Be(expected);
    }

    /// <summary>
    ///     (DE-28) A counted collection over the cap knows its real total, so the remainder is
    ///     spelled out as "... (N more item[s])" — singular and plural pinned separately.
    /// </summary>
    [Theory]
    [InlineData(11, "11 items: 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, ... (1 more item)")]
    [InlineData(12, "12 items: 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, ... (2 more items)")]
    public void ConcatenateMode_CountedOverCap_RendersCountedRemainder(int items, string expected)
    {
        SoleValue(new ConcatCountedCollection(items), nameof(ConcatCountedCollection.Items)).Should().Be(expected);
    }

    /// <summary>
    ///     (DE-28) The uncounted branch of FormatEnumerable is only reachable through the plain
    ///     (non-[CollectionProperty]) property path: CollectionGetter materializes the source into
    ///     a List before calling FormatEnumerable, so Concatenate mode always sees a counted
    ///     collection. Via FormatValue, an uncounted enumerable is prefixed "Many items: " even
    ///     when it fits under the cap — the getter never learned the total.
    /// </summary>
    [Fact]
    public void PlainEnumerable_UncountedUnderCap_RendersManyItemsPrefix()
    {
        SoleValue(new PlainUncountedEnumerables(3), nameof(PlainUncountedEnumerables.Items))
            .Should()
            .Be("Many items: " + string.Join(Environment.NewLine, "1", "2", "3"));
    }

    /// <summary>
    ///     (DE-28) An uncounted enumerable over the cap can only say "... (more items)": it takes
    ///     maxItems + 1 to detect overflow and cannot compute the real remainder — the counted
    ///     counterpart spells out "... (N more items)".
    /// </summary>
    [Fact]
    public void PlainEnumerable_UncountedOverCap_RendersUncountedRemainder()
    {
        SoleValue(new PlainUncountedEnumerables(12), nameof(PlainUncountedEnumerables.Items))
            .Should()
            .Be(
                "Many items: "
                    + string.Join(
                        Environment.NewLine,
                        "1",
                        "2",
                        "3",
                        "4",
                        "5",
                        "6",
                        "7",
                        "8",
                        "9",
                        "10",
                        "... (more items)"
                    )
            );
    }

    /// <summary>
    ///     (DE-28) A plain (non-[CollectionProperty]) enumerable property also flows through
    ///     FormatEnumerable via FormatValue: empty renders "0 items", and values join on
    ///     Environment.NewLine rather than the collection separator.
    /// </summary>
    [Fact]
    public void PlainEnumerableProperty_RendersViaFormatEnumerable()
    {
        var bag = DiagnosticManager.ObjectToPropertyBag(new PlainEnumerables(), "svc", null);

        bag.GetProperty("Empty")!.Value.Should().Be("0 items");
        bag.GetProperty("Some")!.Value.Should().Be("3 items: " + string.Join(Environment.NewLine, "1", "2", "3"));
    }

    private sealed class CountedCollection
    {
        [CollectionProperty(CollectionMode.Count)]
        public List<int> Items => [1, 2, 3];
    }

    private sealed class UncountedBigCollection
    {
        [CollectionProperty(CollectionMode.Count)]
        public IEnumerable<int> Items => Stream(15_000);
    }

    private sealed class ListedCollection
    {
        [CollectionProperty(CollectionMode.List)]
        public List<string> Items => ["a", "b"];
    }

    private sealed class TruncatedListCollection
    {
        [CollectionProperty(CollectionMode.List)]
        public List<object> Items => Enumerable.Range(1, 10_001).Select(_ => new object()).ToList();
    }

    private sealed class CategorizedItem
    {
        public CategorizedItem(string name, int value)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; }

        public int Value { get; }
    }

    private sealed class CategorizedCollection
    {
        [CollectionProperty(CollectionMode.Categories, CategoryProperty = nameof(CategorizedItem.Name))]
        public List<CategorizedItem> Items => [new("alpha", 1), new("beta", 2)];
    }

    private sealed class TruncatedCategoriesCollection
    {
        [CollectionProperty(CollectionMode.Categories, CategoryProperty = nameof(CategorizedItem.Name))]
        public List<CategorizedItem> Items =>
            Enumerable.Range(0, 10_001).Select(i => new CategorizedItem($"item-{i}", i)).ToList();
    }

    private sealed class ConcatCountedCollection
    {
        private readonly int _items;

        public ConcatCountedCollection(int items)
        {
            _items = items;
        }

        [CollectionProperty(CollectionMode.Concatenate, Separator = ", ")]
        public List<int> Items => _items == 1 ? [42] : Enumerable.Range(1, _items).ToList();
    }

    private sealed class PlainUncountedEnumerables
    {
        private readonly int _items;

        public PlainUncountedEnumerables(int items)
        {
            _items = items;
        }

        [Property]
        public IEnumerable<int> Items => Stream(_items);
    }

    private sealed class PlainEnumerables
    {
        [Property]
        public List<int> Empty => [];

        [Property]
        public List<int> Some => [1, 2, 3];
    }
}
