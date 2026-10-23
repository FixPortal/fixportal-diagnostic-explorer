using System.Diagnostics;
using Diagnostic.Service.ClientHandlers;
using DiagnosticExplorer;
using DiagnosticExplorer.Logging;
using log4net;
using Microsoft.AspNetCore.SignalR;

namespace Diagnostic.Service.Hubs;

public class DiagnosticHub : Hub<IDiagnosticHubClient>, IDiagnosticHubServer
{
    private static readonly ILog _log = LogManager.GetLogger(typeof(DiagnosticHub));
    private readonly IHubContext<DiagnosticHub, IDiagnosticHubClient> _hubContext;
    private readonly RetroManager _retroManager;
    private readonly RealtimeManager _rtManager;

    public DiagnosticHub(
        RealtimeManager rtManager,
        RetroManager retroManager,
        IHubContext<DiagnosticHub, IDiagnosticHubClient> hubContext
    )
    {
        _rtManager = rtManager;
        _retroManager = retroManager;
        _hubContext = hubContext;
    }

    // Not async: the body is synchronous (CS1998). SignalR awaits the returned Task either way.
    public Task<RpcResult<RegistrationResponse>> Register(Registration registration)
    {
        RegistrationResponse response = new(TimeSpan.FromSeconds(20));
        try
        {
            _rtManager.Register(registration, Context.ConnectionId);
            return Task.FromResult(RpcResult<RegistrationResponse>.Success(response));
        }
        catch (Exception ex)
        {
            return Task.FromResult(RpcResult<RegistrationResponse>.Fail(null, ex.Message, ex.ToString()));
        }
    }

    public Task<RpcResult> Deregister(Registration registration)
    {
        try
        {
            _rtManager.Deregister(registration);
            return Task.FromResult(RpcResult.Success());
        }
        catch (Exception ex)
        {
            return Task.FromResult(RpcResult.Fail(null, ex));
        }
    }

    // Not async: the body is synchronous (CS1998). SignalR awaits the returned Task either way.
    public Task<RpcResult> LogEvents(DiagnosticMsg[] messages)
    {
        try
        {
            if (messages?.Any() == true)
            {
                _rtManager.RegisterAlertLevel(Context.ConnectionId, messages);

                _retroManager.LogEvents(messages);
            }

            return Task.FromResult(RpcResult.Success());
        }
        catch (Exception ex)
        {
            _log.Error(ex);
            return Task.FromResult(RpcResult.Fail(null, ex));
        }
    }

    // The three *Return callbacks that used to complete a pending request here are gone: with
    // client results the agent returns its value from the invocation itself, so there is nothing
    // for the service to correlate.

    public Task InitializeLogStream(LogStreamInitialization initialization)
    {
        // GetClientHandler returns null on a disconnect/registration race; guard like the other
        // hub methods rather than NRE-ing inside the invocation.
        _rtManager.GetClientHandler(Context.ConnectionId)?.InitializeLogStream(initialization);
        return Task.CompletedTask;
    }

    public Task StreamLogEvents(LogStreamEvent[] events)
    {
        _rtManager.GetClientHandler(Context.ConnectionId)?.StreamLogEvents(events);
        return Task.CompletedTask;
    }

    // The client proxy comes from IHubContext, NOT from Clients.Caller. For the duration of
    // OnConnectedAsync SignalR substitutes a NoInvokeSingleClientProxy, so every client result
    // invoked through it throws "Client results inside OnConnectedAsync Hub methods are not
    // allowed." - and permanently, because the handler holds the captured reference for the life
    // of the connection and it never becomes usable. IHubContext resolves the connection by id
    // instead, and can invoke from the moment the connection exists.
    public override Task OnConnectedAsync()
    {
        _rtManager.AddDiagnosticClient(
            new DiagnosticClientHandler(Context, _hubContext.Clients.Client(Context.ConnectionId))
        );
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        Trace.TraceInformation("Disconnected");
        if (exception != null)
        {
            Trace.TraceError(exception.ToString());
        }

        return base.OnDisconnectedAsync(exception);
    }
}
