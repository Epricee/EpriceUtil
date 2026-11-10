namespace EpriceUtil.Api;

public sealed class JobGate
{
    private readonly SemaphoreSlim _gate;

    public JobGate(IConfiguration configuration, ILogger<JobGate> logger)
    {
        var limit = 1;
        if (int.TryParse(configuration["JobGateLimit"], out var configured) && configured > 0)
            limit = configured;

        _gate = new SemaphoreSlim(limit, limit);
        logger.LogInformation("EpriceUtil aceita {Limit} trabalho(s) ao mesmo tempo.", limit);
    }

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        return new Releaser(_gate);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _gate;

        public Releaser(SemaphoreSlim gate) => _gate = gate;

        public void Dispose() => _gate.Release();
    }
}
