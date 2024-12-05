using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ultra.Sampler.MacOS;

namespace Ultra.Sampler;

public abstract class UltraSampler
{
    private static readonly object Lock = new();

    public bool IsEnabled { get; private set; }

    public static UltraSampler Instance { get; } = OperatingSystem.IsMacOS() ? new MacOSUltraSampler() : throw new PlatformNotSupportedException();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)], EntryPoint = "ultra_sampler_start")]
    internal static void NativeStart() => Instance.Start();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)], EntryPoint = "ultra_sampler_stop")]
    internal static void NativeStop() => Instance.Stop();

    public void Start()
    {
        lock (Lock)
        {
            StartImpl();
        }
    }

    public void Stop()
    {
        lock (Lock)
        {
            StopImpl();
        }
    }

    public void Enable()
    {
        lock (Lock)
        {
            IsEnabled = true;
            EnableImpl();
        }
    }

    public void Disable()
    {
        lock (Lock)
        {
            IsEnabled = false;
            DisableImpl();
        }
    }

    protected abstract void StartImpl();

    protected abstract void StopImpl();

    protected abstract void EnableImpl();

    protected abstract void DisableImpl();
}