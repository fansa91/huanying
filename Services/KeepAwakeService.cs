using System;
using System.Runtime.InteropServices;

namespace Huanying.Services;

/// <summary>
/// 播放期间阻止系统休眠与息屏。
///
/// 走 <c>SetThreadExecutionState</c> 而不是 <c>DisplayRequest</c>：前者能同时申请
/// 「系统不休眠」（<c>ES_SYSTEM_REQUIRED</c>，机器不会睡着）和「显示器常亮」
/// （<c>ES_DISPLAY_REQUIRED</c>，不会黑屏），后者只挡显示器关闭。
/// 加 <c>ES_CONTINUOUS</c> 后设置一直有效，直到显式归还，不必每次喂一遍。
///
/// 注意：这个 API 是**按线程**生效的，必须在 UI 线程调用（本类只在页面逻辑里用）。
/// </summary>
internal sealed class KeepAwakeService : IDisposable
{
    [Flags]
    private enum ExecutionState : uint
    {
        /// <summary>让本次设置持续有效，直到下一次调用把它重置</summary>
        Continuous = 0x80000000,

        /// <summary>系统保持运行，不进入睡眠</summary>
        SystemRequired = 0x00000001,

        /// <summary>显示器保持点亮，不因空闲而息屏</summary>
        DisplayRequired = 0x00000002
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState flags);

    /// <summary>当前是否持有常亮请求</summary>
    public bool IsActive { get; private set; }

    /// <summary>申请常亮。幂等：已经在持有状态时直接返回。</summary>
    public void Acquire()
    {
        if (IsActive) return;

        var previous = SetThreadExecutionState(
            ExecutionState.Continuous | ExecutionState.SystemRequired | ExecutionState.DisplayRequired);

        // 返回 0 表示调用失败（极少数受限环境），此时保持 IsActive=false，避免误以为已生效
        IsActive = previous != 0;
    }

    /// <summary>归还常亮请求，恢复系统原有的休眠 / 息屏策略。幂等。</summary>
    public void Release()
    {
        if (!IsActive) return;

        SetThreadExecutionState(ExecutionState.Continuous);
        IsActive = false;
    }

    public void Dispose() => Release();
}
