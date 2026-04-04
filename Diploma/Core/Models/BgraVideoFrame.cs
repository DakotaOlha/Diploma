using System.IO;
using FFMpegCore.Pipes;

namespace Diploma.Core.Models;

public class BgraVideoFrame : IVideoFrame
{
    public int Width { get; }
    public int Height { get; }
    public string Format => "bgra";
    private readonly byte[] _data;

    public BgraVideoFrame(byte[] data, int width, int height)
    {
        _data = data;
        Width = width;
        Height = height;
    }

    public void Serialize(Stream pipe)
    {
        pipe.Write(_data, 0, _data.Length);
    }
    
    public Task SerializeAsync(Stream pipe, CancellationToken token)
    {
        return pipe.WriteAsync(_data, 0, _data.Length, token);
    }
}