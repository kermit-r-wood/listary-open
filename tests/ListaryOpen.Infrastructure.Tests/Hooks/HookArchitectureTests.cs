using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.Hooks;

public sealed class HookArchitectureTests
{
    [Theory]
    [InlineData(HookArchitecture.X64, "x64")]
    [InlineData(HookArchitecture.X86, "x86")]
    public void FolderNameReturnsPackagingFolder(HookArchitecture architecture, string expected)
    {
        Assert.Equal(expected, architecture.ToFolderName());
    }

    [Theory]
    [InlineData(true, HookArchitecture.X64)]
    [InlineData(false, HookArchitecture.X86)]
    public void FromIs64BitMapsToArchitecture(bool is64Bit, HookArchitecture expected)
    {
        Assert.Equal(expected, HookArchitectureExtensions.FromIs64Bit(is64Bit));
    }
}
