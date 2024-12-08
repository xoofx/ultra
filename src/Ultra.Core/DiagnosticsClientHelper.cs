using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.NETCore.Client;

namespace Ultra.Core;

// Waiting for the following PR to be merged:
// - `DiagnosticsClientConnector`: https://github.com/dotnet/diagnostics/pull/5073
// - `ApplyStartupHook`: https://github.com/dotnet/diagnostics/pull/5086
internal static class DiagnosticsClientHelper
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(ApplyStartupHook))]
    public static extern void ApplyStartupHook(this DiagnosticsClient client, string assemblyPath);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(ApplyStartupHookAsync))]
    public static extern Task ApplyStartupHookAsync(this DiagnosticsClient client, string assemblyPath, CancellationToken token);

    /// <summary>
    /// Wait for an available diagnostic endpoint to the runtime instance.
    /// </summary>
    /// <param name="timeout">The amount of time to wait before cancelling the wait for the connection.</param>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(WaitForConnection))]
    public static extern void WaitForConnection(this DiagnosticsClient client, TimeSpan timeout);

    /// <summary>
    /// Wait for an available diagnostic endpoint to the runtime instance.
    /// </summary>
    /// <param name="token">The token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task the completes when a diagnostic endpoint to the runtime instance becomes available.
    /// </returns>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(WaitForConnectionAsync))]
    public static extern Task WaitForConnectionAsync(this DiagnosticsClient client, CancellationToken token);

    public static DiagnosticsClient Create(IpcEndpointBridge endPoint)
    {
        var ctor = typeof(DiagnosticsClient).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
            [IpcEndpointBridge.IpcEndpointType!])!;

        var result = (DiagnosticsClient)ctor.Invoke([endPoint.Instance]);
        return result;
    }

    public static DiagnosticsClient Create(IpcEndPointConfigBridge endPoint)
    {
        var ctor = typeof(DiagnosticsClient).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
            [IpcEndPointConfigBridge.IpcEndpointConfigType!])!;
        var result = (DiagnosticsClient)ctor.Invoke([endPoint.Instance]);
        return result;
    }
}

/// <summary>
/// A connector that allows to create a <see cref="DiagnosticsClient"/> from a diagnostic port.
/// </summary>
internal sealed class DiagnosticsClientConnector : IAsyncDisposable
{
    private bool _disposed;
    private readonly IAsyncDisposable? _server;

    internal DiagnosticsClientConnector(DiagnosticsClient diagnosticClient, IAsyncDisposable? server)
    {
        _server = server;
        Instance = diagnosticClient;
    }

    /// <summary>
    /// Gets the <see cref="DiagnosticsClient"/> instance.
    /// </summary>
    public DiagnosticsClient Instance { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_server != null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
        }

        _disposed = true;
    }

    /// <summary>
    /// Create a new <see cref="DiagnosticsClientConnector"/> instance using the specified diagnostic port.
    /// </summary>
    /// <param name="diagnosticPort">The diagnostic port.</param>
    /// <param name="ct">The token to monitor for cancellation requests.</param>
    /// <returns>A <see cref="DiagnosticsClientConnector"/> instance</returns>
    public static async Task<DiagnosticsClientConnector?> FromDiagnosticPort(string diagnosticPort, CancellationToken ct)
    {
        if (diagnosticPort is null)
        {
            throw new ArgumentNullException(nameof(diagnosticPort));
        }

        IpcEndPointConfigBridge portConfig = IpcEndPointConfigBridge.Parse(diagnosticPort);

        if (portConfig.IsListenConfig)
        {
            string fullPort = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? portConfig.Address : Path.GetFullPath(portConfig.Address);
            ReversedDiagnosticsServerBridge server = ReversedDiagnosticsServerBridge.Create(fullPort);
            server.Start();

            try
            {
                IpcEndpointInfoBridge endpointInfo = await server.AcceptAsync(ct).ConfigureAwait(false);
                return new DiagnosticsClientConnector(DiagnosticsClientHelper.Create(endpointInfo.Endpoint), (IAsyncDisposable?)server.Instance);
            }
            catch (TaskCanceledException)
            {
                //clean up the server
                await server.DisposeAsync().ConfigureAwait(false);
                if (!ct.IsCancellationRequested)
                {
                    throw;
                }
                return null;
            }
        }

        Debug.Assert(portConfig.IsConnectConfig);
        return new DiagnosticsClientConnector(DiagnosticsClientHelper.Create(portConfig), null);
    }
}

