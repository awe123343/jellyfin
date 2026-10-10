using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Api.Helpers;

/// <summary>
/// Prototype: exposes original clip bytes without adjusting media timestamps.
/// </summary>
internal sealed class ConcatenatedFileStream : Stream
{
    private readonly string[] _paths;
    private readonly long[] _ends;
    private FileStream? _current;
    private int _index = -1;
    private long _position;
    private bool _disposed;

    public ConcatenatedFileStream(IReadOnlyList<string> paths)
    {
        _paths = paths.ToArray();
        _ends = new long[_paths.Length];
        long length = 0;
        for (var i = 0; i < _paths.Length; i++)
        {
            length = checked(length + new FileInfo(_paths[i]).Length);
            _ends[i] = length;
        }
    }

    public override bool CanRead => !_disposed;

    public override bool CanSeek => !_disposed;

    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _ends.Length == 0 ? 0 : _ends[^1];
        }
    }

    public override long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _position;
        }

        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        var count = PrepareRead(buffer.Length);
        return count == 0 ? 0 : Advance(_current!.Read(buffer[..count]));
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = PrepareRead(buffer.Length);
        if (count == 0)
        {
            return 0;
        }

        return Advance(
            await _current!.ReadAsync(buffer[..count], cancellationToken).ConfigureAwait(false));
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (position < 0)
        {
            throw new IOException("Cannot seek before the beginning of the stream.");
        }

        return _position = position;
    }

    public override void Flush() => ObjectDisposedException.ThrowIf(_disposed, this);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _current?.Dispose();
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    private int PrepareRead(int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (count == 0 || _position >= Length)
        {
            return 0;
        }

        var index = 0;
        while (_position >= _ends[index])
        {
            index++;
        }

        var start = index == 0 ? 0 : _ends[index - 1];
        if (_index != index)
        {
            _current?.Dispose();
            _current = null;
            _index = -1;
            _current = new FileStream(
                _paths[index],
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                });
            if (_current.Length != _ends[index] - start)
            {
                _current.Dispose();
                _current = null;
                throw new IOException("Clip length changed during streaming.");
            }

            _index = index;
        }

        _current!.Position = _position - start;
        return (int)Math.Min(count, _ends[index] - _position);
    }

    private int Advance(int count)
    {
        if (count == 0)
        {
            throw new EndOfStreamException("Clip ended before its advertised length.");
        }

        _position += count;
        return count;
    }
}
