using System.Security.Cryptography;
using System.Text;

namespace TokenFloat.Services;

public sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly ManualResetEvent _stopEvent = new(false);
    private readonly bool _ownsMutex;
    private Task? _listenerTask;
    private int _disposed;

    public SingleInstanceService()
    {
        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        var prefix = $"Local\\TokenFloat.{suffix}";

        _mutex = new Mutex(true, $"{prefix}.Mutex", out _ownsMutex);
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $"{prefix}.Activate");
    }

    public bool IsPrimary => _ownsMutex;

    public void SignalPrimary() => _activationEvent.Set();

    /// <summary>
    /// 后台等待第二实例的激活信号，并把窗口唤醒操作交回调用方。
    /// </summary>
    public void StartListening(Action activateWindow)
    {
        if (!_ownsMutex || _listenerTask is not null)
        {
            return;
        }

        _listenerTask = Task.Run(() =>
        {
            var handles = new WaitHandle[] { _activationEvent, _stopEvent };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                activateWindow();
            }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopEvent.Set();
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
        }

        if (_listenerTask is { IsCompleted: false } listener)
        {
            _ = listener.ContinueWith(
                _ => DisposeHandles(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return;
        }

        DisposeHandles();
    }

    private void DisposeHandles()
    {
        _activationEvent.Dispose();
        _stopEvent.Dispose();
        _mutex.Dispose();
    }
}
