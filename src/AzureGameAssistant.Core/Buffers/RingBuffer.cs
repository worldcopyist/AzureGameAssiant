namespace AzureGameAssistant.Core.Buffers;

public sealed class RingBuffer<T>
{
    private readonly object _gate = new();
    private readonly T[] _items;
    private int _start;
    private int _count;
    private long _totalWritten;

    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public long TotalWritten
    {
        get
        {
            lock (_gate)
            {
                return _totalWritten;
            }
        }
    }

    public long OldestSequence
    {
        get
        {
            lock (_gate)
            {
                return _totalWritten - _count;
            }
        }
    }

    public void Write(T item)
    {
        lock (_gate)
        {
            if (_count < _items.Length)
            {
                _items[(_start + _count) % _items.Length] = item;
                _count++;
            }
            else
            {
                _items[_start] = item;
                _start = (_start + 1) % _items.Length;
            }

            _totalWritten++;
        }
    }

    public bool TryGet(long sequence, out T item)
    {
        lock (_gate)
        {
            if (sequence < _totalWritten - _count || sequence >= _totalWritten)
            {
                item = default!;
                return false;
            }

            int offset = (int)(sequence - (_totalWritten - _count));
            item = _items[(_start + offset) % _items.Length];
            return true;
        }
    }

    public int CopyTo(Span<T> destination)
    {
        lock (_gate)
        {
            int length = Math.Min(_count, destination.Length);
            for (int i = 0; i < length; i++)
            {
                destination[i] = _items[(_start + i) % _items.Length];
            }

            return length;
        }
    }

    public T[] ToArray()
    {
        lock (_gate)
        {
            var result = new T[_count];
            for (int i = 0; i < _count; i++)
            {
                result[i] = _items[(_start + i) % _items.Length];
            }

            return result;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_items);
            _start = 0;
            _count = 0;
            _totalWritten = 0;
        }
    }
}
