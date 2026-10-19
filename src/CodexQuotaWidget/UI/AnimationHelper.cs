using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CodexQuotaWidget;

internal static class AnimationDurations
{
    public static readonly TimeSpan ButtonHover = TimeSpan.FromMilliseconds(130);
    public static readonly TimeSpan CapsuleExpand = TimeSpan.FromMilliseconds(600);
    public static readonly TimeSpan CapsuleCollapse = TimeSpan.FromMilliseconds(480);
    public static readonly TimeSpan CapsuleContent = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan CapsuleContentStagger = TimeSpan.FromMilliseconds(60);
    public static readonly TimeSpan CapsuleHoverDelay = TimeSpan.FromMilliseconds(600);
    public static readonly TimeSpan CapsuleClickGrace = CapsuleExpand;
    public static readonly TimeSpan WindowShow = TimeSpan.FromMilliseconds(240);
    public static readonly TimeSpan WindowHide = TimeSpan.FromMilliseconds(220);
    public static readonly TimeSpan Ring = TimeSpan.FromMilliseconds(420);
    public static readonly TimeSpan Refresh = TimeSpan.FromMilliseconds(280);
    public static readonly TimeSpan Success = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan SuccessFlashPeak = TimeSpan.FromMilliseconds(110);
    public static readonly TimeSpan Badge = TimeSpan.FromMilliseconds(270);
    public static readonly TimeSpan BadgeRise = TimeSpan.FromMilliseconds(105);
    public static readonly TimeSpan BadgeAccentPeak = TimeSpan.FromMilliseconds(95);
    public static readonly TimeSpan ResetPulse = TimeSpan.FromMilliseconds(650);
    public static readonly TimeSpan ResetPulsePeak = TimeSpan.FromMilliseconds(240);
}

internal static class AnimationHelper
{
    public static bool Enabled { get; set; } = true;

    public static void AttachButtonHover(Button button)
    {
        if (button is SkillUpdateButton) return; // This button owns its press, hover and progress motion.
        var scale = new ScaleTransform(1, 1);
        button.RenderTransformOrigin = new Point(.5, .5);
        button.RenderTransform = scale;
        button.MouseEnter += (_, _) => AnimateScale(scale, 1.035, AnimationDurations.ButtonHover);
        button.MouseLeave += (_, _) => AnimateScale(scale, 1, AnimationDurations.ButtonHover);
    }

    public static void AttachButtonHover(DependencyObject root)
    {
        if (root is Button button)
            AttachButtonHover(button);

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            AttachButtonHover(VisualTreeHelper.GetChild(root, i));
    }

    public static void Reveal(FrameworkElement element, TimeSpan? duration = null)
    {
        var (scale, translate) = GetTransitionTransform(element);
        if (!Enabled)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            element.Opacity = 1;
            scale.ScaleX = scale.ScaleY = 1;
            translate.Y = 0;
            return;
        }

