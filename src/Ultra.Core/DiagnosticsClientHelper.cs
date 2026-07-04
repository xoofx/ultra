using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Diagnostics.NETCore.Client;

namespace Ultra.Core;

// Waiting for the following PR to be merged:
// - `DiagnosticsClientConnector`: https://github.com/dotnet/diagnostics/pull/5073
internal static class DiagnosticsClientHelper
{
    /// <summary>
    /// Wait for an available diagnostic endpoint to the runtime instance.
    /// </summary>
    /// <param name="client">The <see cref="DiagnosticsClient"/> instance.</param>
    /// <param name="token">The token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task the completes when a diagnostic endpoint to the runtime instance becomes available.
    /// </returns>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(WaitForConnectionAsync))]
    public static extern Task WaitForConnectionAsync(this DiagnosticsClient client, CancellationToken token);

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
    /// <param name="diagnosticPort">The diagnostic port. Only connect mode is supported.</param>
    /// <param name="ct">The token to monitor for cancellation requests.</param>
    /// <returns>A <see cref="DiagnosticsClientConnector"/> instance</returns>
    public static Task<DiagnosticsClientConnector?> FromDiagnosticPort(string diagnosticPort, CancellationToken ct)
    {
        if (diagnosticPort is null)
        {
            throw new ArgumentNullException(nameof(diagnosticPort));
        }

        IpcEndPointConfigBridge portConfig = IpcEndPointConfigBridge.Parse(diagnosticPort);

        // Listen mode (reversed diagnostics server) is not used by ultra
        Debug.Assert(portConfig.IsConnectConfig);
        return Task.FromResult<DiagnosticsClientConnector?>(new DiagnosticsClientConnector(DiagnosticsClientHelper.Create(portConfig), null));
    }
}

struct IpcEndPointConfigBridge
{
#pragma warning disable IL2026
    public static readonly Type? IpcEndpointConfigType = typeof(DiagnosticsClient).Assembly.GetType("Microsoft.Diagnostics.NETCore.Client.IpcEndpointConfig");
#pragma warning restore IL2026
    private static readonly MethodInfo? IpcEndpointConfigParseMethod = IpcEndpointConfigType?.GetMethod("Parse", BindingFlags.Static | BindingFlags.Public);
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

    public bool IsConnectConfig => (bool)IsConnectConfigProperty!.GetValue(Instance)!;
}
