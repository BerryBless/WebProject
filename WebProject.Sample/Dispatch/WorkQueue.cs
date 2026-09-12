using System.Threading.Channels;

namespace WebProject.Sample.Dispatch;

public sealed record WorkItem(long SessionId, byte[] Payload);

/// <summary>워커 큐. 생산자는 Enqueue, 소비자는 워커 태스크.</summary>
public sealed class WorkQueue : IAsyncDisposable
{
    private readonly Channel<WorkItem> _channel = Channel.CreateUnbounded<WorkItem>();
    private readonly List<Task> _workers = new();
    private readonly Func<WorkItem, Task> _handler;
    private readonly CancellationTokenSource _cts = new();

    public WorkQueue(Func<WorkItem, Task> handler, int workerCount)
    {
        _handler = handler;
        for (int i = 0; i < workerCount; i++)
        {
            int workerId = i;
            _workers.Add(Task.Run(() => WorkerLoop(workerId, _cts.Token)));
        }
    }

    public ValueTask EnqueueAsync(WorkItem item) => _channel.Writer.WriteAsync(item);

    private async Task WorkerLoop(int workerId, CancellationToken ct)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(ct))
        {
            var tagged = new { Worker = workerId, item.SessionId };
            await _handler(item);
            Log(tagged.ToString()!);
        }
    }

    private static void Log(string message) => Console.WriteLine(message);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await Task.WhenAll(_workers);
    }
}