        var time = duration ?? AnimationDurations.CapsuleExpand;
        var opacity = new DoubleAnimation(0, 1, time) { EasingFunction = EaseOut() };
        opacity.Completed += (_, _) =>
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            element.Opacity = 1;
            scale.ScaleX = scale.ScaleY = 1;
            translate.Y = 0;
        };
        element.BeginAnimation(UIElement.OpacityProperty, opacity);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.975, 1, time) { EasingFunction = EaseOut() });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.975, 1, time) { EasingFunction = EaseOut() });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, 0, time) { EasingFunction = EaseOut() });
    }

    public static void Conceal(FrameworkElement element, Action completed, TimeSpan? duration = null)
    {
        var (scale, translate) = GetTransitionTransform(element);
        if (!Enabled)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            element.Opacity = 0;
            scale.ScaleX = scale.ScaleY = 1;
            translate.Y = 0;
            completed();
            return;
        }

        var time = duration ?? AnimationDurations.CapsuleCollapse;
        var animation = new DoubleAnimation(element.Opacity, 0, time) { EasingFunction = EaseIn() };
        animation.Completed += (_, _) =>
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            element.Opacity = 0;
            scale.ScaleX = scale.ScaleY = 1;
            translate.Y = 0;
            completed();
        };
        element.BeginAnimation(UIElement.OpacityProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale.ScaleX, .975, time) { EasingFunction = EaseIn() });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale.ScaleY, .975, time) { EasingFunction = EaseIn() });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(translate.Y, -6, time) { EasingFunction = EaseIn() });
    }

    public static void AnimateWindowBounds(Window window, double width, double height, double left, double top, Action? completed = null, TimeSpan? duration = null)
    {
        if (!Enabled || !window.IsVisible)
        {
            StopWindowBounds(window);
            window.Width = width;
            window.Height = height;
            window.Left = left;
            window.Top = top;
            completed?.Invoke();
            return;
        }

        var time = duration ?? AnimationDurations.CapsuleExpand;
        var fromWidth = window.Width;
        var fromHeight = window.Height;
        var fromLeft = window.Left;
        var fromTop = window.Top;
        window.Width = width;
        window.Height = height;
        window.Left = left;
        window.Top = top;
        var animation = SpringTransition(fromHeight, height, time);
        animation.Completed += (_, _) => completed?.Invoke();
        window.BeginAnimation(FrameworkElement.WidthProperty, SpringTransition(fromWidth, width, time));
        window.BeginAnimation(FrameworkElement.HeightProperty, animation);
        window.BeginAnimation(Window.LeftProperty, SpringTransition(fromLeft, left, time));
        if (Math.Abs(fromTop - top) > .5)
            window.BeginAnimation(Window.TopProperty, SpringTransition(fromTop, top, time));
    }

    public static void StopWindowBounds(Window window)
    {
        window.BeginAnimation(FrameworkElement.WidthProperty, null);
        window.BeginAnimation(FrameworkElement.HeightProperty, null);
        window.BeginAnimation(Window.LeftProperty, null);
        window.BeginAnimation(Window.TopProperty, null);
    }

    public static void FadeTo(FrameworkElement element, double target, TimeSpan duration)
    {
        var current = element.Opacity;
        element.Opacity = target;
        element.BeginAnimation(UIElement.OpacityProperty,
            Enabled ? new DoubleAnimation(current, target, duration) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.Stop } : null);
    }

    public static void SlideCapsuleContent(FrameworkElement element, bool opening)
    {
        var (_, translate) = GetTransitionTransform(element);
        var current = opening && element.Opacity < .01 ? 7 : translate.Y;
        var target = opening ? 0 : -4;
        translate.Y = target;
        translate.BeginAnimation(TranslateTransform.YProperty, Enabled
            ? SpringTransition(current, target, opening ? AnimationDurations.CapsuleExpand : AnimationDurations.CapsuleCollapse)
            : null);
    }

    private static DoubleAnimationUsingKeyFrames SpringTransition(double from, double to, TimeSpan duration)
    {
        var distance = to - from;
        var animation = new DoubleAnimationUsingKeyFrames { Duration = duration, FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        // Native splines keep the resize and its small rebound on the same WPF clock.
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to + distance * .06,
            KeyTime.FromTimeSpan(TimeSpan.FromTicks((long)(duration.Ticks * .62))), new KeySpline(.32, 0, .20, 1)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to - distance * .009,
            KeyTime.FromTimeSpan(TimeSpan.FromTicks((long)(duration.Ticks * .82))), new KeySpline(.38, 0, .62, 1)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(duration), new KeySpline(.38, 0, .62, 1)));
        return animation;
    }

    public static void AnimateRing(Ring ring, double? oldValue, double? newValue, bool enabled)
    {
        ring.BeginAnimation(Ring.AnimatedValueProperty, null);
        if (newValue is null)
        {
            ring.SetCurrentValue(Ring.AnimatedValueProperty, 0d);
            return;
        }
        if (!Enabled || !enabled || oldValue is null)
        {
            ring.SetCurrentValue(Ring.AnimatedValueProperty, newValue.Value);
            return;
        }

        ring.SetCurrentValue(Ring.AnimatedValueProperty, oldValue.Value);
        var animation = new DoubleAnimation(oldValue.Value, newValue.Value, AnimationDurations.Ring) { EasingFunction = EaseOut() };
        animation.Completed += (_, _) =>
        {
            ring.BeginAnimation(Ring.AnimatedValueProperty, null);
            ring.SetCurrentValue(Ring.AnimatedValueProperty, newValue.Value);
        };
        ring.BeginAnimation(Ring.AnimatedValueProperty, animation);
    }

    public static void RotateOnce(Button button)
    {
        if (!Enabled)
            return;

        var transforms = button.RenderTransform as TransformGroup;
        ScaleTransform? scale = null;
        RotateTransform? rotate = null;
        if (transforms is not null)
        {
            foreach (var child in transforms.Children)
            {
                if (child is ScaleTransform foundScale) scale = foundScale;
                if (child is RotateTransform foundRotate) rotate = foundRotate;
            }
        }
        else
        {
            if (button.RenderTransform is ScaleTransform existingScale) scale = existingScale;
            if (button.RenderTransform is RotateTransform existingRotate) rotate = existingRotate;
        }
        transforms ??= new TransformGroup();
        if (scale is not null && !transforms.Children.Contains(scale)) transforms.Children.Add(scale);
        rotate ??= new RotateTransform();
        if (!transforms.Children.Contains(rotate)) transforms.Children.Add(rotate);
        button.RenderTransformOrigin = new Point(.5, .5);
        button.RenderTransform = transforms;
        var animation = new DoubleAnimation(rotate.Angle, rotate.Angle + 360, AnimationDurations.Refresh) { EasingFunction = EaseOut() };
        animation.Completed += (_, _) =>
        {
            rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            rotate.Angle = 0;
        };
        rotate.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    public static void RevealStaggered(params FrameworkElement[] elements)
    {
        for (var i = 0; i < elements.Length; i++)
        {
            var element = elements[i];
            if (!Enabled)
            {
                element.Opacity = 1;
                continue;
            }

            var (scale, translate) = GetTransitionTransform(element);
            var begin = TimeSpan.FromTicks(AnimationDurations.CapsuleContentStagger.Ticks * i);
            element.Opacity = 0;
            scale.ScaleX = scale.ScaleY = .975;
            translate.Y = -5;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.975, 1, AnimationDurations.CapsuleContent) { BeginTime = begin, EasingFunction = EaseOut() });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.975, 1, AnimationDurations.CapsuleContent) { BeginTime = begin, EasingFunction = EaseOut() });
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-5, 0, AnimationDurations.CapsuleContent) { BeginTime = begin, EasingFunction = EaseOut() });
            var finish = new DoubleAnimation(0, 1, AnimationDurations.CapsuleContent) { BeginTime = begin, EasingFunction = EaseOut() };
            finish.Completed += (_, _) =>
            {
                element.BeginAnimation(UIElement.OpacityProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                element.Opacity = 1;
                scale.ScaleX = scale.ScaleY = 1;
                translate.Y = 0;
            };
            element.BeginAnimation(UIElement.OpacityProperty, finish);
        }
    }

    public static void StopAnimations(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (element is Border border)
        {
            border.BeginAnimation(Border.BorderThicknessProperty, null);
        }
        if (element.RenderTransform is TransformGroup group)
            foreach (var child in group.Children)
            {
                // WPF templates may share frozen transforms; they cannot have active animations.
                if(child.IsFrozen)continue;
                if (child is ScaleTransform scale)
                {
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                }
                else if (child is TranslateTransform translate)
                    translate.BeginAnimation(TranslateTransform.YProperty, null);
                else if (child is RotateTransform rotate)
                    rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (VisualTreeHelper.GetChild(element, i) is FrameworkElement child)
                StopAnimations(child);
    }

    public static void Flash(FrameworkElement element)
    {
        if (!Enabled)
            return;

        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(.45, KeyTime.FromTimeSpan(AnimationDurations.SuccessFlashPeak)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(AnimationDurations.Success)));
        animation.Completed += (_, _) =>
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
        };
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    public static void AnimateBadge(FrameworkElement element, bool enabled, Border? accentTarget = null)
    {
        if (!enabled)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            return;
        }

        var scale = new ScaleTransform(1, 1);
        element.RenderTransformOrigin = new Point(.5, .5);
        element.RenderTransform = scale;
        var up = new DoubleAnimation(1, 1.08, AnimationDurations.BadgeRise) { EasingFunction = EaseOut() };
        up.Completed += (_, _) => scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1.08, 1, AnimationDurations.Badge - AnimationDurations.BadgeRise) { EasingFunction = EaseOut() });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, up);
        var downY = new DoubleAnimation(1, 1.08, AnimationDurations.BadgeRise) { EasingFunction = EaseOut() };
        downY.Completed += (_, _) =>
        {
            var settleY = new DoubleAnimation(1.08, 1, AnimationDurations.Badge - AnimationDurations.BadgeRise) { EasingFunction = EaseOut() };
            settleY.Completed += (_, _) =>
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = scale.ScaleY = 1;
            };
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, settleY);
        };
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, downY);
        if (accentTarget is Border accent)
        {
            var thickness = new ThicknessAnimationUsingKeyFrames();
            thickness.KeyFrames.Add(new LinearThicknessKeyFrame(new Thickness(1), KeyTime.FromTimeSpan(TimeSpan.Zero)));
            thickness.KeyFrames.Add(new LinearThicknessKeyFrame(new Thickness(1.6), KeyTime.FromTimeSpan(AnimationDurations.BadgeAccentPeak)));
            thickness.KeyFrames.Add(new LinearThicknessKeyFrame(new Thickness(1), KeyTime.FromTimeSpan(AnimationDurations.Badge)));
            thickness.Completed += (_, _) =>
            {
                accent.BeginAnimation(Border.BorderThicknessProperty, null);
                accent.BorderThickness = new Thickness(1);
            };
            accent.BeginAnimation(Border.BorderThicknessProperty, thickness);
        }
    }

    private static void AnimateScale(ScaleTransform scale, double target, TimeSpan duration)
    {
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Enabled ? new DoubleAnimation(scale.ScaleX, target, duration) { EasingFunction = EaseOut() } : null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Enabled ? new DoubleAnimation(scale.ScaleY, target, duration) { EasingFunction = EaseOut() } : null);
        if (!Enabled)
            scale.ScaleX = scale.ScaleY = target;
    }

    private static (ScaleTransform Scale, TranslateTransform Translate) GetTransitionTransform(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup existing && existing.Children.Count >= 2 &&
            existing.Children[0] is ScaleTransform currentScale && existing.Children[1] is TranslateTransform currentTranslate)
            return (currentScale, currentTranslate);

        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform();
        element.RenderTransformOrigin = new Point(.5, .5);
        element.RenderTransform = new TransformGroup { Children = { scale, translate } };
        return (scale, translate);
    }

    private static CubicEase EaseOut() => new() { EasingMode = EasingMode.EaseOut };
    private static CubicEase EaseIn() => new() { EasingMode = EasingMode.EaseIn };
}
