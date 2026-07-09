using System.Threading;
using System.Threading.Channels;

namespace GullProxy.Capture;

/// <summary>
/// Thread-safe sink for captured transactions. Keeps the most recent <see cref="Capacity"/>
/// exchanges in a ring buffer and publishes every completed transaction to a channel the UI
/// drains. The proxy never blocks on the UI: the channel drops the oldest item if the reader
/// falls behind.
/// </summary>
public sealed class TransactionStore
{
    public const int Capacity = 5000;
    public const int MaxBodyBytes = 1024 * 1024; // 1 MB kept per body for display

    private readonly object _gate = new();
    private readonly LinkedList<Transaction> _items = new();
    private readonly Channel<Transaction> _live;
    private long _nextId;
    private bool _paused;

    public TransactionStore()
    {
        _live = Channel.CreateBounded<Transaction>(new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public ChannelReader<Transaction> Live => _live.Reader;

    public bool Paused
    {
        get { lock (_gate) return _paused; }
        set { lock (_gate) _paused = value; }
    }

    public long NextId() => Interlocked.Increment(ref _nextId);

    /// <summary>Records a completed transaction and notifies the UI.</summary>
    public void Add(Transaction tx)
    {
        lock (_gate)
        {
            if (_paused) return;
            _items.AddLast(tx);
            while (_items.Count > Capacity)
                _items.RemoveFirst();
        }
        _live.Writer.TryWrite(tx);
    }

    public IReadOnlyList<Transaction> Snapshot()
    {
        lock (_gate)
            return _items.ToArray();
    }

    public void Clear()
    {
        lock (_gate)
            _items.Clear();
    }

    public void Complete() => _live.Writer.TryComplete();
}
