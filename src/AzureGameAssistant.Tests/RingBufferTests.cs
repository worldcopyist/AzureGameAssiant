using AzureGameAssistant.Core.Buffers;

namespace AzureGameAssistant.Tests;

public sealed class RingBufferTests
{
    [Fact]
    public void Write_WrapsAndKeepsNewestItems()
    {
        var buffer = new RingBuffer<int>(3);
        buffer.Write(1);
        buffer.Write(2);
        buffer.Write(3);
        buffer.Write(4);

        Assert.Equal(3, buffer.Count);
        Assert.Equal(4, buffer.TotalWritten);
        Assert.Equal(1, buffer.OldestSequence);
        Assert.False(buffer.TryGet(0, out _));
        Assert.True(buffer.TryGet(1, out int first));
        Assert.Equal(2, first);
        Assert.True(buffer.TryGet(3, out int last));
        Assert.Equal(4, last);
        Assert.False(buffer.TryGet(4, out _));
    }

    [Fact]
    public void CopyTo_ReturnsOldestFirst()
    {
        var buffer = new RingBuffer<int>(4);
        foreach (int value in new[] { 10, 20, 30, 40, 50 })
        {
            buffer.Write(value);
        }

        Span<int> destination = stackalloc int[2];
        int copied = buffer.CopyTo(destination);

        Assert.Equal(2, copied);
        Assert.Equal(20, destination[0]);
        Assert.Equal(30, destination[1]);
    }

    [Fact]
    public void Clear_ResetsSequence()
    {
        var buffer = new RingBuffer<string>(2);
        buffer.Write("a");
        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.Equal(0, buffer.TotalWritten);
        Assert.Equal(0, buffer.OldestSequence);
    }

    [Fact]
    public async Task ConcurrentReaders_SeeConsistentValues()
    {
        var buffer = new RingBuffer<long>(64);
        const int total = 200_000;
        using var barrier = new Barrier(4);

        var writer = Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (long i = 0; i < total; i++)
            {
                buffer.Write(i);
                if (i % 1_000 == 0)
                {
                    Thread.Yield();
                }
            }
        });

        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            long observed = 0;
            while (!writer.IsCompleted)
            {
                long sequence = buffer.TotalWritten - 1;
                if (sequence >= 0 && buffer.TryGet(sequence, out long value))
                {
                    Assert.InRange(value, 0, total - 1);
                    observed++;
                }
            }

            return observed;
        })).ToArray();

        await Task.WhenAll(readers.Append(writer));

        Assert.All(readers, reader => Assert.True(reader.Result > 0));
        Assert.Equal(total, buffer.TotalWritten);
    }
}
