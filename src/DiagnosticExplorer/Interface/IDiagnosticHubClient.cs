#region Copyright

// Diagnostic Explorer, a .Net diagnostic toolset
// Copyright (C) 2010 Cameron Elliot
//
// This file is part of Diagnostic Explorer.
//
// Diagnostic Explorer is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Diagnostic Explorer is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public License
// along with Diagnostic Explorer.  If not, see <http://www.gnu.org/licenses/>.
//
// http://diagexplorer.sourceforge.net/

#endregion

using DiagnosticExplorer.Logging;

namespace DiagnosticExplorer;

/// <summary>
///     Calls the service makes INTO an agent. The request/response members return a value, which
///     makes them SignalR client results: SignalR owns the correlation and the disconnect
///     handling that this codebase previously hand-rolled with a request id and a shared
///     AsyncResultBucket.
/// </summary>
/// <remarks>
///     SignalR does NOT own the timeout, which the AsyncResultBucket did (a 10 second ceiling).
///     A client result invoked without a token waits forever: SignalR's TypedClientBuilder passes
///     CancellationToken.None when the interface method declares no trailing CancellationToken
///     (TypedClientBuilder.cs, release/8.0, lines 148-152 and 214-222), and an agent that never
///     sends a completion — because its response failed to serialise, say — then parks the caller
///     indefinitely on a connection that is otherwise healthy. Hence the trailing token on every
///     member that waits for a value; TypedClientBuilder strips it from the wire arguments and
///     hands it to InvokeCoreAsync.
/// </remarks>
public interface IDiagnosticHubClient
{
    Task<DiagnosticResponse> GetDiagnostics(CancellationToken cancel);

    /// <summary>
    ///     Renders a value named by a chain of diagnostic paths, so it can be inspected on its own.
    /// </summary>
    Task<DrillDownResponse> GetDrillDown(DrillDownRequest request, CancellationToken cancel);

    // objectPaths is the drilldown the operator triggered this from, empty for the main view. It
    // has to travel with the action: inside a drilldown, path names a property of the drilled-into
    // object, and resolving it against the process's registered objects would miss - or, where a
    // same-named property exists there, hit the wrong object entirely.
    //
    // requestId identifies the OPERATOR'S action, not the invocation. The service's timeout does
    // not reach the agent, so a request that outruns it keeps running while its caller is told it
    // failed; a retry carrying the same id joins the first instead of running the body twice.
    Task<OperationResponse> ExecuteOperation(
        string requestId,
        string[] objectPaths,
        string path,
        string operation,
        string[] arguments,
        CancellationToken cancel
    );
    Task<OperationResponse> SetProperty(
        string requestId,
        string[] objectPaths,
        string path,
        string value,
        CancellationToken cancel
    );
    Task SubscribeEvents();
    Task UnsubscribeEvents();
}

public interface IDiagnosticHubServer
{
    Task<RpcResult<RegistrationResponse>> Register(Registration registration);
    Task<RpcResult> Deregister(Registration registration);

    // Sent as a typed array rather than a protobuf-compressed blob: MessagePack frames it on the
    // wire, so the manual serialize-and-gzip step is gone.
    Task<RpcResult> LogEvents(DiagnosticMsg[] messages);

    // The realtime event feed, replacing SetEvents/StreamEvents over SystemEvent. The stream now
    // comes from LogEventStore rather than EventSinkRepo, so it carries the routing in force and a
    // sequence number per event: a subscriber can tell a replayed event from a live one, and can
    // reconcile after a reconnect instead of starting blank.
    //
    // The legacy log4net DiagnosticAppender writes only to EventSinkRepo and therefore no longer
    // reaches this feed. It stays in the package, but a host that wants realtime events must use
    // RoutingDiagnosticAppender (or the NLog / Serilog / Microsoft.Extensions.Logging adapters).
    Task InitializeLogStream(LogStreamInitialization initialization);
    Task StreamLogEvents(LogStreamEvent[] events);
}

public class RpcResult<T> : RpcResult
{
    public T Response { get; set; }

    public static RpcResult<T> Success(T result)
    {
        return new RpcResult<T> { IsSuccess = true, Response = result };
    }

    public static RpcResult<T> Success(string requestId, T result)
    {
        return new RpcResult<T>
        {
            RequestId = requestId,
            IsSuccess = true,
            Response = result,
        };
    }

    public static new RpcResult<T> Fail(string requestId, string message, string detail)
    {
        return new RpcResult<T>
        {
            RequestId = requestId,
            IsSuccess = false,
            Message = message,
            Detail = detail,
        };
    }

    public static new RpcResult<T> Fail(string requestId, Exception ex)
    {
        return new RpcResult<T>
        {
            RequestId = requestId,
            IsSuccess = false,
            Message = ex.Message,
            Detail = ex.ToString(),
        };
    }
}

public class RpcResult
{
    public string RequestId { get; set; }
    public bool IsSuccess { get; set; }
    public string Message { get; set; }
    public string Detail { get; set; }

    public static RpcResult Success(string requestId = null)
    {
        return new RpcResult { RequestId = requestId, IsSuccess = true };
    }

    public static RpcResult Fail(string requestId, string message, string detail)
    {
        return new RpcResult
        {
            RequestId = requestId,
            IsSuccess = false,
            Message = message,
            Detail = detail,
        };
    }

    public static RpcResult Fail(string requestId, Exception ex)
    {
        return new RpcResult
        {
            RequestId = requestId,
            IsSuccess = false,
            Message = ex.Message,
            Detail = ex.ToString(),
        };
    }
}
