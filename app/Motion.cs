using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RemoteRooms;

internal static class Motion
{
    private static bool Enabled => SystemParameters.ClientAreaAnimation;

    private static DoubleAnimation Transition(double from, double to, int milliseconds) => new(from, to,
        TimeSpan.FromMilliseconds(milliseconds))
    {
        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        FillBehavior = FillBehavior.Stop
    };

    public static void Reveal(FrameworkElement element, double offsetX = 0, double offsetY = 0,
        int milliseconds = 180, double fromOpacity = 0)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
        if ((offsetX != 0 || offsetY != 0) && element.RenderTransform is not TranslateTransform)
            element.RenderTransform = new TranslateTransform();
        if (element.RenderTransform is TranslateTransform move)
        {
            move.BeginAnimation(TranslateTransform.XProperty, null);
            move.BeginAnimation(TranslateTransform.YProperty, null);
            move.X = 0;
            move.Y = 0;
            if (Enabled && element.IsLoaded)
            {
                if (offsetX != 0) move.BeginAnimation(TranslateTransform.XProperty,
                    Transition(offsetX, 0, milliseconds));
                if (offsetY != 0) move.BeginAnimation(TranslateTransform.YProperty,
                    Transition(offsetY, 0, milliseconds));
            }
        }
        if (Enabled && element.IsLoaded && fromOpacity < 1)
            element.BeginAnimation(UIElement.OpacityProperty, Transition(fromOpacity, 1, milliseconds));
    }

    public static void Opacity(FrameworkElement element, double target, int milliseconds = 110)
    {
        var current = element.Opacity;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = target;
        if (Enabled && element.IsLoaded && Math.Abs(current - target) > .01)
            element.BeginAnimation(UIElement.OpacityProperty, Transition(current, target, milliseconds));
    }

    public static void Scale(FrameworkElement element, double target, int milliseconds = 130)
    {
        if (element.RenderTransform is not ScaleTransform scale) return;
        var current = scale.ScaleX;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = target;
        scale.ScaleY = target;
        if (Enabled && element.IsLoaded && Math.Abs(current - target) > .001)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Transition(current, target, milliseconds));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Transition(current, target, milliseconds));
        }
    }

    public static void Zoom(ScaleTransform scale, double target, bool animate)
    {
        var current = scale.ScaleX;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = target;
        scale.ScaleY = target;
        if (animate && Enabled && Math.Abs(current - target) > .001)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Transition(current, target, 180));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Transition(current, target, 180));
        }
    }
}
