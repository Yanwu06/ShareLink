using System.Windows.Threading;

namespace ShareLink;

internal static class BluetoothWorker
{
    public static Task<T> RunAsync<T>(Func<Task<T>> operation) => Task.Run(operation);
    public static Task Run(Action operation) => Task.Run(operation);

    public static async Task<T> AwaitAsync<T>(Windows.Foundation.IAsyncOperation<T> operation, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try { return await operation.AsTask(cancellation.Token); }
        catch (OperationCanceledException ex) when (cancellation.IsCancellationRequested)
        {
            throw new TimeoutException("蓝牙操作超时，已请求取消该操作。", ex);
        }
    }

    // A native call can block before returning its asynchronous operation.
    // Verify that both this and slow teardown leave the WPF dispatcher running.
    public static async Task<object> DiagnosticChecksAsync()
    {
        int ticks = 0;
        bool onUi = true;
        var dispatcher = Dispatcher.CurrentDispatcher;
        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        heartbeat.Tick += (_, _) => ticks++;
        heartbeat.Start();
        try
        {
            await RunAsync(async () => { onUi = dispatcher.CheckAccess(); Thread.Sleep(240); await Task.Delay(120); return true; });
            if (onUi || ticks < 3) throw new InvalidOperationException("慢蓝牙连接阻塞了界面线程。");
            int before = ticks;
            await Run(() => Thread.Sleep(240));
            if (ticks - before < 3) throw new InvalidOperationException("慢蓝牙清理阻塞了界面线程。");
            return new { passed = true, checks = 2, simulatedDriverDelay = true, radioAccessed = false, uiHeartbeats = ticks };
        }
        finally { heartbeat.Stop(); }
    }
}
