using System.Windows;
using System.Windows.Media;

namespace TokenFloat.Services;

/// <summary>
/// 用一整份颜色字典替换主题。画笔放进资源后会被冻结，所以不再改已有画笔的颜色。
/// </summary>
public static class DashboardTheme
{
    private static ResourceDictionary? _themeDictionary;

    public static void Apply(bool dark)
    {
        if (System.Windows.Application.Current is null)
        {
            return;
        }

        var next = new ResourceDictionary();
        Add(next, "DashboardBackground", dark ? "#000000" : "#FFFFFF");
        Add(next, "DashboardPanel", dark ? "#000000" : "#FFFFFF");
        Add(next, "DashboardBorder", dark ? "#2E2E2E" : "#E5E5E5");
        Add(next, "DashboardText", dark ? "#FFFFFF" : "#111111");
        Add(next, "DashboardMuted", dark ? "#A3A3A3" : "#666666");
        Add(next, "DashboardSubtle", dark ? "#737373" : "#999999");
        Add(next, "DashboardGreen", dark ? "#7DCEA0" : "#1F7A45");
        Add(next, "DashboardGreenSoft", dark ? "#16301F" : "#E8F5EE");
        Add(next, "DashboardBlue", dark ? "#7EB6E0" : "#1D6FB8");
        Add(next, "DashboardBlueSoft", dark ? "#122433" : "#E7F2FB");
        Add(next, "DashboardPink", dark ? "#F0A0B4" : "#C43B5C");
        Add(next, "DashboardPinkSoft", dark ? "#3A1822" : "#FDECEF");
        Add(next, "DashboardOrange", dark ? "#F0B45A" : "#C56A00");
        Add(next, "DashboardOrangeSoft", dark ? "#3A2810" : "#FFF3E4");
        Add(next, "DashboardViolet", dark ? "#C4B0F0" : "#6B4FB5");
        Add(next, "DashboardTrack", dark ? "#1A1A1A" : "#F0F0F0");
        Add(next, "DashboardSegment", dark ? "#141414" : "#F3F3F3");
        Add(next, "DashboardDanger", dark ? "#FF8A80" : "#C62828");
        Add(next, "DashboardHover", dark ? "#222222" : "#EAEAEA");
        Add(next, "DashboardPressed", dark ? "#2A2A2A" : "#DEDEDE");
        Add(next, "DashboardPopup", dark ? "#000000" : "#FFFFFF");
        Add(next, "DashboardKnob", dark ? "#F5F5F5" : "#FFFFFF");
        Add(next, "DashboardScroll", dark ? "#4A4A4A" : "#BDBDBD");
        Add(next, "DashboardShadow", dark ? "#33FFFFFF" : "#1F000000");
        Add(next, "DashboardSelection", dark ? "#667DCEA0" : "#661F7A45");
        Add(next, "DashboardFocus", dark ? "#997DCEA0" : "#991F7A45");

        var merged = System.Windows.Application.Current.Resources.MergedDictionaries;
        if (_themeDictionary is not null)
        {
            merged.Remove(_themeDictionary);
        }

        merged.Add(next);
        _themeDictionary = next;
        InkContextMenuRenderer.ApplyTheme(dark);
    }

    private static void Add(ResourceDictionary dictionary, string key, string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        dictionary.Add(key, new SolidColorBrush(color));
    }
}
