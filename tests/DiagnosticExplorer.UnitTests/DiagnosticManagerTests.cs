using System.Reflection;
using AwesomeAssertions;

namespace DiagnosticExplorer.UnitTests;

public class DiagnosticManagerTests
{
    [Fact]
    public void IsMethodValidOperationTarget_FiltersOutGenericMethods()
    {
        var obj = new GenericAndNonGenericMethodsClass();
        var registered = new RegisteredObject(obj, "TestCategory", "TestBag");
        var response = DiagnosticManager.GetDiagnostics(new[] { registered });

        response.Should().NotBeNull();
        response.OperationSets.Should().HaveCount(1);
        var opSet = response.OperationSets[0];

        opSet
            .Operations.Should()
            .Contain(o => o.Signature.StartsWith(nameof(GenericAndNonGenericMethodsClass.NonGenericMethod)));
        opSet
            .Operations.Should()
            .NotContain(o => o.Signature.StartsWith(nameof(GenericAndNonGenericMethodsClass.GenericMethod)));
    }

    [Fact]
    public void ExecuteOperation_UnwrapsTargetInvocationException_ForCustomParsableType()
    {
        var obj = new OperationWithCustomParsableType();
        var registered = new RegisteredObject(obj, "TestCategory", "TestBag");

        // Let's obtain the operation signature
        var response = DiagnosticManager.GetDiagnostics(new[] { registered });
        response.OperationSets.Should().HaveCount(1);
        var signature = response.OperationSets[0].Operations[0].Signature;

        var result = DiagnosticManager.ExecuteOperation(
            new[] { registered },
            "TestCategory|TestBag",
            signature,
            new[] { "invalid-val" }
        );

        result.IsSuccess.Should().BeFalse();
        result
            .ErrorMessage.Should()
            .Match(m =>
                m.Contains("can't convert 'invalid-val' to CustomParsableType")
                || m.Contains("Parse failed for value: invalid-val")
            );
        result.ErrorDetail.Should().Contain(nameof(CustomParseException));
        result.ErrorDetail.Should().Contain("Parse failed for value: invalid-val");
        result.ErrorDetail.Should().NotContain(nameof(TargetInvocationException));
    }

    [Fact]
    public void ExecuteOperation_UnwrapsTargetInvocationException_ForThrowingOperation()
    {
        var obj = new ThrowingOperation();
        var registered = new RegisteredObject(obj, "TestCategory", "TestBag");

        var response = DiagnosticManager.GetDiagnostics(new[] { registered });
        response.OperationSets.Should().HaveCount(1);
        var signature = response.OperationSets[0].Operations[0].Signature;

        var result = DiagnosticManager.ExecuteOperation(
            new[] { registered },
            "TestCategory|TestBag",
            signature,
            Array.Empty<string>()
        );

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("Operation failed explicitly");
        result.ErrorDetail.Should().Contain(nameof(InvalidOperationException));
        result.ErrorDetail.Should().NotContain(nameof(TargetInvocationException));
    }

    /// <summary>
    ///     The [DiagnosticMethod] selection gate in IsMethodValidOperationTarget is the single point
    ///     of failure standing between an arbitrary public method and anonymous hub execution:
    ///     ExecuteOperation matches by Signature and never re-checks the attribute, so a public method
    ///     without [DiagnosticMethod] must be absent from the OperationSet outright — asserted here by
    ///     name, independent of any Operations[0] indexing or ordering. (DE-12b)
    /// </summary>
    [Fact]
    public void OperationSet_ExcludesPublicMethodWithoutDiagnosticMethodAttribute()
    {
        var obj = new MixedAttributedMethodsClass();
        var registered = new RegisteredObject(obj, "TestCategory", "TestBag");
        var response = DiagnosticManager.GetDiagnostics(new[] { registered });

        response.OperationSets.Should().HaveCount(1);
        var opSet = response.OperationSets[0];

        opSet.Operations.Should().Contain(o => o.Signature.StartsWith(nameof(MixedAttributedMethodsClass.Decorated)));
        opSet
            .Operations.Should()
            .NotContain(o => o.Signature.StartsWith(nameof(MixedAttributedMethodsClass.NotDecorated)));
    }

    [Fact]
    public void EventSinkRepo_CanBeDisposed()
    {
        var repo = new EventSinkRepo();
        var act = () => repo.Dispose();
        act.Should().NotThrow();
    }

    public class GenericAndNonGenericMethodsClass
    {
        [DiagnosticMethod]
        public void NonGenericMethod()
        {
            // Empty method for testing
        }

        [DiagnosticMethod]
        public T GenericMethod<T>(T val)
        {
            return val;
        }
    }

    public class CustomParseException : Exception
    {
        public CustomParseException(string message)
            : base(message) { }
    }

    public class CustomParsableType
    {
        public static CustomParsableType Parse(string value)
        {
            throw new CustomParseException("Parse failed for value: " + value);
        }
    }

    public class OperationWithCustomParsableType
    {
        [DiagnosticMethod]
#pragma warning disable IDE0060
        public void Run(CustomParsableType arg)
#pragma warning restore IDE0060
        {
            // Empty method for testing
        }
    }

    public class ThrowingOperation
    {
        [DiagnosticMethod]
        public void Run()
        {
            throw new InvalidOperationException("Operation failed explicitly");
        }
    }

    public class MixedAttributedMethodsClass
    {
        [DiagnosticMethod]
        public void Decorated()
        {
            // Empty method for testing
        }

        public void NotDecorated()
        {
            // Empty method for testing — must never appear in the OperationSet (DE-12b)
        }
    }
}
