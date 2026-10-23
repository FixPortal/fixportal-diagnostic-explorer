using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;

namespace DiagnosticExplorer.UnitTests;

/// <summary>
///     TraceScope is the library's hierarchical tracing primitive: scopes nest via an
///     AsyncLocal stack, only the root scope emits on dispose, and the rendered output can
///     collapse fast child scopes to a single BEGIN/END line. These tests inject the trace
///     action to capture the rendered text and assert on the stable structural markers
///     (BEGIN/END, the suppression "*", trace lines) rather than the timing values, which are
///     wall-clock dependent. Every scope is disposed via using, so the AsyncLocal stack returns
///     to empty after each test.
/// </summary>
public class TraceScopeTests
{
    /// <summary>
    ///     Scope age is elapsed time from its injected provider, independent of machine wall time.
    /// </summary>
    [Fact]
    public void Age_UsesInjectedTimeProvider()
    {
        FakeTimeProvider timeProvider = new(new DateTimeOffset(2026, 8, 12, 9, 30, 0, TimeSpan.Zero));
        using var scope = new TraceScope(timeProvider, "Timed", _ => { });

        timeProvider.Advance(TimeSpan.FromSeconds(3));

        scope.Age.Should().Be(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    ///     Disposal owns the timer lifecycle lock, so a late timer request is ignored rather than
    ///     creating an orphaned timer after the scope has closed.
    /// </summary>
    [Fact]
    public void StartAutoTraceTimer_AfterDispose_IsIgnored()
    {
        var scope = new TraceScope(_ => { });
        scope.Dispose();

        scope.StartAutoTraceTimer(TimeSpan.Zero);

        scope.HasAutoTraceTimer.Should().BeFalse();
    }

    /// <summary>
    ///     Outside any scope there is nothing to attach to, so the static Trace is a no-op that
    ///     returns null — callers can trace unconditionally without guarding for "no active scope".
    /// </summary>
    [Fact]
    public void Trace_OutsideAnyScope_ReturnsNull()
    {
        TraceScope.Current.Should().BeNull();

        TraceScope.Trace("orphan").Should().BeNull();
    }

    /// <summary>
    ///     Entering a scope makes it Current; leaving it (dispose) clears Current back to null.
    ///     This is the contract the AsyncLocal stack exists to provide.
    /// </summary>
    [Fact]
    public void Current_TracksTheActiveScope_AndClearsOnDispose()
    {
        using (var scope = new TraceScope(_ => { }))
        {
            TraceScope.Current.Should().BeSameAs(scope);
        }

        TraceScope.Current.Should().BeNull();
    }

    /// <summary>
    ///     Nested scopes form a stack: the inner scope is Current while open, and disposing it
    ///     restores the outer scope as Current — verifying push/pop ordering through the public API.
    /// </summary>
    [Fact]
    public void NestedScopes_RestoreOuterScopeOnInnerDispose()
    {
        using var outer = new TraceScope(_ => { });

        using (var inner = new TraceScope(_ => { }))
        {
            TraceScope.Current.Should().BeSameAs(inner);
        }

        TraceScope.Current.Should().BeSameAs(outer);
    }

    /// <summary>
    ///     Scopes nest correctly across await points within one logical async flow: the AsyncLocal
    ///     stack flows into the continuation, so Current tracks the active scope before and after each
    ///     await and disposing the inner scope restores the outer. This documents that the realistic
    ///     async usage is sound — the AsyncLocal "mutations don't flow back" limitation only affects
    ///     forked/parallel contexts, which is not how using-based trace scopes are used.
    /// </summary>
    [Fact]
    public async Task NestedScopes_AcrossAwaits_TrackCurrentWithinOneAsyncFlow()
    {
        using var outer = new TraceScope(_ => { });
        await Task.Yield();
        TraceScope.Current.Should().BeSameAs(outer);

        using (var inner = new TraceScope(_ => { }))
        {
            await Task.Yield();
            TraceScope.Current.Should().BeSameAs(inner);
        }

        await Task.Yield();
        TraceScope.Current.Should().BeSameAs(outer);
    }

    /// <summary>
    ///     The root scope, on dispose, renders its name and every traced line and hands the text
    ///     to its trace action — the actual diagnostic output a consumer sees. Asserts the BEGIN/END
    ///     framing and that both traced messages appear.
    /// </summary>
    [Fact]
    public void RootScope_OnDispose_RendersBeginEndAndTracedLines()
    {
        string? captured = null;

        using (new TraceScope("Work", s => captured = s))
        {
            TraceScope.Trace("step one");
            TraceScope.Trace("step two");
        }

        captured.Should().NotBeNull();
        captured.Should().Contain("BEGIN Work");
        captured.Should().Contain("step one");
        captured.Should().Contain("step two");
        captured.Should().Contain("END Work");
    }

    /// <summary>
    ///     Only the root scope emits on dispose; a nested child stays silent unless it is created
    ///     with forceTrace. This is what keeps a deep call tree from emitting one trace per frame —
    ///     the whole tree is rendered once, by the root. Parameterized over the forceTrace flag.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ChildScope_EmitsOnDisposeOnlyWhenForced(bool forceTrace, bool expectedEmit)
    {
        var childEmitted = false;

        using var root = new TraceScope("Root", _ => { });

        using (new TraceScope("Child", _ => childEmitted = true, forceTrace))
        {
            TraceScope.Trace("child");
        }

        childEmitted.Should().Be(expectedEmit);
    }

    /// <summary>
    ///     A child whose duration is under SuppressDetailThreshold is collapsed in the root's
    ///     output to a single "BEGIN/END* Child" line, and its own traced detail is omitted — the
    ///     noise-reduction feature for fast operations. A 10-minute threshold guarantees the
    ///     microsecond-long child is always under it, keeping the test deterministic.
    /// </summary>
    [Fact]
    public void ChildScope_UnderSuppressThreshold_IsCollapsedInRootOutput()
    {
        string? captured = null;

        using (new TraceScope("Root", s => captured = s))
        {
            using var child = new TraceScope("Child", null);
            child.SuppressDetailThreshold = TimeSpan.FromMinutes(10);
            TraceScope.Trace("hidden detail");
        }

        captured.Should().NotBeNull();
        captured.Should().Contain("BEGIN/END* Child");
        captured.Should().NotContain("hidden detail");
    }

    /// <summary>
    ///     Rendering a still-running child under SuppressDetailThreshold must not throw. The auto-trace
    ///     timer renders the tree while a child is still open (its _disposed is null); FullTraceRequired
    ///     read that null _disposed.Value and threw, which TraceMessage swallowed, so the auto-trace
    ///     feature silently produced nothing. Reproduced deterministically here by rendering the root via
    ///     ToString() with the child still open. (M29)
    /// </summary>
    [Fact]
    public void RenderingAnOpenChildUnderSuppressThreshold_DoesNotThrow()
    {
        using var root = new TraceScope("Root", _ => { });
        using var child = new TraceScope("Child", null);
        child.SuppressDetailThreshold = TimeSpan.FromMinutes(10);

        // child is intentionally NOT disposed — this is the auto-trace render state.
        // ReSharper disable once AccessToDisposedClosure -- assertion runs before the using scope exits.
        Action render = () => _ = root.ToString();

        render.Should().NotThrow();
        root.ToString().Should().Contain("BEGIN Root");
    }
}
