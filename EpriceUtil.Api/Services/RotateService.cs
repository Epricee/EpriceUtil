using ImageMagick;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Processing;

namespace EpriceUtil.Api.Services;

public sealed class RotateService
{
    private const ulong GifPixelCacheMemoryBytes = 256UL * 1024 * 1024;
    private const ulong GifPixelCacheDiskBytes = 8UL * 1024 * 1024 * 1024;

    private static readonly SemaphoreSlim GifLock = new(1, 1);
    private static bool _gifCacheReady;

    private readonly ILogger<RotateService> _logger;
    private readonly JobGate _gate;

    public RotateService(ILogger<RotateService> logger, JobGate gate)
    {
        _logger = logger;
        _gate = gate;
    }

    public async Task<byte[]> RotateAsync(Stream input, string fileName, CancellationToken cancellationToken)
    {
        using (await _gate.EnterAsync(cancellationToken))
        {
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, cancellationToken);
            return Rotate(buffer.ToArray(), fileName);
        }
    }

    private byte[] Rotate(byte[] content, string fileName)
    {
        try
        {
            var extension = Path.GetExtension(fileName)?.ToLowerInvariant();
            if (extension == ".gif")
                return ProcessGif(content);

            return ProcessStaticImage(content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao girar {FileName}", fileName);
            return null;
        }
    }

    private byte[] ProcessStaticImage(byte[] content)
    {
        using var input = new MemoryStream(content);
        using var image = Image.Load(input, out IImageFormat format);
        image.Mutate(x => x.Rotate(RotateMode.Rotate90));

        using var rotated = new MemoryStream();
        image.Save(rotated, format);
        return rotated.ToArray();
    }

    private byte[] ProcessGif(byte[] content)
    {
        GifLock.Wait();
        try
        {
            ConfigureGifCache();
            using var input = new MemoryStream(content);
            using var gif = new MagickImageCollection(input);
            foreach (var frame in gif)
                frame.Rotate(90);

            using var rotated = new MemoryStream();
            gif.Write(rotated, MagickFormat.Gif);
            return rotated.ToArray();
        }
        finally
        {
            GifLock.Release();
        }
    }

    private static void ConfigureGifCache()
    {
        if (_gifCacheReady)
            return;

        var temp = Path.Combine(Path.GetTempPath(), "magick-view");
        Directory.CreateDirectory(temp);
        MagickNET.SetTempDirectory(temp);
        ResourceLimits.Memory = GifPixelCacheMemoryBytes;
        ResourceLimits.MaxMemoryRequest = 32UL * 1024 * 1024;
        ResourceLimits.Disk = GifPixelCacheDiskBytes;
        _gifCacheReady = true;
    }
}
