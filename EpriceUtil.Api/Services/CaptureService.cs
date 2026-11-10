using Microsoft.Playwright;

namespace EpriceUtil.Api.Services;

public sealed class CaptureService
{
    private const int NavigationTimeoutMilliseconds = 30000;
    private const int SignalTimeoutMilliseconds = 30000;
    private const int MaxAttempts = 2;
    private const int RetryDelayMilliseconds = 2000;

    private readonly ILogger<CaptureService> _logger;
    private readonly JobGate _gate;

    public CaptureService(ILogger<CaptureService> logger, JobGate gate)
    {
        _logger = logger;
        _gate = gate;
    }

    public async Task<byte[]> CaptureAsync(
        string url,
        int width,
        int height,
        string awaitSignal,
        string type,
        CancellationToken cancellationToken)
    {
        using (await _gate.EnterAsync(cancellationToken))
        {
            return await CaptureCoreAsync(url, width, height, awaitSignal, type, cancellationToken);
        }
    }

    private async Task<byte[]> CaptureCoreAsync(
        string url,
        int width,
        int height,
        string awaitSignal,
        string type,
        CancellationToken cancellationToken)
    {
        try
        {
            await PlaywrightInstaller.EnsureInstalledAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FastShot] - Falha ao instalar o Chromium.");
            return null;
        }

        var shotType = string.Equals(type, "png", StringComparison.OrdinalIgnoreCase)
            ? ScreenshotType.Png
            : ScreenshotType.Jpeg;

        _logger.LogInformation(
            "[FastShot] - Iniciando captura. Url: {Url}, Width: {Width}, Height: {Height}, AwaitSignal: {AwaitSignal}, Type: {Type}",
            url,
            width,
            height,
            awaitSignal,
            shotType);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var screenshot = await CaptureAttemptAsync(
                url,
                width,
                height,
                awaitSignal,
                shotType,
                attempt);

            if (screenshot?.Length > 0)
            {
                _logger.LogInformation(
                    "[FastShot] - Captura concluída com sucesso. Attempt: {Attempt}, Url: {Url}, Bytes: {Bytes}",
                    attempt,
                    url,
                    screenshot.Length);

                return screenshot;
            }

