using System.ComponentModel;
using System.Diagnostics;

namespace ListaryOpen.Infrastructure.Hooks;

public sealed record HookHostLaunchRequest
{
    public HookHostLaunchRequest(string hostExePath, string pipeName, string hookDllPath)
    {
        if (string.IsNullOrWhiteSpace(hostExePath))
        {
            throw new ArgumentException("Hook host path cannot be empty.", nameof(hostExePath));
        }

        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("Pipe name cannot be empty.", nameof(pipeName));
        }

        if (string.IsNullOrWhiteSpace(hookDllPath))
        {
            throw new ArgumentException("Hook DLL path cannot be empty.", nameof(hookDllPath));
        }

        HostExePath = hostExePath;
        PipeName = pipeName;
        HookDllPath = hookDllPath;
    }

    public string HostExePath { get; }

    public string PipeName { get; }

    public string HookDllPath { get; }
}

public class HookHostProcessFactory
{
    /// <summary>
    /// Short wait for graceful host exit. On timeout we detach without killing so
    /// native vtable restore can still finish after the parent process exits.
    /// </summary>
    private static readonly TimeSpan TerminationTimeout = TimeSpan.FromSeconds(1.5);

    public virtual ProcessStartInfo CreateStartInfo(
        string hostExePath,
        string pipeName,
        string hookDllPath,
        bool elevated,
        IEnumerable<HookHostLaunchRequest>? childHosts = null,
        int? parentProcessId = null,
        string? secret = null)
    {
        if (string.IsNullOrWhiteSpace(hostExePath))
        {
            throw new ArgumentException("Hook host path cannot be empty.", nameof(hostExePath));
        }

        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("Pipe name cannot be empty.", nameof(pipeName));
        }

        if (string.IsNullOrWhiteSpace(hookDllPath))
        {
            throw new ArgumentException("Hook DLL path cannot be empty.", nameof(hookDllPath));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = hostExePath,
            UseShellExecute = elevated,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        if (elevated)
        {
            startInfo.Verb = "runas";
        }

        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--dll");
        startInfo.ArgumentList.Add(hookDllPath);

        if (parentProcessId is not null && !string.IsNullOrWhiteSpace(secret))
        {
            startInfo.ArgumentList.Add("--parent-pid");
            startInfo.ArgumentList.Add(parentProcessId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--secret");
            startInfo.ArgumentList.Add(secret);
        }

        if (childHosts is not null)
        {
            foreach (var childHost in childHosts)
            {
                ArgumentNullException.ThrowIfNull(childHost);
                startInfo.ArgumentList.Add("--launch-host");
                startInfo.ArgumentList.Add(childHost.HostExePath);
                startInfo.ArgumentList.Add("--launch-pipe");
                startInfo.ArgumentList.Add(childHost.PipeName);
                startInfo.ArgumentList.Add("--launch-dll");
                startInfo.ArgumentList.Add(childHost.HookDllPath);
            }
        }

        return startInfo;
    }

    public virtual Process? Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        return Process.Start(startInfo);
    }

    public virtual void Terminate(Process process)
    {
        TerminateCore(process, waitForGracefulExit: false, TerminationTimeout);
    }

    public virtual void WaitForGracefulExitOrTerminate(Process process)
    {
        WaitForGracefulExitOrTerminate(process, TerminationTimeout);
    }

    public virtual void WaitForGracefulExitOrTerminate(Process process, TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        TerminateCore(process, waitForGracefulExit: true, timeout);
    }

    private void TerminateCore(Process process, bool waitForGracefulExit, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            if (IsRunning(process))
            {
                if (waitForGracefulExit)
                {
                    // An authenticated graceful shutdown may legitimately take
                    // longer when a target UI thread is suspended. Killing here
                    // would bypass native vtable restoration and hook rundown.
                    // The host also monitors its parent and will finish cleanup
                    // after the app exits, so timeout means detach, never kill.
                    var waitMs = (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
                    if (waitMs > 0)
                    {
                        _ = process.WaitForExit(waitMs);
                    }

                    return;
                }

                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(checked((int)TerminationTimeout.TotalMilliseconds));
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    public virtual bool IsRunning(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Win32Exception)
        {
            return true;
        }
    }
}
