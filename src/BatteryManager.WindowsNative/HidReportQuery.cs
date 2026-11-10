using BatteryManager.Core;

namespace BatteryManager.WindowsNative;

internal sealed record HidReportReply(byte[] Report, ClockInstant ReceivedAt);

internal static class HidReportQuery
{
    public static async Task<HidReportReply?> ReadAsync(IHidChannel channel, byte[] request, Func<byte[], bool> accepts,
        TimeSpan attemptTimeout, int maximumAttempts, CancellationToken cancellationToken, bool useInterruptOutput = false,
        TimeProvider? clock = null)
    {
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(attemptTimeout);
            try
            {
                if (useInterruptOutput)
                    await channel.WriteInterruptReportAsync(request, deadline.Token).ConfigureAwait(false);
                else
                    await channel.WriteOutputReportAsync(request, deadline.Token).ConfigureAwait(false);
                while (true)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    var report = await channel.ReadInputReportAsync(deadline.Token).ConfigureAwait(false);
                    var receivedAt = ClockInstant.Read(clock ?? TimeProvider.System);
                    deadline.Token.ThrowIfCancellationRequested();
                    if (report.Length == 0) throw new IOException("The HID input channel ended before an expected response arrived.");
                    if (accepts(report)) return new(report, receivedAt);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                // I/O has actually ended before another query is sent.
            }
        }
        return null;
    }
}
