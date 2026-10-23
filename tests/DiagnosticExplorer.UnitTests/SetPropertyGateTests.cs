using AwesomeAssertions;

// Properties in the nested fixtures are consumed through reflection by DiagnosticManager.
// ReSharper disable UnusedMember.Local

namespace DiagnosticExplorer.UnitTests;

/// <summary>
///     With AuthMode = None (the default), the hub exposes DiagnosticManager.SetProperty
///     anonymously, so PropertyGetter.CanSet is the only write-side protection: a property may be
///     set through the hub only when the declaring class opts in via
///     DiagnosticClassAttribute.AllPropertiesSettable or the property itself carries
///     PropertyAttribute.AllowSet = true. Both CanSet and the SetProperty outcome are asserted
///     because they are separate failure points. (DE-2)
/// </summary>
public class SetPropertyGateTests
{
    public static IEnumerable<object[]> Cases =>
        new List<object[]>
        {
            new object[] { (Func<object>)(() => new PlainWritable()), nameof(PlainWritable.Value), false },
            new object[] { (Func<object>)(() => new AllSettable()), nameof(AllSettable.Value), true },
            new object[] { (Func<object>)(() => new AllowSetProperty()), nameof(AllowSetProperty.Value), true },
            new object[] { (Func<object>)(() => new ReadOnly()), nameof(ReadOnly.Value), false },
        };

    /// <summary>
    ///     A writable property without any opt-in must be rejected with "AllowSet is not enabled!",
    ///     and only an explicit class- or property-level opt-in may let a write through. (DE-2)
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void SetProperty_RespectsTheCanSetGate(Func<object> fixtureFactory, string propertyName, bool expectedCanSet)
    {
        var fixture = fixtureFactory();
        var propInfo = fixture.GetType().GetProperty(propertyName)!;

        var getter = new PropertyGetter(propInfo, false);
        getter.CanSet.Should().Be(expectedCanSet);

        var registered = new RegisteredObject(fixture, "TestCategory", "TestBag");
        var result = DiagnosticManager.SetProperty(new[] { registered }, $"TestCategory|TestBag||{propertyName}", "42");

        if (expectedCanSet)
        {
            result.IsSuccess.Should().BeTrue();
            propInfo.GetValue(fixture).Should().Be(42);
        }
        else
        {
            result.IsSuccess.Should().BeFalse();
            result.ErrorMessage.Should().Contain("AllowSet is not enabled!");
            propInfo.GetValue(fixture).Should().Be(0);
        }
    }

    // Fixture properties are assigned through reflection by DiagnosticManager.SetProperty.
#pragma warning disable S3459
    private sealed class PlainWritable
    {
        public int Value { get; set; }
    }

    [DiagnosticClass(AllPropertiesSettable = true)]
    private sealed class AllSettable
    {
        public int Value { get; set; }
    }

    private sealed class AllowSetProperty
    {
        [Property(AllowSet = true)]
        public int Value { get; set; }
    }

    private sealed class ReadOnly
    {
        public ReadOnly()
        {
            Value = 0;
        }

        public int Value { get; }
    }
#pragma warning restore S3459
}
