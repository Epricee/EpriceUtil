namespace EpriceUtil.Api;

public sealed class UtilTokenMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _token;

    public UtilTokenMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _token = configuration["UtilToken"];
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method)
            && (context.Request.Path.Equals("/", StringComparison.OrdinalIgnoreCase)
                || context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        if (string.IsNullOrWhiteSpace(_token)
            || context.Request.Headers["X-Util-Token"] != _token)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await _next(context);
    }
}
