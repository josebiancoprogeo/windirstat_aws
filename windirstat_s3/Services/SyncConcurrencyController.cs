using System;
using System.Threading;
using System.Threading.Tasks;

namespace windirstat_s3.Services;

public sealed class SyncConcurrencyController
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _permits;
    private readonly int _maxConcurrency;
    private int _inFlight;
    private int _targetConcurrency;

    public SyncConcurrencyController(int initialConcurrency, int maxConcurrency = 64)
    {
        _maxConcurrency = Math.Clamp(maxConcurrency, 1, 64);
        _targetConcurrency = Math.Clamp(initialConcurrency, 1, _maxConcurrency);
        _permits = new SemaphoreSlim(_targetConcurrency, _maxConcurrency);
    }

    public int TargetConcurrency
    {
        get
        {
            lock (_sync)
            {
                return _targetConcurrency;
            }
        }
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await _permits.WaitAsync(cancellationToken);
        lock (_sync)
        {
            _inFlight++;
        }
    }

    public void Release()
    {
        lock (_sync)
        {
            _inFlight = Math.Max(0, _inFlight - 1);
            var totalAllowed = _inFlight + _permits.CurrentCount;
            if (totalAllowed < _targetConcurrency)
            {
                _permits.Release();
            }
        }
    }

    public void UpdateTargetConcurrency(int desiredConcurrency)
    {
        lock (_sync)
        {
            _targetConcurrency = Math.Clamp(desiredConcurrency, 1, _maxConcurrency);
            var totalAllowed = _inFlight + _permits.CurrentCount;
            if (totalAllowed < _targetConcurrency)
            {
                _permits.Release(_targetConcurrency - totalAllowed);
            }
        }
    }
}
