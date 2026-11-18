using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ShareLink.BrowserSharing;

// Same cycle as Harmony DeviceName: hold, full left exit, invisible reset, fade in.
public static class MarqueeMotion
{
    public static double ExitSeconds(double textWidth) => 1 + Math.Ceiling((textWidth + 4) / 80 * 1000) / 1000;
    public static double CycleSeconds(double textWidth) => ExitSeconds(textWidth) + .2 + .26;
    public static DoubleAnimationUsingKeyFrames Translation(double textWidth)
    {
        double exit = ExitSeconds(textWidth), cycle = CycleSeconds(textWidth);
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(cycle), RepeatBehavior = RepeatBehavior.Forever };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-textWidth - 4, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(exit))));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(exit + .2))));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(cycle))));
        return animation;
    }
    public static DoubleAnimationUsingKeyFrames Opacity(double textWidth)
    {
        double exit = ExitSeconds(textWidth), cycle = CycleSeconds(textWidth);
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(cycle), RepeatBehavior = RepeatBehavior.Forever };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(exit))));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(exit + .2))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(cycle)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        return animation;
    }
    public static ClockGroup? Apply(TextBlock label, TranslateTransform translation, double width, bool active)
    {
        translation.BeginAnimation(TranslateTransform.XProperty, null);
        label.BeginAnimation(UIElement.OpacityProperty, null);
        translation.X = 0; label.Opacity = 1;
        if (!active) return null;
        // Start both clocks together so the reset remains invisible after any number of loops.
        var timeline = new ParallelTimeline { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(CycleSeconds(width)) };
        var move = Translation(width); move.RepeatBehavior = new RepeatBehavior(1);
        var fade = Opacity(width); fade.RepeatBehavior = new RepeatBehavior(1);
        timeline.Children.Add(move); timeline.Children.Add(fade);
        var clock = (ClockGroup)timeline.CreateClock(true);
        translation.ApplyAnimationClock(TranslateTransform.XProperty, (AnimationClock)clock.Children[0]);
        label.ApplyAnimationClock(UIElement.OpacityProperty, (AnimationClock)clock.Children[1]);
        return clock;
    }
}