            if (attempt < MaxAttempts)
            {
                _logger.LogWarning(
                    "[FastShot] - Tentativa {Attempt} falhou. Nova tentativa em {Delay}ms. Url: {Url}",
                    attempt,
                    RetryDelayMilliseconds,
                    url);

                await Task.Delay(RetryDelayMilliseconds, cancellationToken);
            }
        }

        _logger.LogError("[FastShot] - Todas as tentativas falharam. Url: {Url}", url);
        return null;
    }

    private async Task<byte[]> CaptureAttemptAsync(
        string url,
        int width,
        int height,
        string awaitSignal,
        ScreenshotType type,
        int attempt)
    {
        IPlaywright playwright = null;
        IBrowser browser = null;
        IBrowserContext context = null;
        IPage page = null;
        ICDPSession cdpSession = null;

        try
        {
            playwright = await Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions
                {
                    Headless = true,
                    Args = new[]
                    {
                        "--no-sandbox",
                        "--disable-setuid-sandbox",
                        "--disable-dev-shm-usage"
                    }
                });

            var customSize = width > 0 && height > 0;
            var cacheKey = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}";

            context = await browser.NewContextAsync(
                new BrowserNewContextOptions
                {
                    ViewportSize = customSize
                        ? new ViewportSize
                        {
                            Width = width,
                            Height = height
                        }
                        : null,
                    ServiceWorkers = ServiceWorkerPolicy.Block,
                    ExtraHTTPHeaders = new Dictionary<string, string>
                    {
                        ["Cache-Control"] = "no-cache, no-store, max-age=0, must-revalidate",
                        ["Pragma"] = "no-cache"
                    }
                });

            page = await context.NewPageAsync();
            ConfigurePageLogs(page, attempt);

            try
            {
                cdpSession = await context.NewCDPSessionAsync(page);
                await cdpSession.SendAsync("Network.enable");
                await cdpSession.SendAsync(
                    "Network.setCacheDisabled",
                    new Dictionary<string, object>
                    {
                        ["cacheDisabled"] = true
                    });
                await cdpSession.SendAsync("Network.clearBrowserCache");

                _logger.LogInformation(
                    "[FastShot] - Cache interno do Chromium desabilitado. Attempt: {Attempt}",
                    attempt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[FastShot] - Não foi possível desabilitar o cache via CDP. Attempt: {Attempt}",
                    attempt);
            }

            await page.RouteAsync(
                "**/assets/**",
                async route =>
                {
                    try
                    {
                        var requestUrl = route.Request.Url;
                        if (!requestUrl.Contains("_fastshot=", StringComparison.OrdinalIgnoreCase))
                        {
                            var separator = requestUrl.Contains('?') ? "&" : "?";
                            requestUrl = $"{requestUrl}{separator}_fastshot={cacheKey}";
                        }

                        var headers = new Dictionary<string, string>(
                            route.Request.Headers,
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["cache-control"] = "no-cache, no-store, max-age=0, must-revalidate",
                            ["pragma"] = "no-cache"
                        };

                        await route.ContinueAsync(
                            new RouteContinueOptions
                            {
                                Url = requestUrl,
                                Headers = headers
                            });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "[FastShot] - Erro ao aplicar cache-busting no asset. Url: {Url}, Attempt: {Attempt}",
                            route.Request.Url,
                            attempt);

                        await route.ContinueAsync();
                    }
                });

            var navigationResponse = await page.GotoAsync(
                url,
                new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = NavigationTimeoutMilliseconds
                });

            _logger.LogInformation(
                "[FastShot] - Navegação concluída. RequestedUrl: {RequestedUrl}, FinalUrl: {FinalUrl}, Status: {Status}, BrowserVersion: {BrowserVersion}, CacheKey: {CacheKey}, Attempt: {Attempt}",
                url,
                page.Url,
                navigationResponse?.Status,
                browser.Version,
                cacheKey,
                attempt);

            if (!string.IsNullOrWhiteSpace(awaitSignal))
            {
                await WaitForSignalAsync(
                    page,
                    awaitSignal,
                    SignalTimeoutMilliseconds,
                    attempt);
            }

            return await page.ScreenshotAsync(
                new PageScreenshotOptions
                {
                    FullPage = !customSize,
                    Type = type
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[FastShot] - Erro na captura. Url: {Url}, CurrentPageUrl: {CurrentPageUrl}, Attempt: {Attempt}",
                url,
                page?.Url,
                attempt);

            return null;
        }
        finally
        {
            if (cdpSession != null)
            {
                try
                {
                    await cdpSession.DetachAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[FastShot] - Erro ao desconectar CDP. Attempt: {Attempt}", attempt);
                }
            }

            if (context != null)
            {
                try
                {
                    await context.CloseAsync();
                    _logger.LogInformation("[FastShot] - Contexto fechado com sucesso. Attempt: {Attempt}", attempt);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[FastShot] - Erro ao fechar contexto. Attempt: {Attempt}", attempt);
                }
            }

            if (browser != null)
            {
                try
                {
                    await browser.CloseAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[FastShot] - Erro ao fechar o Chromium. Attempt: {Attempt}", attempt);
                }
            }

            playwright?.Dispose();
        }
    }

    private async Task WaitForSignalAsync(
        IPage page,
        string signal,
        int timeoutMilliseconds,
        int attempt)
    {
        try
        {
            var initialValue = await page.EvaluateAsync<string>(
                "key => localStorage.getItem(key)",
                signal);

            _logger.LogInformation(
                "[FastShot] - Aguardando signal. Signal: {Signal}, InitialValue: {InitialValue}, Url: {Url}, Timeout: {Timeout}ms, Attempt: {Attempt}",
                signal,
                initialValue,
                page.Url,
                timeoutMilliseconds,
                attempt);

            await page.WaitForFunctionAsync(
                "key => localStorage.getItem(key) === 'true'",
                signal,
                new PageWaitForFunctionOptions
                {
                    Timeout = timeoutMilliseconds,
                    PollingInterval = 250
                });

            var finalValue = await page.EvaluateAsync<string>(
                "key => localStorage.getItem(key)",
                signal);

            _logger.LogInformation(
                "[FastShot] - Signal encontrado. Signal: {Signal}, FinalValue: {FinalValue}, Url: {Url}, Attempt: {Attempt}",
                signal,
                finalValue,
                page.Url,
                attempt);
        }
        catch (Exception ex)
        {
            var diagnostics = await GetSignalDiagnosticsAsync(page, signal);

            _logger.LogError(
                ex,
                "[FastShot] - Erro aguardando signal. Signal: {Signal}, CurrentValue: {CurrentValue}, Url: {Url}, Title: {Title}, ReadyState: {ReadyState}, LocalStorage: {@LocalStorage}, Attempt: {Attempt}",
                signal,
                diagnostics.CurrentValue,
                page.Url,
                diagnostics.Title,
                diagnostics.ReadyState,
                diagnostics.LocalStorage,
                attempt);

            throw;
        }
    }

    private void ConfigurePageLogs(IPage page, int attempt)
    {
        page.Console += (_, message) =>
        {
            var level = message.Type switch
            {
                "error" => LogLevel.Error,
                "warning" => LogLevel.Warning,
                _ => LogLevel.Information
            };

            _logger.Log(
                level,
                "[FastShot][BrowserConsole] Type: {Type}, Text: {Text}, Attempt: {Attempt}",
                message.Type,
                message.Text,
                attempt);
        };

        page.PageError += (_, error) =>
        {
            _logger.LogError(
                "[FastShot][PageError] Url: {Url}, Error: {Error}, Attempt: {Attempt}",
                page.Url,
                error,
                attempt);
        };

        page.RequestFailed += (_, request) =>
        {
            _logger.LogWarning(
                "[FastShot][RequestFailed] Method: {Method}, Url: {Url}, ResourceType: {ResourceType}, Failure: {Failure}, Attempt: {Attempt}",
                request.Method,
                request.Url,
                request.ResourceType,
                request.Failure,
                attempt);
        };

        page.Response += async (_, response) =>
        {
            try
            {
                if (!response.Url.Contains("/assets/", StringComparison.OrdinalIgnoreCase))
                {
                    if (response.Status >= 400)
                    {
                        _logger.LogWarning(
                            "[FastShot][HttpResponse] Status: {Status}, Url: {Url}, Attempt: {Attempt}",
                            response.Status,
                            response.Url,
                            attempt);
                    }

                    return;
                }

                response.Headers.TryGetValue("content-type", out var contentType);
                response.Headers.TryGetValue("cf-cache-status", out var cloudflareCacheStatus);
                response.Headers.TryGetValue("cf-ray", out var cloudflareRay);

                _logger.LogInformation(
                    "[FastShot][AssetResponse] Status: {Status}, Url: {Url}, ContentType: {ContentType}, CfCacheStatus: {CfCacheStatus}, CfRay: {CfRay}, FromServiceWorker: {FromServiceWorker}, Attempt: {Attempt}",
                    response.Status,
                    response.Url,
                    contentType,
                    cloudflareCacheStatus,
                    cloudflareRay,
                    response.FromServiceWorker,
                    attempt);

                var isJavaScript = response.Url.Contains(".js", StringComparison.OrdinalIgnoreCase);
                var validContentType =
                    !string.IsNullOrWhiteSpace(contentType) &&
                    (contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
                     || contentType.Contains("ecmascript", StringComparison.OrdinalIgnoreCase));

                if (!isJavaScript || validContentType)
                    return;

                var bodyPreview = await GetResponseBodyPreviewAsync(response);
                _logger.LogError(
                    "[FastShot][InvalidJavaScript] Asset JS retornou conteúdo inválido. Status: {Status}, Url: {Url}, ContentType: {ContentType}, CfCacheStatus: {CfCacheStatus}, CfRay: {CfRay}, BodyPreview: {BodyPreview}, Attempt: {Attempt}",
                    response.Status,
                    response.Url,
                    contentType,
                    cloudflareCacheStatus,
                    cloudflareRay,
                    bodyPreview,
                    attempt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[FastShot] - Erro ao analisar resposta HTTP. Url: {Url}, Attempt: {Attempt}",
                    response.Url,
                    attempt);
            }
        };
    }

    private static async Task<string> GetResponseBodyPreviewAsync(IResponse response)
    {
        try
        {
            var body = await response.TextAsync();
            if (string.IsNullOrWhiteSpace(body))
                return "[CORPO VAZIO]";

            const int maximumLength = 1500;
            return body.Length <= maximumLength
                ? body
                : body[..maximumLength];
        }
        catch (Exception ex)
        {
            return $"[ERRO AO LER CORPO: {ex.Message}]";
        }
    }

    private async Task<SignalDiagnostics> GetSignalDiagnosticsAsync(IPage page, string signal)
    {
        var result = new SignalDiagnostics();

        try
        {
            result.CurrentValue = await page.EvaluateAsync<string>(
                "key => localStorage.getItem(key)",
                signal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FastShot] - Não foi possível consultar o signal.");
        }

        try
        {
            result.Title = await page.TitleAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FastShot] - Não foi possível consultar o título.");
        }

        try
        {
            result.ReadyState = await page.EvaluateAsync<string>("() => document.readyState");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FastShot] - Não foi possível consultar readyState.");
        }

        try
        {
            result.LocalStorage = await page.EvaluateAsync<Dictionary<string, string>>(
                @"() => {
                    const values = {};
                    for (let index = 0; index < localStorage.length; index++) {
                        const key = localStorage.key(index);
                        values[key] = localStorage.getItem(key);
                    }
                    return values;
                }");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FastShot] - Não foi possível consultar localStorage.");
        }

        return result;
    }

    private sealed class SignalDiagnostics
    {
        public string CurrentValue { get; set; }
        public string Title { get; set; }
        public string ReadyState { get; set; }
        public Dictionary<string, string> LocalStorage { get; set; }
    }
}
