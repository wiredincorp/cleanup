using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace Cleanup.Services;

/// <summary>
/// One motion system for the whole app: the same durations and curves everywhere, applied only to opacity,
/// translation and scale, so moving never re-lays out a page. Every helper does nothing, or only sets the final
/// state, when Windows "Animation effects" is turned off.
/// </summary>
internal static class Motion
{
    /// <summary>How long something arrives: a short, decelerating rise and fade.</summary>
    public static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(240);

    /// <summary>How long something leaves: quick, so the next page is never kept waiting.</summary>
    public static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(120);

    /// <summary>Distance a page or card rises as it arrives.</summary>
    public const float DefaultRise = 10;

    private static readonly UISettings Ui = new();
    private static readonly List<(WeakReference<UIElement> Element, float Rise)> Registrations = [];
    private static readonly ConditionalWeakTable<object, DispatcherQueueTimer> Tweens = new();

    static Motion()
    {
        Ui.AnimationsEnabledChanged += (_, _) =>
        {
            lock (Registrations)
            {
                Registrations.RemoveAll(entry => !entry.Element.TryGetTarget(out _));
                foreach (var entry in Registrations)
                    if (entry.Element.TryGetTarget(out var element))
                        element.DispatcherQueue.TryEnqueue(() => ConfigureShowHide(element, entry.Rise));
            }
        };
    }

    public static bool Enabled => Ui.AnimationsEnabled;

