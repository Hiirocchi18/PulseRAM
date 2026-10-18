using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace PulseRAM
{
    public sealed partial class MainWindow
    {
        #region Custom WinUI 3 XAML Animations

        private static TransitionCollection CreateFluentTransitions()
        {
            return new TransitionCollection
            {
                new EntranceThemeTransition { IsStaggeringEnabled = true },
                new RepositionThemeTransition()
            };
        }

        private void StartAmbientXamlAnimations()
        {
            // Ambient loops are skipped entirely in reduce-motion / lite mode (re-started if the user picks Full).
            if (_reduceMotion || _reactorBreathingStoryboard != null) return;
            StartAmbientXamlAnimationsCore();
            // Keep the storyboards consistent with the current paused state (e.g. launched hidden with --tray).
            if (_animationsPaused)
            {
                foreach (var sb in new[] { _reactorBreathingStoryboard, _reactorOrbitalStoryboard, _logoPulseStoryboard })
                {
                    try { sb?.Pause(); } catch { }
                }
            }
        }

        private void StartAmbientXamlAnimationsCore()
        {

            try
            {
                // 1. Reactor Halo Breathing Animation (Scale + Opacity)
                _reactorBreathingStoryboard = new Storyboard();
                var haloOpacity = new DoubleAnimation
                {
                    From = 0.18,
                    To = 0.58,
                    Duration = new Duration(TimeSpan.FromMilliseconds(2200)),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EnableDependentAnimation = true,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                Storyboard.SetTarget(haloOpacity, _reactorPulseHalo);
                Storyboard.SetTargetProperty(haloOpacity, "Opacity");
                _reactorBreathingStoryboard.Children.Add(haloOpacity);
                _reactorBreathingStoryboard.Begin();

                // 2. Reactor Dashed Orbital Ring Continuous 360 Rotation
                _reactorOrbitalStoryboard = new Storyboard();
                var rotAnim = new DoubleAnimation
                {
                    From = 0.0,
                    To = 360.0,
                    Duration = new Duration(TimeSpan.FromSeconds(18)),
                    RepeatBehavior = RepeatBehavior.Forever,
                    EnableDependentAnimation = true
                };
                Storyboard.SetTarget(rotAnim, _reactorOrbitalRing);
                Storyboard.SetTargetProperty(rotAnim, "(UIElement.RenderTransform).(CompositeTransform.Rotation)");
                _reactorOrbitalStoryboard.Children.Add(rotAnim);
                _reactorOrbitalStoryboard.Begin();

                // 3. Brand Emblem Pulse Animation
                _logoPulseStoryboard = new Storyboard();
                var logoPulse = new DoubleAnimation
                {
                    From = 0.25,
                    To = 0.85,
                    Duration = new Duration(TimeSpan.FromMilliseconds(1600)),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EnableDependentAnimation = true,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                Storyboard.SetTarget(logoPulse, _logoGlowRing);
                Storyboard.SetTargetProperty(logoPulse, "Opacity");
                _logoPulseStoryboard.Children.Add(logoPulse);
                _logoPulseStoryboard.Begin();
            }
            catch { }
        }

        private void TriggerReactorShockwaveAnimation()
        {
            if (_reduceMotion) return;
            try
            {
                _reactorShockwaveRing.Visibility = Visibility.Visible;
                var sb = new Storyboard();

                var scaleX = new DoubleAnimation
                {
                    From = 0.72,
                    To = 1.18,
                    Duration = new Duration(TimeSpan.FromMilliseconds(680)),
                    EnableDependentAnimation = true,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(scaleX, _reactorShockwaveRing);
                Storyboard.SetTargetProperty(scaleX, "(UIElement.RenderTransform).(CompositeTransform.ScaleX)");

                var scaleY = new DoubleAnimation
                {
                    From = 0.72,
                    To = 1.18,
                    Duration = new Duration(TimeSpan.FromMilliseconds(680)),
                    EnableDependentAnimation = true,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(scaleY, _reactorShockwaveRing);
                Storyboard.SetTargetProperty(scaleY, "(UIElement.RenderTransform).(CompositeTransform.ScaleY)");

                var fade = new DoubleAnimation
                {
                    From = 0.95,
                    To = 0.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(680)),
                    EnableDependentAnimation = true,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(fade, _reactorShockwaveRing);
                Storyboard.SetTargetProperty(fade, "Opacity");

                sb.Children.Add(scaleX);
                sb.Children.Add(scaleY);
                sb.Children.Add(fade);
                sb.Begin();
            }
            catch { }
        }

        private static void AnimateViewEntrance(UIElement view)
        {
            try
            {
                view.RenderTransform = new CompositeTransform { TranslateY = 14 };
                var sb = new Storyboard();

                var fade = new DoubleAnimation
                {
                    From = 0.0,
                    To = 1.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                    EnableDependentAnimation = true,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(fade, view);
                Storyboard.SetTargetProperty(fade, "Opacity");

                var slide = new DoubleAnimation
                {
                    From = 14.0,
                    To = 0.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                    EnableDependentAnimation = true,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(slide, view);
                Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");

                sb.Children.Add(fade);
                sb.Children.Add(slide);
                sb.Begin();
            }
            catch { }
        }

        private void OnGaugeAnimationFrame(DispatcherQueueTimer sender, object args) => StepGauge(0.16);

        /// <summary>Moves the gauge toward its targets; factor 1.0 jumps straight there (reduce-motion).</summary>
        private void StepGauge(double lerp)
        {
            try
            {
                bool changed = false;

                if (Math.Abs(_targetPercent - _displayedPercent) > 0.05)
                {
                    _displayedPercent += (_targetPercent - _displayedPercent) * lerp;
                    _gaugePercentText.Text = $"{_displayedPercent:F1}%";
                    changed = true;
                }
                else if (_displayedPercent != _targetPercent)
                {
                    _displayedPercent = _targetPercent;
                    _gaugePercentText.Text = $"{_displayedPercent:F1}%";
                }

                if (Math.Abs(_targetUsedRatio - _displayedUsedRatio) > 0.0015 ||
                    Math.Abs(_targetCombinedRatio - _displayedCombinedRatio) > 0.0015 ||
                    Math.Abs(_targetThresholdPct - _displayedThresholdPct) > 0.1)
                {
                    _displayedUsedRatio += (_targetUsedRatio - _displayedUsedRatio) * lerp;
                    _displayedCombinedRatio += (_targetCombinedRatio - _displayedCombinedRatio) * lerp;
                    _displayedThresholdPct += (_targetThresholdPct - _displayedThresholdPct) * lerp;
                    changed = true;
                }

                if (changed)
                {
                    UpdateGaugeArc(_gaugeUsedPath, 110, 110, 92, _displayedUsedRatio);
                    UpdateGaugeArc(_gaugeCachePath, 110, 110, 92, _displayedCombinedRatio);
                    UpdateThresholdMarkerDot(110, 110, 92, _displayedThresholdPct);
                }
            }
            catch { }
        }

        #endregion
    }
}
