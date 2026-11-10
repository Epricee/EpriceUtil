using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace EpriceUtil.Api.Controllers;

[ApiController]
public class VersionController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public VersionController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [HttpGet("/")]
    public async Task<IActionResult> Get([FromQuery] string version)
    {
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        if (Request.Headers.ContainsKey("X-Forwarded-For"))
            clientIp = Request.Headers["X-Forwarded-For"].FirstOrDefault();

        var payload = new
        {
            Version = _configuration["UtilVersion"],
            ClientIp = clientIp,
            ServerIps = await GetPublicIpAsync()
        };

        return new JsonResult(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = null
        });
    }

    private static async Task<string> GetPublicIpAsync()
    {
        try
        {
            using var http = new HttpClient();
            var response = await http.GetStringAsync("https://api.ipify.org?format=json");
            using var document = JsonDocument.Parse(response);
            return document.RootElement.GetProperty("ip").GetString();
        }
        catch
        {
            return null;
        }
    }
}
