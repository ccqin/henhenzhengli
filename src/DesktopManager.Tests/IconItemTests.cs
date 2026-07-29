using System.ComponentModel;
using DesktopManager.Core.Models;

namespace DesktopManager.Tests;

/// <summary>P0-T1：IconItem record→class+INPC 的行为契约。
/// 守门对象：构造签名（DesktopDiffTests/DesktopSnapshot 依赖 2 参 + x/y 默认 0）。</summary>
public class IconItemTests
{
    private static List<string?> Subscribe(IconItem item)
    {
        var names = new List<string?>();
        item.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        return names;
    }

    // 守门：构造签名保持 —— 2 参形态，x/y 默认 0。
    [Fact]
    public void Ctor_TwoArgs_DefaultsXyZero()
    {
        var item = new IconItem("C:\\a.txt", "a.txt");
        Assert.Equal("C:\\a.txt", item.FilePath);
        Assert.Equal("a.txt", item.DisplayName);
        Assert.Equal(0, item.X);
        Assert.Equal(0, item.Y);
    }

    // 守门：4 参形态可显式给 x/y。
    [Fact]
    public void Ctor_FourArgs_SetsXy()
    {
        var item = new IconItem("C:\\a.txt", "a.txt", 12.5, 34.0);
        Assert.Equal(12.5, item.X);
        Assert.Equal(34.0, item.Y);
    }

    // 构造期不触发通知：构造完成即刻订阅，无后续变更 → 零事件。
    // （构造期 event 无订阅者；此测试 + 下文 4 setter 单名通知 共同约束：构造只赋 backing field，不经 Set<T>。）
    [Fact]
    public void Ctor_NoNotificationBeforeMutation()
    {
        var item = new IconItem("C:\\a.txt", "a.txt", 1, 2);
        var names = Subscribe(item);
        Assert.Empty(names);
    }

    // 4 属性各一条：设值触发且仅触发对应名的 PropertyChanged。
    [Theory]
    [InlineData("FilePath", "C:\\new.txt")]
    [InlineData("DisplayName", "new.txt")]
    [InlineData("X", 10.0)]
    [InlineData("Y", 20.0)]
    public void Set_RaisesPropertyChanged_ForPropertyOnly(string propName, object newValue)
    {
        var item = new IconItem("C:\\a.txt", "a.txt");
        var names = Subscribe(item);
        switch (propName)
        {
            case "FilePath":    item.FilePath = (string)newValue; break;
            case "DisplayName": item.DisplayName = (string)newValue; break;
            case "X":           item.X = (double)newValue; break;
            case "Y":           item.Y = (double)newValue; break;
        }
        Assert.Equal(new[] { propName }, names);
    }

    // 等值赋值不触发通知（Set<T> 的 EqualityComparer 短路）。
    [Fact]
    public void Set_SameValue_RaisesNoEvent()
    {
        var item = new IconItem("C:\\a.txt", "a.txt", 5, 5);
        var names = Subscribe(item);
        item.FilePath = "C:\\a.txt";
        item.DisplayName = "a.txt";
        item.X = 5;
        item.Y = 5;
        Assert.Empty(names);
    }
}
