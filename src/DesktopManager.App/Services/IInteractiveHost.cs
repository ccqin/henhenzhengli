namespace DesktopManager.App.Services;

/// <summary>M2 真机修复 Bug 2：宿主窗口若以 WS_EX_NOACTIVATE 运行（如 IconLayerWindow），所有子 TextBox
/// 无法接收键盘输入（app 从不获取前台焦点）。需在弹出文本输入（FenceControl 标题编辑 / RenameDialog）前
/// 让宿主临时激活（去 NOACTIVATE + SetForegroundWindow），结束后恢复 NOACTIVATE + 回桌面层 Z-order。
/// 接口让 FenceControl / RenameDialog 不直接依赖 IconLayerWindow 具体类型（解耦）。</summary>
public interface IInteractiveHost
{
    /// <summary>进入需要键盘输入的状态：临时去 WS_EX_NOACTIVATE 并前台化。必须与 EndInput 成对调用。</summary>
    void BeginInput();

    /// <summary>结束输入状态：恢复 WS_EX_NOACTIVATE 并回桌面层 Z-order。</summary>
    void EndInput();
}
