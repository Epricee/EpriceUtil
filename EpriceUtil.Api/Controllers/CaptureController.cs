using EpriceUtil.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace EpriceUtil.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class CaptureController : ControllerBase
{
    private readonly CaptureService _capture;

    public CaptureController(CaptureService capture)
    {
        _capture = capture;
    }

    [HttpPost]
    [RequestSizeLimit(100_000_000)]
    public async Task<IActionResult> Capture([FromBody] CaptureRequest request, CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Url))
            return BadRequest();

        var bytes = await _capture.CaptureAsync(
            request.Url,
            request.Width,
            request.Height,
            request.AwaitSignal,
            request.Type,
            cancellationToken);

        if (bytes == null || bytes.Length == 0)
            return StatusCode(StatusCodes.Status502BadGateway);

        var png = string.Equals(request.Type, "png", StringComparison.OrdinalIgnoreCase);
        return File(bytes, png ? "image/png" : "image/jpeg");
    }
}

public sealed class CaptureRequest
{
    public string Url { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string AwaitSignal { get; set; }
    public string Type { get; set; }
}
