using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Helpers;
using Xunit;

namespace Jellyfin.Api.Tests.Helpers;

public sealed class ConcatenatedFileStreamTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        Guid.NewGuid().ToString("N"));

    private readonly string[] _paths;

    public ConcatenatedFileStreamTests()
    {
        Directory.CreateDirectory(_directory);
        _paths = new[]
        {
            Path.Combine(_directory, "first"),
            Path.Combine(_directory, "empty"),
            Path.Combine(_directory, "last"),
        };
        File.WriteAllBytes(_paths[0], new byte[] { 0, 1, 2, 3 });
        File.WriteAllBytes(_paths[1], Array.Empty<byte>());
        File.WriteAllBytes(_paths[2], new byte[] { 4, 5, 6, 7 });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAndSeek_CrossBoundariesAndSkipEmptyFiles(bool asyncRead)
    {
        using var stream = new ConcatenatedFileStream(_paths);
        Assert.Equal(8, stream.Length);
        stream.Position = 2;
        var buffer = new byte[4];
        if (asyncRead)
        {
            await stream.ReadExactlyAsync(buffer, TestContext.Current.CancellationToken);
        }
        else
        {
            stream.ReadExactly(buffer);
        }

        Assert.Equal(new byte[] { 2, 3, 4, 5 }, buffer);
        Assert.Equal(6, stream.Position);
        Assert.Equal(7, stream.Seek(-1, SeekOrigin.End));
        Assert.Equal(7, stream.ReadByte());
        Assert.Equal(-1, stream.ReadByte());
        Assert.Equal(4, stream.Seek(-4, SeekOrigin.Current));
        Assert.Equal(4, stream.ReadByte());
        Assert.Equal(0, stream.Seek(0, SeekOrigin.Begin));
        Assert.Equal(0, stream.ReadByte());
        stream.Position = 100;
        Assert.Equal(-1, stream.ReadByte());
    }

    [Fact]
    public async Task CancelledRead_DoesNotAdvancePosition()
    {
        using var stream = new ConcatenatedFileStream(_paths);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            stream.ReadAsync(new byte[4], 0, 4, cancellation.Token));
        Assert.Equal(0, stream.Position);
        Assert.Equal(0, stream.ReadByte());
    }

    [Fact]
    public void InvalidSeek_DoesNotChangePosition()
    {
        using var stream = new ConcatenatedFileStream(_paths);
        stream.Position = 2;
        Assert.Throws<IOException>(() => stream.Seek(-3, SeekOrigin.Current));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(0, (SeekOrigin)42));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = -1);
        Assert.Throws<OverflowException>(() => stream.Seek(long.MaxValue, SeekOrigin.Current));
        Assert.Equal(2, stream.Position);
    }

    [Fact]
    public void ChangedFileLength_FailsInsteadOfReturningWrongBytes()
    {
        using var stream = new ConcatenatedFileStream(_paths);
        File.WriteAllBytes(_paths[2], new byte[2]);
        stream.Position = 4;
        Assert.Throws<IOException>(() => stream.ReadByte());
    }

    [Fact]
    public void DisposedStream_RejectsReadsAndSeeks()
    {
        var stream = new ConcatenatedFileStream(_paths);
        Assert.Equal(0, stream.ReadByte());
        stream.Dispose();
        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
        Assert.Throws<ObjectDisposedException>(() => stream.Seek(0, SeekOrigin.Begin));
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