    // Arrivals decelerate into place; departures accelerate away.
    private static CompositionEasingFunction Arrive(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));

    private static CompositionEasingFunction Depart(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));

    /// <summary>Fades and rises the element in when it becomes visible, and fades it out when hidden.</summary>
    public static void ShowHide(UIElement element, float rise = 12)
    {
        lock (Registrations)
        {
            // Pages are rebuilt on each visit, so drop registrations of pages that were collected.
            Registrations.RemoveAll(entry => !entry.Element.TryGetTarget(out _));
            Registrations.Add((new WeakReference<UIElement>(element), rise));
        }
        ConfigureShowHide(element, rise);
    }

    private static void ConfigureShowHide(UIElement element, float rise)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        // Translation must be enabled before it is read, stopped or reset, even
        // when animations are off; otherwise XAML rejects the element at startup.
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        if (!Enabled)
        {
            ElementCompositionPreview.SetImplicitShowAnimation(element, null);
            ElementCompositionPreview.SetImplicitHideAnimation(element, null);
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.StopAnimation(nameof(Visual.Scale));
            visual.StopAnimation("Translation");
            visual.Opacity = 1;
            visual.Scale = Vector3.One;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            return;
        }
        Compositor compositor = visual.Compositor;

        ScalarKeyFrameAnimation fadeIn = compositor.CreateScalarKeyFrameAnimation();
        fadeIn.Target = "Opacity";
        fadeIn.InsertKeyFrame(0f, 0f);
        fadeIn.InsertKeyFrame(1f, 1f, Arrive(compositor));
        fadeIn.Duration = EnterDuration;
        Vector3KeyFrameAnimation slideIn = compositor.CreateVector3KeyFrameAnimation();
        slideIn.Target = "Translation";
        slideIn.InsertKeyFrame(0f, new Vector3(0, rise, 0));
        slideIn.InsertKeyFrame(1f, Vector3.Zero, Arrive(compositor));
        slideIn.Duration = EnterDuration;
        CompositionAnimationGroup show = compositor.CreateAnimationGroup();
        show.Add(fadeIn);
        show.Add(slideIn);
        ElementCompositionPreview.SetImplicitShowAnimation(element, show);

        ScalarKeyFrameAnimation fadeOut = compositor.CreateScalarKeyFrameAnimation();
        fadeOut.Target = "Opacity";
        fadeOut.InsertKeyFrame(1f, 0f, Depart(compositor));
        fadeOut.Duration = ExitDuration;
        ElementCompositionPreview.SetImplicitHideAnimation(element, fadeOut);
    }

    /// <summary>
    /// Fades and rises an element into place. It is held invisible from the first frame, so it never flashes
    /// at full opacity before its animation starts.
    /// </summary>
    public static void Enter(UIElement element, float rise = DefaultRise, TimeSpan delay = default)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        if (!Enabled)
        {
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.StopAnimation("Translation");
            visual.Opacity = 1;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            return;
        }
        Compositor compositor = visual.Compositor;
        visual.Opacity = 0;
        visual.Properties.InsertVector3("Translation", new Vector3(0, rise, 0));

        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, Arrive(compositor));
        fade.Duration = EnterDuration;
        fade.DelayTime = delay;
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        Vector3KeyFrameAnimation slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0f, new Vector3(0, rise, 0));
        slide.InsertKeyFrame(1f, Vector3.Zero, Arrive(compositor));
        slide.Duration = EnterDuration;
        slide.DelayTime = delay;
        slide.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation(nameof(Visual.Opacity), fade);
        visual.StartAnimation("Translation", slide);
    }

    /// <summary>Fades an element out. The task finishes when the fade is over. Call only when <see cref="Enabled"/>.</summary>
    public static async Task Exit(UIElement element)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        ScalarKeyFrameAnimation fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 0f, Depart(visual.Compositor));
        fade.Duration = ExitDuration;
        visual.StartAnimation(nameof(Visual.Opacity), fade);
        await Task.Delay(ExitDuration);
    }

    /// <summary>
    /// Calls <paramref name="apply"/> once per frame with a value that eases from 0 to 1 over
    /// <paramref name="duration"/>, after <paramref name="delay"/>. Use it for values that must move smoothly, such as
    /// a ring's share or a bar's length. It replaces any tween already running for the same <paramref name="owner"/>.
    /// Call only when <see cref="Enabled"/>; otherwise set the final value directly.
    /// </summary>
    public static void Tween(object owner, DispatcherQueue queue, TimeSpan duration, TimeSpan delay, Action<double> apply)
    {
        Stop(owner);
        var clock = Stopwatch.StartNew();
        DispatcherQueueTimer timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            double elapsed = clock.Elapsed.TotalMilliseconds - delay.TotalMilliseconds;
            double progress = Math.Clamp(elapsed / duration.TotalMilliseconds, 0, 1);
            apply(1 - Math.Pow(1 - progress, 3)); // Ease out: quick start, gentle landing.
            if (progress >= 1)
            {
                timer.Stop();
                Tweens.Remove(owner);
            }
        };
        Tweens.AddOrUpdate(owner, timer);
        timer.Start();
    }

    /// <summary>Ends a tween started for <paramref name="owner"/>, leaving the value where it is.</summary>
    public static void Stop(object owner)
    {
        if (Tweens.TryGetValue(owner, out DispatcherQueueTimer? timer))
        {
            timer.Stop();
            Tweens.Remove(owner);
        }
    }

    /// <summary>Scales and fades a small element in, optionally after a delay.</summary>
    public static void PopIn(FrameworkElement element, TimeSpan delay)
    {
        if (!Enabled) return;
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        Compositor compositor = visual.Compositor;
        visual.CenterPoint = new Vector3((float)element.Width / 2, (float)element.Height / 2, 0);

        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, Arrive(compositor));
        fade.Duration = TimeSpan.FromMilliseconds(220);
        fade.DelayTime = delay;
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        Vector3KeyFrameAnimation scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, new Vector3(0.5f, 0.5f, 1f));
        scale.InsertKeyFrame(1f, Vector3.One, compositor.CreateCubicBezierEasingFunction(new Vector2(0.3f, 1.4f), new Vector2(0.5f, 1f)));
        scale.Duration = TimeSpan.FromMilliseconds(300);
        scale.DelayTime = delay;
        scale.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation(nameof(Visual.Opacity), fade);
        visual.StartAnimation(nameof(Visual.Scale), scale);
    }

    /// <summary>A brief scale bump to draw the eye to a changed value.</summary>
    public static void Bump(FrameworkElement element)
    {
        if (!Enabled || element.ActualWidth <= 0) return;
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        Compositor compositor = visual.Compositor;
        visual.CenterPoint = new Vector3(0, (float)element.ActualHeight / 2, 0);
        Vector3KeyFrameAnimation bump = compositor.CreateVector3KeyFrameAnimation();
        bump.InsertKeyFrame(0f, Vector3.One);
        bump.InsertKeyFrame(0.35f, new Vector3(1.06f, 1.06f, 1f));
        bump.InsertKeyFrame(1f, Vector3.One, Arrive(compositor));
        bump.Duration = TimeSpan.FromMilliseconds(280);
        visual.StartAnimation(nameof(Visual.Scale), bump);
    }
}
