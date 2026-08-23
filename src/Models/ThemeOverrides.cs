using System.Collections.Generic;
using Avalonia.Media;

namespace SourceGit.Models
{
    public class ThemeOverrides
    {
        public Dictionary<string, Color> BasicColors { get; set; } = new Dictionary<string, Color>();
        public double GraphPenThickness { get; set; } = 2;
        public double OpacityForNotMergedCommits { get; set; } = 0.5;
        public List<Color> GraphColors { get; set; } = new List<Color>();

        // 新增：悬浮过渡时长（毫秒）。null = 使用代码默认 160ms
        public double? HoverTransitionMs { get; set; } = null;

        // 新增：悬浮过渡缓动名（大小写不敏感，例如 "CubicEaseOut"、"Linear"、"QuadraticEaseIn"）。
        // null / 空 / 未识别 = 使用代码默认 CubicEaseOut
        public string HoverTransitionEasing { get; set; } = null;

        // 新增：是否整体关闭这次新加的细腻 hover 底色（true 时 Color.SubtleHover 强制透明，等同于修改前的无色硬反馈）。
        // null = 保持现有主题/默认的 SubtleHover 颜色
        public bool? DisableSubtleHover { get; set; } = null;
    }
}
