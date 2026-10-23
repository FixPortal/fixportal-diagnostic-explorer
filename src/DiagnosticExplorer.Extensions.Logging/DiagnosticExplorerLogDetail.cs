using System.Text;
using Microsoft.Extensions.Logging;

namespace DiagnosticExplorer.Extensions.Logging;

/// <summary>
///     Splits a rendered log message into the one-line headline the event list shows and the
///     multi-line detail the event pane shows, folding the exception, event id, state and scopes
///     into the latter.
/// </summary>
internal static class DiagnosticExplorerLogDetail
{
    public static string? GetHeadline(string? message)
    {
        // Written as an explicit null test rather than string.IsNullOrEmpty because only net10.0
        // carries the [NotNullWhen] annotation that narrows the latter; net48 does not.
        if (message is null || message.Length == 0)
        {
            return message;
        }

        int newLine = message.IndexOfAny(['\r', '\n']);
        return newLine < 0 ? message : message.Substring(0, newLine);
    }

    public static string? Create<TState>(
        string? message,
        Exception? exception,
        EventId eventId,
        TState state,
        IExternalScopeProvider? scopeProvider
    )
    {
        StringBuilder detail = new();
        string? headline = GetHeadline(message);
        if (message is not null && headline is not null && message.Length != headline.Length)
        {
            detail.AppendLine(message);
        }

        if (exception != null)
        {
            detail.AppendLine(exception.ToString());
        }

        if (eventId.Id != 0 || !string.IsNullOrEmpty(eventId.Name))
        {
            detail.AppendLine($"EventId: {eventId.Id} {eventId.Name}".TrimEnd());
        }

        AppendState(detail, state, "State", includeScalar: false);
        scopeProvider?.ForEachScope(
            (scope, builder) => AppendState(builder, scope, "Scope", includeScalar: true),
            detail
        );
        return detail.Length == 0 ? null : detail.ToString().TrimEnd();
    }

    /// <param name="detail">The builder the rendered state is appended to.</param>
    /// <param name="state">The log state or scope to render.</param>
    /// <param name="prefix">The label written before each rendered entry.</param>
    /// <param name="includeScalar">
    ///     Whether a state that is not a property list is worth recording as a bare value.
    ///     <c>BeginScope("RequestId=7")</c> is a normal thing to write and its correlation value is
    ///     lost otherwise, so scopes say yes. The message state says no: a scalar there is the
    ///     message itself, which is already the headline, and repeating it would double every
    ///     non-templated log line.
    /// </param>
    private static void AppendState<TState>(StringBuilder detail, TState state, string prefix, bool includeScalar)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> properties)
        {
            if (includeScalar && state is not null)
            {
                detail.Append(prefix).Append(": ").AppendLine(state.ToString());
            }

            return;
        }

        foreach (KeyValuePair<string, object?> property in properties)
        {
            if (property.Key == "{OriginalFormat}")
            {
                continue;
            }

            detail.Append(prefix).Append('.').Append(property.Key).Append(": ").AppendLine(property.Value?.ToString());
        }
    }
}
