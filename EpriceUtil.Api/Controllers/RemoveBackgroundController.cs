using EpriceUtil.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace EpriceUtil.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class RemoveBackgroundController : ControllerBase
{
    private readonly RemoveBackgroundService _removeBackground;

    public RemoveBackgroundController(RemoveBackgroundService removeBackground)
    {
        _removeBackground = removeBackground;
    }

    [HttpPost]
    [RequestSizeLimit(100_000_000)]
    public async Task<IActionResult> Remove(IFormFile file, CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
            return BadRequest();

        await using var input = file.OpenReadStream();
        var bytes = await _removeBackground.RemoveAsync(input, cancellationToken);
        if (bytes == null || bytes.Length == 0)
            return StatusCode(StatusCodes.Status502BadGateway);

        return File(bytes, "image/png");
    }
}
