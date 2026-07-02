using ListaryOpen.Core.Indexing;

namespace ListaryOpen.Core.Tests.Indexing;

public sealed class IndexRootTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative")]
    public void ConstructorRejectsInvalidPaths(string path)
    {
        Assert.Throws<ArgumentException>(() => new IndexRoot(path));
    }

    [Fact]
    public void ConstructorNormalizesPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid(), ".");

        var indexRoot = new IndexRoot(root);

        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), indexRoot.Path);
    }
}
