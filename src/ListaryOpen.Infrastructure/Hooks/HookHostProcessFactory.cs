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
    public virtual ProcessStartInfo CreateStartInfo(
        string hostExePath,
        string pipeName,
        string hookDllPath,
        bool elevated,
        IEnumerable<HookHostLaunchRequest>? childHosts = null)
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
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            if (IsRunning(process))
            {
                process.Kill(entireProcessTree: true);
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
