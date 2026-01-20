namespace DNFSyncBox.Agent
{
    /// <summary>
    /// 代理与注入器之间的最小指令集。
    /// </summary>
    public enum AgentCommand : int
    {
        KeyDown = 1,
        KeyUp = 2,
        Clear = 3,
        EnableSpoof = 4,
        DisableSpoof = 5,
        DumpStats = 6,
        Stop = 9
    }

    /// <summary>
    /// 方向键枚举（保持简单、稳定）。
    /// </summary>
    public enum DirectionKey : int
    {
        Up = 0,
        Down = 1,
        Left = 2,
        Right = 3
    }

    /// <summary>
    /// 管道响应类型。
    /// </summary>
    public enum AgentResponseType : int
    {
        Ack = 1,
        Stats = 2
    }

    /// <summary>
    /// 统计快照（用于确认输入路径调用频率）。
    /// </summary>
    public readonly struct AgentStats
    {
        public AgentStats(int getAsyncKeyStateCalls, int directInputCreateCalls, int createDeviceCalls, int getDeviceStateCalls, int getDeviceDataCalls)
        {
            GetAsyncKeyStateCalls = getAsyncKeyStateCalls;
            DirectInputCreateCalls = directInputCreateCalls;
            CreateDeviceCalls = createDeviceCalls;
            GetDeviceStateCalls = getDeviceStateCalls;
            GetDeviceDataCalls = getDeviceDataCalls;
        }

        public int GetAsyncKeyStateCalls { get; }
        public int DirectInputCreateCalls { get; }
        public int CreateDeviceCalls { get; }
        public int GetDeviceStateCalls { get; }
        public int GetDeviceDataCalls { get; }
    }
}
