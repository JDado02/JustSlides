using System.Runtime.InteropServices;
using Serilog;

namespace Regia.PptHost;

/// <summary>
/// IMessageFilter registrato sul thread STA: se PowerPoint è occupato (SERVERCALL_RETRYLATER / chiamata rifiutata)
/// la chiamata si ripete ogni 100 ms fino a <see cref="MaxRetryMs"/>, poi si rinuncia e lascia decidere al watchdog della regia.
/// </summary>
[ComVisible(true)]
internal sealed class MessageFilter : IOleMessageFilter
{
    /// <summary>Oltre questo tempo una chiamata rifiutata viene abbandonata (è sopra i timeout del watchdog: scatta prima lui).</summary>
    public const int MaxRetryMs = 10_000;

    private const int ServerCallIsHandled = 0;
    private const int ServerCallRetryLater = 2;
    private const int PendingMsgWaitDefProcess = 2;

    private static IOleMessageFilter? _old;
    private static MessageFilter? _instance;

    /// <summary>Da chiamare sul thread STA, prima di qualsiasi chiamata COM.</summary>
    public static void Register()
    {
        _instance = new MessageFilter();
        var hr = Native.CoRegisterMessageFilter(_instance, out _old);
        if (hr != 0)
            Log.Warning("CoRegisterMessageFilter fallita: 0x{Hr:X8}", hr);
        else
            Log.Debug("IMessageFilter registrato");
    }

    public static void Revoke()
    {
        if (_instance is null)
            return;

        Native.CoRegisterMessageFilter(_old, out _);
        _instance = null;
    }

    public int HandleInComingCall(int callType, IntPtr taskCaller, int tickCount, IntPtr interfaceInfo) => ServerCallIsHandled;

    public int RetryRejectedCall(IntPtr taskCallee, int tickCount, int rejectType)
    {
        if (rejectType == ServerCallRetryLater && tickCount < MaxRetryMs)
            return 100; // riprova tra 100 ms

        Log.Warning("Chiamata COM rifiutata da PowerPoint e abbandonata (tipo {Type}, {Ticks} ms)", rejectType, tickCount);
        return -1; // annulla
    }

    public int MessagePending(IntPtr taskCallee, int tickCount, int pendingType) => PendingMsgWaitDefProcess;
}
