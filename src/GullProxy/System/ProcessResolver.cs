using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GullProxy.SystemIntegration;

/// <summary>
/// Identifies which local application opened a connection to the proxy. When a client connects,
/// its ephemeral local port appears in the OS TCP table paired with our proxy port and the
/// owning process id; we look that up and resolve the id to a process name. The table is snapshotted
/// briefly (it changes constantly) and process names are cached.
/// </summary>
public sealed class ProcessResolver
{
    public readonly record struct AppInfo(int Pid, string Name);

    private readonly ConcurrentDictionary<int, string> _names = new();
    private readonly object _gate = new();
    private Dictionary<(int local, int remote), int>? _snapshot;
    private DateTime _taken;
    private static readonly TimeSpan Ttl = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan MinRefresh = TimeSpan.FromMilliseconds(75);

    /// <summary>Resolves the app that owns the connection from <paramref name="localPort"/> → proxy <paramref name="proxyPort"/>.</summary>
    public AppInfo? Resolve(int localPort, int proxyPort)
    {
        var table = Snapshot(force: false);
        if (table is null || !table.TryGetValue((localPort, proxyPort), out int pid))
        {
            // Miss — likely a brand-new, short-lived connection not in the cached snapshot.
            // Force a fresh query (throttled) since the connection is definitely open right now.
            table = Snapshot(force: true);
            if (table is null || !table.TryGetValue((localPort, proxyPort), out pid)) return null;
        }

        string name = _names.GetOrAdd(pid, p =>
        {
            try { return Process.GetProcessById(p).ProcessName; }
            catch { return "pid " + p; }
        });
        return new AppInfo(pid, name);
    }

    private Dictionary<(int, int), int>? Snapshot(bool force)
    {
        lock (_gate)
        {
            var age = DateTime.UtcNow - _taken;
            if (_snapshot is not null && (force ? age < MinRefresh : age < Ttl)) return _snapshot;
            _snapshot = QueryTable();
            _taken = DateTime.UtcNow;
            return _snapshot;
        }
    }

    private static Dictionary<(int, int), int>? QueryTable()
    {
        try
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0)
                    return null;

                int count = Marshal.ReadInt32(buffer);
                var map = new Dictionary<(int, int), int>(count);
                int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                IntPtr rowPtr = buffer + 4;
                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                    map[(row.LocalPort, row.RemotePort)] = (int)row.owningPid;
                    rowPtr += rowSize;
                }
                return map;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { return null; }
    }

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tableClass, int reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public byte localPort1, localPort2, localPort3, localPort4;
        public uint remoteAddr;
        public byte remotePort1, remotePort2, remotePort3, remotePort4;
        public uint owningPid;

        public readonly int LocalPort => (localPort1 << 8) + localPort2;
        public readonly int RemotePort => (remotePort1 << 8) + remotePort2;
    }
}
