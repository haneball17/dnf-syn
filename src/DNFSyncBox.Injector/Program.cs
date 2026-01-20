using DNFSyncBox.Agent;
using EasyHook;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;

namespace DNFSyncBox.Injector
{
internal static class Program
{
    private const string TargetProcessName = "dnf";
    private const string TargetWindowKeyword = "DNF Taiwan";

    public static int Main(string[] args)
    {
        Console.WriteLine("DNFSyncBox 注入器（技术验证模式）");

        var targetPid = ResolveTargetPid(args);
        if (targetPid <= 0)
        {
            Console.WriteLine("未找到可用的 DNF 进程。");
            return 1;
        }

        var agentPath = ResolveAgentPath(args);
        if (string.IsNullOrEmpty(agentPath))
        {
            Console.WriteLine("未找到代理 DLL。请先构建 Agent，或使用 --agent 指定路径。");
            return 1;
        }

        var pipeName = $"DNFSyncBox.Agent.{targetPid}";

        try
        {
            Console.WriteLine($"准备注入 PID={targetPid} ...");
            RemoteHooking.Inject(
                targetPid,
                InjectionOptions.DoNotRequireStrongName,
                agentPath,
                agentPath,
                pipeName);
            Console.WriteLine("注入完成。");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"注入失败：{ex.Message}");
            return 1;
        }

        using var pipe = ConnectPipe(pipeName);
        if (pipe == null)
        {
            Console.WriteLine("连接代理管道失败。");
            return 1;
        }

        using var reader = new BinaryReader(pipe);
        using var writer = new BinaryWriter(pipe);

        Console.WriteLine("已连接代理。输入 help 查看指令。");
        CommandLoop(reader, writer);
        return 0;
    }

