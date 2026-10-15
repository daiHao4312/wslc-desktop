using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace WSLC_Panel_App.Helpers;

/// <summary>
/// 批量操作条的进出场动画。
///
/// 这条 bar 改成浮在列表底部、不再占用独立布局行，所以它出现/消失不会把列表整体顶下去。
/// 原来"突兀"的根因正是布局跳动：整块列表内容瞬间位移 44px，横向对齐也一起变。
/// 现在只有透明度和 14px 位移在做动画，列表纹丝不动，视觉上就是"滑上来"。
///
/// Show/Hide 竞态（曾出现的 bug）：Hide 的 140ms 退出动画期间 Visibility 仍是 Visible，
/// 此时 Show 若直接 early-return，随后 Hide 的 Completed 会把条收起——用户刚重新勾上选项，
/// 批量条却挂着"已选 N 项"消失了。解法是给每条 bar 记一个"显示代数"：
/// Show 递增代数并停掉在途退出动画；Hide 的 Completed 只有代数没变才真正收起。
/// </summary>
internal static class BatchBarAnimator
{
    private const double SlideDistance = 14;

    private static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(140);

    /// <summary>每条 bar 一份动画状态，随元素生命周期自动回收（ConditionalWeakTable）。</summary>
    private sealed class BarState
    {
        /// <summary>显示代数：每次 Show 递增；Hide 的 Completed 只有代数没变才真正收起。</summary>
        public long Generation;

        /// <summary>在途的退出动画；Show 时要先停掉，避免两个故事板抢同一属性。</summary>
        public Storyboard? ExitStoryboard;
    }

    private static readonly ConditionalWeakTable<FrameworkElement, BarState> States = new();

    private static BarState StateFor(FrameworkElement bar)
        => States.GetValue(bar, static _ => new BarState());

    public static void Show(FrameworkElement bar)
    {
        var state = StateFor(bar);
        state.Generation++;

        // 已经是稳定显示态（可见、且没有在途退出动画）：不必重放
        if (bar.Visibility == Visibility.Visible && state.ExitStoryboard is null) return;

        // 退出动画进行中又被请求显示：先停掉在途动画，否则两个故事板会抢 Opacity/TranslateY
        state.ExitStoryboard?.Stop();
        state.ExitStoryboard = null;

        var transform = EnsureTransform(bar);

        // 先把起始态摆好再显示，否则会有一帧满不透明度/零位移的闪烁
        bar.Opacity = 0;
        transform.TranslateY = SlideDistance;
        bar.Visibility = Visibility.Visible;

        Build(bar, 0, SlideDistance, 1, 0, EnterDuration, EasingMode.EaseOut).Begin();
    }

    public static void Hide(FrameworkElement bar)
    {
        if (bar.Visibility != Visibility.Visible) return;

        var state = StateFor(bar);
        var myGeneration = state.Generation;
        var transform = EnsureTransform(bar);

        var storyboard = Build(
            bar, bar.Opacity, transform.TranslateY, 0, SlideDistance, ExitDuration, EasingMode.EaseIn);

        state.ExitStoryboard = storyboard;

        storyboard.Completed += (_, _) =>
        {
            // 动画期间又被 Show（代数已变、退出动画已被替换）：不收起，让入场动画放完
            if (state.ExitStoryboard != storyboard || state.Generation != myGeneration) return;

            state.ExitStoryboard = null;
            bar.Visibility = Visibility.Collapsed;

            // 复位：Show 依赖这两个初始值，残留会导致下次滑入位置奇怪
            bar.Opacity = 1;
            transform.TranslateY = 0;
        };

        storyboard.Begin();
    }

    private static Storyboard Build(
        FrameworkElement bar,
        double fromOpacity,
        double fromOffset,
        double toOpacity,
        double toOffset,
        TimeSpan duration,
        EasingMode mode)
    {
        var easing = new CubicEase { EasingMode = mode };

        var opacity = new DoubleAnimation
        {
            From = fromOpacity,
            To = toOpacity,
            Duration = duration,
            EasingFunction = easing
        };
        Storyboard.SetTarget(opacity, bar);
        Storyboard.SetTargetProperty(opacity, "Opacity");

        // 位移动画是依赖动画，必须显式放行，否则会被合成器直接忽略
        var slide = new DoubleAnimation
        {
            From = fromOffset,
            To = toOffset,
            Duration = duration,
            EasingFunction = easing,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(slide, bar);
        Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");

        var storyboard = new Storyboard();
        storyboard.Children.Add(opacity);
        storyboard.Children.Add(slide);
        return storyboard;
    }

    private static CompositeTransform EnsureTransform(FrameworkElement bar)
    {
        if (bar.RenderTransform is CompositeTransform existing) return existing;

        var transform = new CompositeTransform();
        bar.RenderTransform = transform;
        return transform;
    }
}
