using System.Buffers;
using System.IO;
using FFMpegCore.Pipes;

namespace Diploma.Core.Models;

public class BgraVideoFrame : IVideoFrame
{
    public int Width  { get; }
    public int Height { get; }
    public string Format => "bgra";

    private readonly byte[] _data;
    private readonly int    _length;
    private readonly bool   _pooled;

    public BgraVideoFrame(byte[] data, int width, int height, bool pooled = false)
    {
        _data   = data;
        Width   = width;
        Height  = height;
        _length = width * height * 4;
        _pooled = pooled;
    }
    
    public void Serialize(Stream pipe)
        => pipe.Write(_data, 0, _length);

    public Task SerializeAsync(Stream pipe, CancellationToken token)
        => pipe.WriteAsync(_data, 0, _length, token);

    public void Return()
    {
        if (_pooled)
            ArrayPool<byte>.Shared.Return(_data);
    }
}