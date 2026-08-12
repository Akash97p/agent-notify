using System.Runtime.InteropServices;

namespace AgentNotify.Host;

/// <summary>
/// Turns Ctrl+C and the termination signals a service manager sends into a cancellation request.
/// </summary>
/// <remarks>
/// <para>
/// The registrations are held by this object for as long as the broker runs. This is not
/// bookkeeping: <see cref="PosixSignalRegistration"/> unhooks its handler when it is finalized, so
/// a registration created and discarded is collected at an arbitrary later moment and the daemon
/// silently stops reacting to <c>SIGTERM</c> — it neither shuts down nor dies, which makes it
/// impossible to stop without <c>SIGKILL</c>.
/// </para>
/// <para>
/// A second signal exits immediately. The first one asks for a graceful stop; if the caller signals
/// again they have stopped waiting, and an interrupted delivery is recovered from the outbox on the
/// next start.
/// </para>
/// </remarks>
internal sealed class ShutdownSignals : IDisposable
{
    private readonly List<PosixSignalRegistration> _registrations = [];
    private readonly CancellationTokenSource _shutdown;
    private int _count;

    private ShutdownSignals(CancellationTokenSource shutdown) => _shutdown = shutdown;

    internal static ShutdownSignals Register(CancellationTokenSource shutdown)
    {
        var signals = new ShutdownSignals(shutdown);

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            signals.Requested();
        };

        foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGQUIT })
        {
            try
            {
                signals._registrations.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    // Suppress the default terminate so the broker can close the API and the
                    // dispatcher first. Main bounds that work, so this cannot wedge the process.
                    context.Cancel = true;
                    signals.Requested();
                }));
            }
            catch (Exception)
            {
                // Windows has no SIGQUIT, and a constrained container may refuse a registration.
                // Ctrl+C handling above still applies.
            }
        }

        return signals;
    }

    private void Requested()
    {
        if (Interlocked.Increment(ref _count) > 1)
        {
            Console.Error.WriteLine("Second signal received; exiting immediately.");
            Environment.Exit(130);
            return;
        }

        try { _shutdown.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        foreach (var registration in _registrations)
            try { registration.Dispose(); } catch { }
        _registrations.Clear();
    }
}
