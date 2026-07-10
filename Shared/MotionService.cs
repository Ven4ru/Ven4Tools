using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Ven4Tools.Shared
{
    /// <summary>Small, shared motion vocabulary for both desktop applications.</summary>
    public static class MotionService
    {
        public static bool Enabled { get; set; } = true;

        public static void FadeIn(UIElement element, double durationMs = 160)
        {
            if (!Enabled) { element.Opacity = 1; return; }
            element.Opacity = 0;
            element.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(durationMs)) { EasingFunction = Ease() });
        }

        public static void SlideIn(UIElement element, double offset = 8, double durationMs = 180)
        {
            if (element.RenderTransform is not TranslateTransform transform)
            {
                transform = new TranslateTransform();
                element.RenderTransform = transform;
            }
            if (!Enabled) { transform.Y = 0; element.Opacity = 1; return; }
            transform.Y = offset;
            element.Opacity = 0;
            transform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(offset, 0, TimeSpan.FromMilliseconds(durationMs)) { EasingFunction = Ease() });
            element.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(durationMs)) { EasingFunction = Ease() });
        }

        public static void Pulse(FrameworkElement element, double scale = 1.035, double durationMs = 180)
        {
            if (!Enabled) return;
            if (element.RenderTransform is not ScaleTransform transform)
            {
                transform = new ScaleTransform(1, 1);
                element.RenderTransform = transform;
                element.RenderTransformOrigin = new Point(0.5, 0.5);
            }
            var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(durationMs) };
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0)));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(scale, KeyTime.FromPercent(0.45), Ease()));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), Ease()));
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation.Clone());
        }

        public static void CrossFade(UIElement element, double durationMs = 180)
        {
            if (!Enabled) return;
            element.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(durationMs),
                    KeyFrames = { new DiscreteDoubleKeyFrame(0.72, KeyTime.FromPercent(0.35)), new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), Ease()) } });
        }

        private static CubicEase Ease() => new() { EasingMode = EasingMode.EaseOut };
    }
}
