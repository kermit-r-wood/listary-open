using ListaryOpen.Infrastructure.Windows;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ListaryOpen.Infrastructure.Hooks;

public sealed class HookProcessBitnessDetector
{
    private readonly Func<uint, bool> _is64BitProcess;

    public HookProcessBitnessDetector()
        : this(Is64BitProcess)
    {
    }

    internal HookProcessBitnessDetector(Func<uint, bool> is64BitProcess)
    {
        _is64BitProcess = is64BitProcess;
    }

    public HookArchitecture GetArchitectureForProcess(uint processId) =>
        HookArchitectureExtensions.FromIs64Bit(_is64BitProcess(processId));

    private static bool Is64BitProcess(uint processId)
    {
        if (!Environment.Is64BitOperatingSystem)
        {
            return false;
        }

        using var process = Process.GetProcessById(checked((int)processId));
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, process.Id);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"OpenProcess failed for process {processId}. Win32 error: {Marshal.GetLastWin32Error()}.");
        }

        try
        {
            if (!NativeMethods.IsWow64Process2(handle, out var processMachine, out _))
            {
                throw new InvalidOperationException($"IsWow64Process2 failed for process {processId}. Win32 error: {Marshal.GetLastWin32Error()}.");
            }

            return processMachine == NativeMethods.ImageFileMachineUnknown;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