    /// <summary>
    /// 解析目标 PID：优先 --pid，其次按进程名/窗口标题匹配。
    /// </summary>
    private static int ResolveTargetPid(string[] args)
    {
        var pidArgIndex = Array.IndexOf(args, "--pid");
        if (pidArgIndex >= 0 && pidArgIndex + 1 < args.Length && int.TryParse(args[pidArgIndex + 1], out var pid))
        {
            return pid;
        }

        var candidates = Process.GetProcesses()
            .Where(process =>
            {
                try
                {
                    if (process.ProcessName.Equals(TargetProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    var title = process.MainWindowTitle;
                    return !string.IsNullOrEmpty(title)
                        && title.IndexOf(TargetWindowKeyword, StringComparison.OrdinalIgnoreCase) >= 0;
                }
                catch
                {
                    return false;
                }
            })
            .OrderBy(process => process.Id)
            .ToArray();

        if (candidates.Length == 0)
        {
            return -1;
        }

        if (candidates.Length == 1)
        {
            return candidates[0].Id;
        }

        Console.WriteLine("检测到多个 DNF 进程，请选择：");
        for (var i = 0; i < candidates.Length; i++)
        {
            Console.WriteLine($"{i + 1}. PID={candidates[i].Id} | 标题={candidates[i].MainWindowTitle}");
        }

        Console.Write("输入序号：");
        var input = Console.ReadLine();
        if (int.TryParse(input, out var index) && index >= 1 && index <= candidates.Length)
        {
            return candidates[index - 1].Id;
        }

        return -1;
    }

    /// <summary>
    /// 解析代理 DLL 路径：优先 --agent，其次尝试常见输出目录。
    /// </summary>
    private static string ResolveAgentPath(string[] args)
    {
        var agentArgIndex = Array.IndexOf(args, "--agent");
        if (agentArgIndex >= 0 && agentArgIndex + 1 < args.Length)
        {
            var inputPath = args[agentArgIndex + 1];
            if (File.Exists(inputPath))
            {
                return inputPath;
            }
        }

        var baseDir = AppContext.BaseDirectory;
        var local = Path.Combine(baseDir, "DNFSyncBox.Agent.dll");
        if (File.Exists(local))
        {
            return local;
        }

        var debugCandidate = Path.GetFullPath(Path.Combine(
            baseDir,
            "..",
            "..",
            "..",
            "DNFSyncBox.Agent",
            "bin",
            "Debug",
            "net48",
            "DNFSyncBox.Agent.dll"));
        if (File.Exists(debugCandidate))
        {
            return debugCandidate;
        }

        var releaseCandidate = Path.GetFullPath(Path.Combine(
            baseDir,
            "..",
            "..",
            "..",
            "DNFSyncBox.Agent",
            "bin",
            "Release",
            "net48",
            "DNFSyncBox.Agent.dll"));
        if (File.Exists(releaseCandidate))
        {
            return releaseCandidate;
        }

        return string.Empty;
    }

    /// <summary>
    /// 连接代理命名管道（带简单重试）。
    /// </summary>
    private static NamedPipeClientStream? ConnectPipe(string pipeName)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        try
        {
            // 代理初始化可能稍慢，这里做简单重试。
            for (var i = 0; i < 10; i++)
            {
                if (pipe.IsConnected)
                {
                    return pipe;
                }

                try
                {
                    pipe.Connect(500);
                }
                catch
                {
                    Thread.Sleep(200);
                }
            }
        }
        catch
        {
            pipe.Dispose();
            return null;
        }

        if (pipe.IsConnected)
        {
            return pipe;
        }

        pipe.Dispose();
        return null;
    }

    /// <summary>
    /// 控制台交互循环，发送指令并读取响应。
    /// </summary>
    private static void CommandLoop(BinaryReader reader, BinaryWriter writer)
    {
        while (true)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var command = parts[0].ToLowerInvariant();

            switch (command)
            {
                case "help":
                    PrintHelp();
                    break;
                case "stats":
                    RequestStats(reader, writer);
                    break;
                case "spoof":
                    if (parts.Length < 2)
                    {
                        Console.WriteLine("用法：spoof on/off");
                        break;
                    }
                    ToggleSpoof(reader, writer, parts[1].Equals("on", StringComparison.OrdinalIgnoreCase));
                    break;
                case "down":
                    SendKeyCommand(reader, writer, AgentCommand.KeyDown, parts);
                    break;
                case "up":
                    SendKeyCommand(reader, writer, AgentCommand.KeyUp, parts);
                    break;
                case "clear":
                    SendCommand(reader, writer, AgentCommand.Clear, DirectionKey.Up);
                    Console.WriteLine("已清空方向键状态。");
                    break;
                case "stop":
                    SendCommand(reader, writer, AgentCommand.Stop, DirectionKey.Up);
                    Console.WriteLine("已发送停止指令。");
                    return;
                case "exit":
                    return;
                default:
                    Console.WriteLine("未知指令，输入 help 查看可用指令。");
                    break;
            }
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("可用指令：");
        Console.WriteLine("  stats            - 输出调用统计");
        Console.WriteLine("  spoof on/off     - 开启或关闭伪造输入");
        Console.WriteLine("  down <key>       - 方向键按下（up/down/left/right）");
        Console.WriteLine("  up <key>         - 方向键抬起（up/down/left/right）");
        Console.WriteLine("  clear            - 清空方向键状态");
        Console.WriteLine("  stop             - 让代理退出");
        Console.WriteLine("  exit             - 退出注入器");
    }

    private static void SendKeyCommand(BinaryReader reader, BinaryWriter writer, AgentCommand command, string[] parts)
    {
        if (parts.Length < 2 || !TryParseDirection(parts[1], out var key))
        {
            Console.WriteLine("用法：down/up <up|down|left|right>");
            return;
        }

        SendCommand(reader, writer, command, key);
        Console.WriteLine($"已发送 {command} {key}");
    }

    private static void ToggleSpoof(BinaryReader reader, BinaryWriter writer, bool enable)
    {
        SendCommand(reader, writer, enable ? AgentCommand.EnableSpoof : AgentCommand.DisableSpoof, DirectionKey.Up);
        Console.WriteLine(enable ? "已开启伪造输入。" : "已关闭伪造输入。");
    }

    private static void RequestStats(BinaryReader reader, BinaryWriter writer)
    {
        SendCommand(reader, writer, AgentCommand.DumpStats, DirectionKey.Up, expectStats: true);
    }

    /// <summary>
    /// 发送指令并处理响应（stats 会读取统计结果）。 
    /// </summary>
    private static void SendCommand(BinaryReader reader, BinaryWriter writer, AgentCommand command, DirectionKey key, bool expectStats = false)
    {
        writer.Write((int)command);
        writer.Write((int)key);
        writer.Flush();

        var responseType = (AgentResponseType)reader.ReadInt32();
        if (expectStats && responseType == AgentResponseType.Stats)
        {
            var stats = new AgentStats(
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32());
            PrintStats(stats);
        }
    }

    private static void PrintStats(AgentStats stats)
    {
        Console.WriteLine("调用统计：");
        Console.WriteLine($"  GetAsyncKeyState: {stats.GetAsyncKeyStateCalls}");
        Console.WriteLine($"  DirectInput8Create: {stats.DirectInputCreateCalls}");
        Console.WriteLine($"  CreateDevice: {stats.CreateDeviceCalls}");
        Console.WriteLine($"  GetDeviceState: {stats.GetDeviceStateCalls}");
        Console.WriteLine($"  GetDeviceData: {stats.GetDeviceDataCalls}");
    }

    private static bool TryParseDirection(string value, out DirectionKey key)
    {
        switch (value.ToLowerInvariant())
        {
            case "up":
                key = DirectionKey.Up;
                return true;
            case "down":
                key = DirectionKey.Down;
                return true;
            case "left":
                key = DirectionKey.Left;
                return true;
            case "right":
                key = DirectionKey.Right;
                return true;
            default:
                key = DirectionKey.Up;
                return false;
        }
    }
}
}
