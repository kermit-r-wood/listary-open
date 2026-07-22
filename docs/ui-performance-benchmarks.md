# UI and Explorer performance benchmarks

These benchmarks audit the Explorer overlay hot paths independently of the SQLite search benchmark.
They are A/B eliminations: the baseline repeats the work performed before each optimization, while the
optimized side invokes the production helper or service. Every row checks that both sides produce the
same observable result.

## Repeatable commands

```powershell
dotnet run --project tools/ListaryOpen.UiPerformanceBenchmark/ListaryOpen.UiPerformanceBenchmark.csproj `
  -c Release -- --scale 1
```

`--scale` accepts 1–20. The default fixture contains 1,050 direct files/folders. Fixed `SpinWait`
proxies stand in for Win32 focus inspection, Shell COM, and WPF `UpdateLayout`; their milliseconds are
not production latency. Call counts and relative A/B time are the relevant signals. Current-directory
enumeration uses a real temporary NTFS directory and is removed after the run.

Native HookHost idle CPU can be measured without connecting to the running ListaryOpen instance:

```powershell
tools/Measure-HookHostIdle.ps1 `
  -BaselineHost artifacts/ListaryOpen-win-x64/hooks/x64/ListaryOpen.HookHost.exe `
  -OptimizedHost native/ListaryOpen.Hooks/target/debug/listary_open_hook_host.exe `
  -HookDll artifacts/ListaryOpen-win-x64/hooks/x64/ListaryOpen.Hook.dll `
  -Seconds 30 -Repeats 3
```

ReadyToRun is measured with alternating process launches of a small probe that touches the same App,
Infrastructure, query, input-coalescing, and positioning assemblies without opening the real database:

```powershell
tools/Measure-ReadyToRunStartup.ps1 -Samples 20
```

## Results on 2026-07-15

Environment: Windows 10.0.19045, .NET 8.0.423, 8 logical CPUs, Release build, scale 1.

| Scenario | Baseline | Optimized | Work reduction | Semantic result |
| --- | ---: | ---: | ---: | --- |
| Input delivery, 32,000 keys | 81.855 ms | 20.517 ms | 32,000 → 8,000 handlers (75.0%) | identical text/order |
| Non-Explorer hook filtering | 73.626 ms | 36.334 ms | 20,000 → 10,000 OS proxies (50.0%) | same Explorer events |
| Current-folder snapshot, 8 queries | 58.255 ms | 39.774 ms | 8 → 1 enumerations (87.5%) | identical ranked signatures |
| Local-first result merge, 1,000 runs | 87.664 ms | 71.761 ms | same result count | identical result sequence |
| Explorer snapshot, 2,000 UI reads | 706.548 ms | 1.475 ms | 2,000 → 0 COM proxies | identical candidates/order |
| QuickSwitch position checks | 93.636 ms | 0.449 ms | 20,000 → 80 layouts (99.6%) | identical final bounds |
| Explorer selection changes | 44.320 ms caller | 3.962 ms caller | 300 → 1 COM proxies (99.7%) | same final item; STA worker |
| Concurrent Explorer observations | 45.120 ms caller | 0.244 ms caller | 200 → 2 COM proxies (99.0%) | same fresh snapshot; STA worker |

The first Explorer-snapshot A/B exposed a residual `Directory.Exists/GetFullPath` on every cached read.
Caching the already validated normalized remembered path reduced the optimized 2,000-read measurement
from 238.619 ms to 1.475 ms without changing `LastFolder` or candidate ordering.

### Native HookHost

The old loop woke every 50 ms, rescanned every 500 ms, and reopened/queried the parent process every
500 ms. The optimized loop uses
`MsgWaitForMultipleObjectsEx`; foreground and dialog-start WinEvents request an immediate scan, a
5-second scan remains as recovery, and failed WinEvent registration automatically retains the old
500 ms interval. Parent lifetime monitoring now opens one synchronization handle and blocks on
`WaitForSingleObject(INFINITE)`, waking only when the parent exits.

- Analytical idle proxy over 8 seconds: 160 → 2 loop wakeups, 16 → 2 recovery scans, and
  16 → 0 periodic parent-process checks.
- Three 8-second CPU samples were timer-quantized: baseline median 15.625 ms; optimized median 0 ms.
- A concurrent 30-second comparison after converting parent monitoring to a process-handle wait:
  baseline 109.375 ms CPU, optimized 0 ms at the 15.625 ms process-CPU timer resolution. Both used the
  same hook DLL and isolated pipe names. The preceding event-only version measured 31.250 ms, which
  identified the remaining 500 ms parent-process poll before it was removed.
- WinEvent delivery is event-driven rather than bounded by the old 50 ms sleep, so normal dialog
  response is not traded for the idle reduction.

### ReadyToRun

Twenty alternating startup-probe samples:

| Variant | Median | P95 | Published bytes |
| --- | ---: | ---: | ---: |
| ReadyToRun off | 190.480 ms | 209.490 ms | 3,509,758 |
| ReadyToRun on | 178.800 ms | 191.040 ms | 4,989,598 |

ReadyToRun reduced median startup by 6.1% and P95 by 8.8%, at a 1.48 MB/42.2% increase for this small
probe. Retain it for Release `win-x64`; the latency improvement is repeatable and the absolute package
increase is modest. This is an assembly-loading proxy, not a claim about database-open or indexing time.

## Keyboard navigation audit

The Up/Down path makes the handled decision synchronously: the low-level hook raises
`NavigationPressed`, an immutable session snapshot atomically binds the active state to the Explorer
window, and the hook returns non-zero before posting the WPF selection move. Closing disables capture
before clearing the session, while the dispatcher rejects navigation queued by a closed or replacement
session, including a replacement using the same HWND. Unit tests cover active, inactive, and close/reopen
decisions. The remaining OS-bound gap is a live low-level-hook integration test
that proves Windows suppresses Explorer's native selection; it should remain in the manual checklist
because injecting the private hook callback would not validate Windows hook behavior.
