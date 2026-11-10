using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace EpriceUtil.Api.Services;

public sealed class RemoveBackgroundService
{
    private const int MinimumModelBytes = 1024 * 1024;

    private readonly ILogger<RemoveBackgroundService> _logger;
    private readonly JobGate _gate;
    private readonly string _modelUrl;
    private readonly string _modelPath;
    private readonly SemaphoreSlim _downloadLock = new(1, 1);

    public RemoveBackgroundService(ILogger<RemoveBackgroundService> logger, IConfiguration configuration, JobGate gate)
    {
        _logger = logger;
        _gate = gate;
        _modelUrl = configuration["U2NetUrl"];
        if (string.IsNullOrWhiteSpace(_modelUrl))
            _modelUrl = "http://storages3.eprice.com.br/utils/u2net.onnx";

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = Path.GetTempPath();
        var folder = Path.Combine(root, "eprice-util");
        Directory.CreateDirectory(folder);
        _modelPath = Path.Combine(folder, "u2net.onnx");
    }

    public async Task<byte[]> RemoveAsync(Stream input, CancellationToken cancellationToken)
    {
        using (await _gate.EnterAsync(cancellationToken))
        {
            if (!await EnsureModelAsync(cancellationToken))
                return null;

            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, cancellationToken);
            return Remove(buffer.ToArray());
        }
    }

    private async Task<bool> EnsureModelAsync(CancellationToken cancellationToken)
    {
        if (ModelExists())
            return true;

        await _downloadLock.WaitAsync(cancellationToken);
        try
        {
            if (ModelExists())
                return true;

            if (File.Exists(_modelPath))
                File.Delete(_modelPath);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var response = await http.GetAsync(_modelUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Download do U2Net respondeu {StatusCode}", (int)response.StatusCode);
                return false;
            }

            var data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!IsOnnxPayload(data))
            {
                _logger.LogError("Download do U2Net inválido. Bytes: {Bytes}", data?.Length ?? 0);
                return false;
            }

            var temporary = _modelPath + ".download";
            try
            {
                await File.WriteAllBytesAsync(temporary, data, cancellationToken);
                File.Move(temporary, _modelPath, true);
                return true;
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao baixar o modelo U2Net");
            return false;
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    private byte[] Remove(byte[] content)
    {
        InferenceSession session = null;
        var sessionReady = false;
        try
        {
            using var options = new Microsoft.ML.OnnxRuntime.SessionOptions
            {
                EnableCpuMemArena = false,
                EnableMemoryPattern = false
            };
            session = new InferenceSession(_modelPath, options);
            sessionReady = true;

            using var input = new MemoryStream(content);
            using var image = Image.Load<Rgba32>(input);

            var originalWidth = image.Width;
            var originalHeight = image.Height;
            const int target = 320;

            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(target, target),
                Mode = ResizeMode.Stretch
            }));

            var inputData = new float[target * target * 3];
            var i = 0;
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (var x = 0; x < row.Length; x++)
                    {
                        inputData[i++] = row[x].R / 255f;
                        inputData[i++] = row[x].G / 255f;
                        inputData[i++] = row[x].B / 255f;
                    }
                }
            });

            var tensor = new DenseTensor<float>(new[] { 1, 3, target, target });
            for (var c = 0; c < 3; c++)
                for (var y = 0; y < target; y++)
                    for (var x = 0; x < target; x++)
                        tensor[0, c, y, x] = inputData[(y * target + x) * 3 + c];

            var inputName = session.InputMetadata.Keys.First();
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, tensor) };

            using var results = session.Run(inputs);
            var outputName = session.OutputMetadata.Keys.First();
            var mask = results.First(x => x.Name == outputName).AsEnumerable<float>().ToArray();

            var max = mask.Max();
            var min = mask.Min();
            for (var j = 0; j < mask.Length; j++)
                mask[j] = (mask[j] - min) / (max - min);

            using var resultImage = new Image<Rgba32>(target, target);
            i = 0;

            image.DangerousTryGetSinglePixelMemory(out var srcMemory);
            resultImage.DangerousTryGetSinglePixelMemory(out var dstMemory);

            for (var y = 0; y < target; y++)
            {
                var srcRow = srcMemory.Slice(y * target, target).Span;
                var dstRow = dstMemory.Slice(y * target, target).Span;

                for (var x = 0; x < target; x++)
                {
                    var alpha = (byte)(mask[i++] * 255);
                    dstRow[x] = new Rgba32(srcRow[x].R, srcRow[x].G, srcRow[x].B, alpha);
                }
            }

            resultImage.Mutate(x => x.Resize(originalWidth, originalHeight, KnownResamplers.Bicubic));

            using var png = new MemoryStream();
            resultImage.SaveAsPng(png);
            return png.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao remover o fundo");
            if (!sessionReady)
                TryDeleteModel();
            return null;
        }
        finally
        {
            session?.Dispose();
        }
    }

    private bool ModelExists()
    {
        return File.Exists(_modelPath) && new FileInfo(_modelPath).Length >= MinimumModelBytes;
    }

    private static bool IsOnnxPayload(byte[] data)
    {
        if (data == null || data.Length < MinimumModelBytes)
            return false;

        return data[0] != (byte)'<' && data[0] != (byte)'{';
    }

    private void TryDeleteModel()
    {
        try
        {
            if (File.Exists(_modelPath))
                File.Delete(_modelPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Não foi possível apagar o modelo U2Net inválido");
        }
    }
}