struct IpcEndPointConfigBridge
{
#pragma warning disable IL2026
    public static readonly Type? IpcEndpointConfigType = typeof(DiagnosticsClient).Assembly.GetType("Microsoft.Diagnostics.NETCore.Client.IpcEndpointConfig");
#pragma warning restore IL2026
    private static readonly MethodInfo? IpcEndpointConfigParseMethod = IpcEndpointConfigType?.GetMethod("Parse", BindingFlags.Static | BindingFlags.Public);
    private static readonly PropertyInfo? AddressProperty = IpcEndpointConfigType?.GetProperty("Address");
    private static readonly PropertyInfo? IsListenConfigProperty = IpcEndpointConfigType?.GetProperty("IsListenConfig");
    private static readonly PropertyInfo? IsConnectConfigProperty = IpcEndpointConfigType?.GetProperty("IsConnectConfig");

    public readonly object Instance;

    private IpcEndPointConfigBridge(object instance)
    {
        Instance = instance;
    }

    public static IpcEndPointConfigBridge Parse(string diagnosticPort)
    {
        if (IpcEndpointConfigParseMethod is null)
        {
            throw new MissingMethodException("IpcEndpointConfig.Parse method not found");
        }

        return new IpcEndPointConfigBridge(IpcEndpointConfigParseMethod.Invoke(null, [diagnosticPort])!);
    }

    public string Address => (string)AddressProperty!.GetValue(Instance)!;

    public bool IsListenConfig => (bool)IsListenConfigProperty!.GetValue(Instance)!;

    public bool IsConnectConfig => (bool)IsConnectConfigProperty!.GetValue(Instance)!;
}

struct ReversedDiagnosticsServerBridge
{
    public static readonly Type? ReversedDiagnosticsServerType = typeof(DiagnosticsClient).Assembly.GetType("Microsoft.Diagnostics.NETCore.Client.ReversedDiagnosticsServer");
    private static readonly ConstructorInfo? ReversedDiagnosticsServerConstructor = ReversedDiagnosticsServerType?.GetConstructor([typeof(string)]);
    private static readonly MethodInfo? ReversedDiagnosticsServerStartMethod = ReversedDiagnosticsServerType?.GetMethod("Start", []);
    private static readonly MethodInfo? ReversedDiagnosticsServerAcceptAsyncMethod = ReversedDiagnosticsServerType?.GetMethod("AcceptAsync", [typeof(CancellationToken)]);
    private static readonly MethodInfo? ReversedDiagnosticsServerDisposeAsyncMethod = ReversedDiagnosticsServerType?.GetMethod("DisposeAsync");

    public readonly object Instance;

    private ReversedDiagnosticsServerBridge(object instance)
    {
        Instance = instance;
    }
    public static ReversedDiagnosticsServerBridge Create(string address)
    {
        if (ReversedDiagnosticsServerConstructor is null)
        {
            throw new MissingMethodException("ReversedDiagnosticsServer constructor not found");
        }
        return new ReversedDiagnosticsServerBridge(ReversedDiagnosticsServerConstructor.Invoke([address])!);
    }

    public void Start()
    {
        ReversedDiagnosticsServerStartMethod!.Invoke(Instance, []);
    }
    public async Task<IpcEndpointInfoBridge> AcceptAsync(CancellationToken ct)
    {
        Task task = (Task)ReversedDiagnosticsServerAcceptAsyncMethod!.Invoke(Instance, [ct])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        return new IpcEndpointInfoBridge(result);
    }

    public Task DisposeAsync()
    {
        return (Task)ReversedDiagnosticsServerDisposeAsyncMethod!.Invoke(Instance, [])!;
    }
}

struct IpcEndpointInfoBridge
{
    public static readonly Type? IpcEndpointInfoType = typeof(DiagnosticsClient).Assembly.GetType("Microsoft.Diagnostics.NETCore.Client.IpcEndpointInfo");
    private static readonly PropertyInfo? EndpointProperty = IpcEndpointInfoType?.GetProperty("Endpoint");

    public readonly object Instance;

    public IpcEndpointInfoBridge(object instance)
    {
        Instance = instance;
    }
    public IpcEndpointBridge Endpoint => new(EndpointProperty!.GetValue(Instance)!);
}

struct IpcEndpointBridge
{
    public static readonly Type IpcEndpointType = typeof(DiagnosticsClient).Assembly.GetType("Microsoft.Diagnostics.NETCore.Client.IpcEndpoint")!;
    public readonly object Instance;

    public IpcEndpointBridge(object instance)
    {
        Instance = instance;
    }
}