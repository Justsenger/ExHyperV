// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media.Animation;

namespace Wpf.Ui.Controls;

// ExHyperV 性能模式挂钩：宿主 App 在启动最早期（静态构造）置 Reduced=true 即令全部动画瞬切。
// 用值型 MarkupExtension（返回 Duration/KeyTime 值、非资源引用），避开模板 Storyboard freeze 后 DynamicResource 失效的问题。
public static class UiPerformance
{
    public static bool Reduced;
}

// 包裹 Animation.Duration：Reduced 时返回 0 → 动画瞬间到终态。
[MarkupExtensionReturnType(typeof(Duration))]
public sealed class MotionExtension : MarkupExtension
{
    public Duration Duration { get; set; }

    public MotionExtension() { }

    public MotionExtension(Duration duration) => Duration = duration;

    public override object ProvideValue(IServiceProvider serviceProvider)
        => UiPerformance.Reduced ? new Duration(TimeSpan.Zero) : Duration;
}

// 包裹 KeyFrame.KeyTime：有些动画（如 CardExpander 展开、Snackbar 滑入）靠 KeyTime 定时、无 Duration，
// Reduced 时把 KeyTime 也归零，否则这些关键帧动画仍会逐帧播放（软渲染下卡）。
[MarkupExtensionReturnType(typeof(KeyTime))]
public sealed class MotionKeyExtension : MarkupExtension
{
    public KeyTime KeyTime { get; set; }

    public MotionKeyExtension() { }

    public MotionKeyExtension(KeyTime keyTime) => KeyTime = keyTime;

    public override object ProvideValue(IServiceProvider serviceProvider)
        => UiPerformance.Reduced ? KeyTime.FromTimeSpan(TimeSpan.Zero) : KeyTime;
}
