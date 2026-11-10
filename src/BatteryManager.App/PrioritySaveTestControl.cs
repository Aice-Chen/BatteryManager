namespace BatteryManager.App;

// Constructed only when both a fictional scenario and a test pipe were requested.
// It pauses/fails the save boundary; the panel's queue and the app's actual save path stay in use.
internal sealed class PrioritySaveTestControl
{
    private TaskCompletionSource? _nextGate;
    private TaskCompletionSource? _activeGate;
    private bool _failNext;
    private int _delayMilliseconds;

    internal void Configure(string command)
    {
        if (command == "release")
        {
            (_activeGate ?? _nextGate)?.TrySetResult();
            return;
        }
        if (_activeGate is not null || _nextGate is not null)
            throw new InvalidOperationException("Release the held save before changing the test control.");
        if (command is "hold" or "hold-fail")
        {
            _nextGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _failNext = command == "hold-fail";
            return;
        }
        if (command == "reset") { _delayMilliseconds = 0; _failNext = false; return; }
        if (command.StartsWith("delay:", StringComparison.Ordinal) &&
            int.TryParse(command.AsSpan(6), out var delay) && delay is >= 0 and <= 5000)
        {
            _delayMilliseconds = delay;
            return;
        }
        throw new ArgumentException("Use hold, hold-fail, release, reset or delay:0..5000.");
    }

    internal async Task<string?> BeforeSaveAsync()
    {
        var gate = _nextGate;
        var fail = _failNext;
        var delay = _delayMilliseconds;
        _nextGate = null;
        _failNext = false;
        _activeGate = gate;
        try
        {
            if (gate is not null) await gate.Task;
            if (delay > 0) await Task.Delay(delay);
            return fail ? "模拟保存失败 / Simulated priority save failure" : null;
        }
        finally { _activeGate = null; }
    }

    // Test shutdown can no longer receive a pipe release. Release its artificial barrier only;
    // this does not cancel the accepted write or bypass the ordinary save/drain path.
    internal void ReleaseForExit() => (_activeGate ?? _nextGate)?.TrySetResult();
}
