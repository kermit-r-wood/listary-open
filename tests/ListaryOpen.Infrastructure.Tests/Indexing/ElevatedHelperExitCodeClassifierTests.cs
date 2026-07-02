using System.ComponentModel;
using ListaryOpen.Indexer.Elevated;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class ElevatedHelperExitCodeClassifierTests
{
    [Fact]
    public void IsNtfsAccessFailureReturnsTrueForWin32AndPermissionFailures()
    {
        Assert.True(ElevatedHelperExitCodeClassifier.IsNtfsAccessFailure(new Win32Exception(5)));
        Assert.True(ElevatedHelperExitCodeClassifier.IsNtfsAccessFailure(new UnauthorizedAccessException()));
    }

    [Fact]
    public void IsNtfsAccessFailureReturnsFalseForDataShapeFailures()
    {
        Assert.False(ElevatedHelperExitCodeClassifier.IsNtfsAccessFailure(new InvalidDataException()));
        Assert.False(ElevatedHelperExitCodeClassifier.IsNtfsAccessFailure(new IOException()));
    }
}
