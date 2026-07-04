using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.Hooks;

public sealed class HookProcessBitnessDetectorTests
{
    [Theory]
    [InlineData(true, HookArchitecture.X64)]
    [InlineData(false, HookArchitecture.X86)]
    public void DetectFromProbeResultMapsArchitecture(bool is64BitProcess, HookArchitecture expected)
    {
        var detector = new HookProcessBitnessDetector(_ => is64BitProcess);

        Assert.Equal(expected, detector.GetArchitectureForProcess(1234));
    }

    [Fact]
    public void DetectFromProbeWrapsNativeFailure()
    {
        var detector = new HookProcessBitnessDetector(_ => throw new InvalidOperationException("OpenProcess failed."));

        var exception = Assert.Throws<InvalidOperationException>(() => detector.GetArchitectureForProcess(1234));

        Assert.Contains("OpenProcess failed", exception.Message);
    }
}
