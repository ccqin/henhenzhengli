using System.Windows;

namespace DesktopManager.Plugin.Pet;

/// <summary>宠物大脑：行为状态机 + 简单物理（重力/地面/屏幕边界/攀爬）。
/// 坐标 = 虚拟桌面像素（与 WPF 多屏 Left/Top 一致）。30fps Step 驱动。</summary>
internal sealed class PetBrain
{
    public const int Size = 96;

    public enum State { Idle, Walk, Climb, Fall, Drag, Sleep }

    public State Current { get; private set; } = State.Idle;
    public double X { get; private set; } = 400;
    public double Y { get; private set; } = 400;
    public int Facing { get; private set; } = 1;               // 1 右 -1 左
    public string Ground { get; private set; } = "bottom";     // bottom/left/right

    private double _vx, _vy;                                    // 像素/秒
    private int _stateTicks;
    private readonly Random _rng = new();
    private Rect _world;                                        // 并集（虚拟桌面）
    private string _lastMood = "";
    private int _moodTicks;
    private double _floor => _world.Bottom - Size - 60;         // 底边离任务栏
    private double _ceil => _world.Top + 8;

    public void SetBounds(List<Rect> screens)
    {
        if (screens.Count == 0) return;
        var u = screens[0];
        foreach (var s in screens.Skip(1)) u = Rect.Union(u, s);
        _world = u;
        if (X < u.Left || X > u.Right - Size) X = u.Left + u.Width / 2;
        if (Y < u.Top || Y > u.Bottom - Size) Y = _floor;
    }

    public void Step(bool dragging)
    {
        _stateTicks++;
        if (_moodTicks > 0) _moodTicks--;

        switch (Current)
        {
            case State.Idle:
                if (_stateTicks > _rng.Next(90, 300)) // 3-10s 后自发行动
                {
                    if (_rng.Next(3) == 0) StartFall(vx: 0);
                    else StartWalk();
                }
                break;

            case State.Walk:
                X += _vx * Dt;
                if (X <= _world.Left + 4) { Facing = 1; MaybeIdleOrClimb("left"); }
                else if (X >= _world.Right - Size - 4) { Facing = -1; MaybeIdleOrClimb("right"); }
                else if (_stateTicks > _rng.Next(120, 420))
                {
                    if (_rng.Next(4) == 0) StartClimb(X < _world.Left + _world.Width / 2 ? "left" : "right");
                    else SetIdle();
                }
                break;

            case State.Climb:
                Y += _vy * Dt;
                if (Y <= _ceil) // 爬到顶 → 倒挂歇息或掉落
                {
                    if (_rng.Next(2) == 0) { SetIdle(); }
                    else StartFall(_rng.Next(-40, 40));
                }
                break;

            case State.Fall:
                _vy += 1200 * Dt; // 重力
                X += _vx * Dt;
                Y += _vy * Dt;
                if (Y >= _floor) { Y = _floor; Land(); }
                break;

            case State.Drag:
            case State.Sleep:
                break;
        }

        // 边界钳制
        X = Math.Clamp(X, _world.Left, Math.Max(_world.Left, _world.Right - Size));
        Y = Math.Clamp(Y, _world.Top, Math.Max(_world.Top, _world.Bottom - Size));
    }

    private static double Dt => 0.033;

    // ---- 姿态输出（渲染层消费）：脸 + 是否跳跃帧 + 旋转角 ----
    public (string Face, bool Hop, double Angle) Pose
    {
        get
        {
            if (_moodTicks > 0) return (_lastMood, false, 0);   // 交互表情优先
            return Current switch
            {
                State.Walk => ("🐈", (_stateTicks / 8) % 2 == 0, (_stateTicks / 8) % 2 == 0 ? -4 : 4), // 摇摆走
                State.Climb => ("🐈", false, 90 * Facing),       // 竖着爬
                State.Fall => ("🙀", false, _vy * 0.02),         // 惊恐旋转坠落
                State.Drag => ("😻", false, Math.Sin(_stateTicks * 0.3) * 12), // 拖拽摆动
                State.Sleep => ("😴", false, 0),
                _ => ("🐈", false, 0),
            };
        }
    }

    // ---- 外部事件 ----
    public void BeginDrag() { Current = State.Drag; _stateTicks = 0; }

    public void DragTo(double x, double y) { X = x; Y = y; }

    public void EndDrag() { StartFall(_rng.Next(-30, 30)); }

    public void Interact(string where)
    {
        _lastMood = where switch { "head" => "😺", "double" => "😻", _ => "😹" };
        _moodTicks = 45; // ~1.5s 表情
    }

    public void Sleep() { Current = State.Sleep; }

    public void Wake() { SetIdle(); }

    // ---- 内部转移 ----
    private void SetIdle() { Current = State.Idle; _stateTicks = 0; }

    private void StartWalk()
    {
        Current = State.Walk; _stateTicks = 0;
        Facing = _rng.Next(2) == 0 ? 1 : -1;
        _vx = Facing * _rng.Next(40, 110);
    }

    private void StartClimb(string side)
    {
        Current = State.Climb; _stateTicks = 0;
        Ground = side;
        X = side == "left" ? _world.Left + 2 : _world.Right - Size - 2;
        Facing = side == "left" ? 1 : -1;
        _vy = -_rng.Next(30, 70);
    }

    private void StartFall(double vx)
    {
        Current = State.Fall; _stateTicks = 0;
        _vx = vx; _vy = 0;
    }

    private void Land()
    {
        Ground = "bottom";
        if (_rng.Next(3) == 0) SetIdle(); else StartWalk();
    }

    private void MaybeIdleOrClimb(string side)
    {
        if (_rng.Next(3) == 0) StartClimb(side);
        // 否则继续走（Facing 已调转）
    }
}
