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
        if (IsPublic(context.Request))
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

    private static bool IsPublic(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
            return false;

        var path = request.Path.Value?.TrimEnd('/');
        if (string.IsNullOrEmpty(path))
            path = "/";

        return path.Equals("/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/health", StringComparison.OrdinalIgnoreCase);
    }
}
