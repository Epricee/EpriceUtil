namespace EpriceUtil.Api;

public static class PlaywrightInstaller
{
    private static bool _installed;
    private static readonly object Lock = new();

    public static Task EnsureInstalledAsync()
    {
        if (_installed)
            return Task.CompletedTask;

        lock (Lock)
        {
            if (_installed)
                return Task.CompletedTask;

            var exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "--with-deps" });
            if (exitCode != 0)
                throw new Exception($"Playwright install failed ({exitCode})");

            _installed = true;
            return Task.CompletedTask;
        }
    }
}
