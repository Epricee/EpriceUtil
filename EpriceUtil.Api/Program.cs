using EpriceUtil.Api;
using EpriceUtil.Api.Services;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<JobGate>();
builder.Services.AddSingleton<CaptureService>();
builder.Services.AddSingleton<RotateService>();
builder.Services.AddSingleton<RemoveBackgroundService>();
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 100 * 1024 * 1024;
});
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 100 * 1024 * 1024;
});

var app = builder.Build();

app.UseMiddleware<UtilTokenMiddleware>();
app.MapControllers();
app.MapHealthChecks("/health");
app.Run();
