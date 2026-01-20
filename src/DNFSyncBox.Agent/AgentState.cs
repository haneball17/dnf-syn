using System.Threading;

namespace DNFSyncBox.Agent
{
    /// <summary>
    /// 代理内部状态：按键状态、伪造开关与调用统计。
    /// </summary>
    public sealed class AgentState
    {
        private readonly object _lock = new object();
        private readonly bool[] _directionDown = new bool[4];
        private bool _spoofEnabled;
        private volatile bool _stopRequested;

        public bool StopRequested => _stopRequested;

        public void RequestStop()
        {
            _stopRequested = true;
        }

        public bool SpoofEnabled
        {
            get
            {
                lock (_lock)
                {
                    return _spoofEnabled;
                }
            }
        }

        public void SetSpoofEnabled(bool enabled)
        {
            lock (_lock)
            {
                _spoofEnabled = enabled;
            }
        }

        public void SetDirectionKey(DirectionKey key, bool isDown)
        {
            lock (_lock)
            {
                _directionDown[(int)key] = isDown;
            }
        }

        public bool IsDirectionKeyDown(DirectionKey key)
        {
            lock (_lock)
            {
                return _directionDown[(int)key];
            }
        }

        public void ClearAll()
        {
            lock (_lock)
            {
                for (var i = 0; i < _directionDown.Length; i++)
                {
                    _directionDown[i] = false;
                }
            }
        }

        public int GetAsyncKeyStateCalls => (int)Interlocked.Read(ref _getAsyncKeyStateCalls);
        public int DirectInputCreateCalls => (int)Interlocked.Read(ref _directInputCreateCalls);
        public int CreateDeviceCalls => (int)Interlocked.Read(ref _createDeviceCalls);
        public int GetDeviceStateCalls => (int)Interlocked.Read(ref _getDeviceStateCalls);
        public int GetDeviceDataCalls => (int)Interlocked.Read(ref _getDeviceDataCalls);

        private long _getAsyncKeyStateCalls;
        private long _directInputCreateCalls;
        private long _createDeviceCalls;
        private long _getDeviceStateCalls;
        private long _getDeviceDataCalls;

        public void IncrementGetAsyncKeyState()
        {
            Interlocked.Increment(ref _getAsyncKeyStateCalls);
        }

        public void IncrementDirectInputCreate()
        {
            Interlocked.Increment(ref _directInputCreateCalls);
        }

        public void IncrementCreateDevice()
        {
            Interlocked.Increment(ref _createDeviceCalls);
        }

        public void IncrementGetDeviceState()
        {
            Interlocked.Increment(ref _getDeviceStateCalls);
        }

        public void IncrementGetDeviceData()
        {
            Interlocked.Increment(ref _getDeviceDataCalls);
        }

        public AgentStats SnapshotStats()
        {
            return new AgentStats(
                GetAsyncKeyStateCalls,
                DirectInputCreateCalls,
                CreateDeviceCalls,
                GetDeviceStateCalls,
                GetDeviceDataCalls);
        }
    }
}
