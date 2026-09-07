namespace DesktopManager.Plugin.Pet;

/// <summary>渲染器抽象：姿态 → 视觉表现。Emoji 为内置兜底；Sprite（帧序列）/Live2D（WebView2）
/// 为后续实现——按素材目录内容自动选择（pet.json 或 frames/ 存在与否）。</summary>
internal interface IPetRenderer
{
    string IdleFace { get; }
    string SleepFace { get; }
    // 后续：Frame(state, tick) → 图像源；Live2D 版走 WebView2 消息协议
}

internal sealed class EmojiPetRenderer : IPetRenderer
{
    public string IdleFace => "🐈";
    public string SleepFace => "😴";
}
