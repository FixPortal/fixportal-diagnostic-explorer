using AwesomeAssertions;

namespace DiagnosticExplorer.UnitTests;

/// <summary>
///     ScopeStack is the immutable persistent stack behind TraceScope's AsyncLocal nesting.
///     It is internal (reached via InternalsVisibleTo) and its Remove must cope with the
///     scope leaving from anywhere in the stack — not just the top — because async flows can
///     dispose scopes out of order. The structural-sharing logic in Remove is the subtle part
///     these tests pin directly, rather than only through TraceScope's higher-level behaviour.
/// </summary>
public class ScopeStackTests
{
    // ScopeStack.Push needs TraceScope instances; only their reference identity matters
    // here. Constructing a TraceScope touches the global AsyncLocal, but these tests operate
    // entirely on local ScopeStack values, so that side effect is irrelevant to them.
    private static TraceScope Scope()
    {
        return new TraceScope(_ => { });
    }

    /// <summary>
    ///     The shared Empty sentinel reports itself empty with no current scope — the base case
    ///     every push/remove ultimately unwinds to.
    /// </summary>
    [Fact]
    public void Empty_HasNoCurrentAndIsEmpty()
    {
        ScopeStack.Empty.IsEmpty.Should().BeTrue();
        ScopeStack.Empty.Current.Should().BeNull();
    }

    /// <summary>
    ///     Push returns a new stack whose Current is the pushed scope, leaving the original
    ///     (Empty) untouched — confirming immutability and that Current tracks the top.
    /// </summary>
    [Fact]
    public void Push_MakesScopeCurrentAndLeavesOriginalUnchanged()
    {
        var a = Scope();

        var pushed = ScopeStack.Empty.Push(a);

        pushed.IsEmpty.Should().BeFalse();
        pushed.Current.Should().BeSameAs(a);
        ScopeStack.Empty.IsEmpty.Should().BeTrue();
    }

    /// <summary>
    ///     Removing the current (top) scope returns the parent stack, so the previously-pushed
    ///     scope becomes current again — the normal nested-dispose case.
    /// </summary>
    [Fact]
    public void Remove_TopScope_RestoresParentAsCurrent()
    {
        var a = Scope();
        var b = Scope();
        var stack = ScopeStack.Empty.Push(a).Push(b);

        var afterRemove = stack.Remove(b);

        afterRemove.Current.Should().BeSameAs(a);
    }

    /// <summary>
    ///     Removing a scope from the middle must drop only that scope while preserving the ones
    ///     above it — an out-of-order dispose. Verified by removing the middle scope, confirming
    ///     the top is still current, then removing the top and landing on the bottom (proving the
    ///     middle is gone, not merely hidden).
    /// </summary>
    [Fact]
    public void Remove_MiddleScope_DropsOnlyThatScope()
    {
        var a = Scope();
        var b = Scope();
        var c = Scope();
        var stack = ScopeStack.Empty.Push(a).Push(b).Push(c);

        var afterRemoveB = stack.Remove(b);

        afterRemoveB.Current.Should().BeSameAs(c);
        afterRemoveB.Remove(c).Current.Should().BeSameAs(a);
    }

    /// <summary>
    ///     Removing a scope that is not in the stack changes nothing, so Remove returns the very
    ///     same instance (structural sharing — no needless reallocation). BeSameAs is the point:
    ///     it proves the no-change fast path, not just value equality.
    /// </summary>
    [Fact]
    public void Remove_ScopeNotPresent_ReturnsSameInstance()
    {
        var a = Scope();
        var b = Scope();
        var other = Scope();
        var stack = ScopeStack.Empty.Push(a).Push(b);

        stack.Remove(other).Should().BeSameAs(stack);
    }

    /// <summary>
    ///     Removing the sole remaining scope unwinds to an empty stack, and removing from Empty
    ///     is a safe no-op returning Empty — the boundary conditions Dispose relies on when the
    ///     outermost scope closes.
    /// </summary>
    [Fact]
    public void Remove_LastScopeOrFromEmpty_YieldsEmpty()
    {
        var a = Scope();

        ScopeStack.Empty.Push(a).Remove(a).IsEmpty.Should().BeTrue();
        ScopeStack.Empty.Remove(a).Should().BeSameAs(ScopeStack.Empty);
    }
}
