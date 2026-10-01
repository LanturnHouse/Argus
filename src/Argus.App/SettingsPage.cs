using System.Windows;
using System.Windows.Controls;
using Argus.Core.Modules;

namespace Argus.App;

/// <summary>모듈들이 제공한 설정 UI(IArgusModule.CreateSettingsView)를 한 페이지에 모아 보여준다.</summary>
public sealed class SettingsPage : UserControl
{
    private sealed record Section(IArgusModule Module, UIElement View);

    /// <summary>한 탭(그룹)에 보이는 것: 모듈 하나의 설정 + 그 모듈 아래 하위 항목으로 지정한 다른 모듈들의 설정.</summary>
    private sealed record Group(Section Main, List<Section> Children);

    public static SettingsPage? Create(IEnumerable<IArgusModule> modules)
    {
        var sections = modules
            .Select(m => (Module: m, View: m.CreateSettingsView() as UIElement))
            .Where(s => s.View != null)
            .Select(s => new Section(s.Module, s.View!))
            .ToList();

        var groups = new List<Group>();
        foreach (var s in sections)
        {
            // 부모 모듈이 (설정을 가진 채) 있으면 그 아래 하위 항목, 없으면 독립 그룹
            var parent = s.Module.SettingsParentId is { } pid ? sections.FirstOrDefault(x => x.Module.Id == pid && x != s) : null;
            if (parent == null) groups.Add(new Group(s, []));
        }
        foreach (var s in sections)
            if (s.Module.SettingsParentId is { } pid && groups.FirstOrDefault(g => g.Main.Module.Id == pid) is { } g) g.Children.Add(s);

        return groups.Count == 0 ? null : new SettingsPage(groups);
    }

    private static string? _lastSelected;   // 다른 화면에 갔다 와도 보던 그룹을 유지

    private SettingsPage(List<Group> groups)
    {
        var stack = new StackPanel { MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };

        var title = new TextBlock { Text = "설정", FontSize = 24, FontWeight = FontWeights.Bold };
        var sub = new TextBlock { Text = "기능별 설정", Margin = new Thickness(0, 4, 0, 16) };
        sub.SetResourceReference(FrameworkElement.StyleProperty, "Dim");
        stack.Children.Add(title);
        stack.Children.Add(sub);

        // 기능(모듈)별 그룹 탭
        var tabs = new WrapPanel();
        var tabBar = new Border { Child = tabs, Margin = new Thickness(0, 0, 0, 16) };
        tabBar.SetResourceReference(FrameworkElement.StyleProperty, "SegmentBar");
        stack.Children.Add(tabBar);

        var groupTitle = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 10) };
        var body = new StackPanel();
        stack.Children.Add(groupTitle);
        stack.Children.Add(body);

        static Border Card(UIElement view, double bottom)
        {
            var c = new Border { Margin = new Thickness(0, 0, 0, bottom) };
            c.SetResourceReference(FrameworkElement.StyleProperty, "Card");
            c.Child = view;
            return c;
        }

        void Select(Group g)
        {
            _lastSelected = g.Main.Module.Id;
            groupTitle.Text = g.Main.Module.DisplayName;
            // 이전에 보이던 설정 뷰를 카드에서 떼어야 다시 붙일 수 있다.
            foreach (var old in body.Children.OfType<Border>()) old.Child = null;
            body.Children.Clear();
            body.Children.Add(Card(g.Main.View, g.Children.Count > 0 ? 22 : 0));
            foreach (var child in g.Children)
            {
                // 하위 항목: 소제목 + 카드
                var sub = new TextBlock { Text = child.Module.DisplayName, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 8) };
                body.Children.Add(sub);
                body.Children.Add(Card(child.View, 16));
            }
        }

        var selected = groups.FirstOrDefault(g => g.Main.Module.Id == _lastSelected) ?? groups[0];
        foreach (var group in groups)
        {
            var module = group.Main.Module;
            var icon = new TextBlock { Text = module.Icon, FontSize = 14, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            var label = new TextBlock { Text = module.DisplayName, VerticalAlignment = VerticalAlignment.Center };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (!string.IsNullOrEmpty(module.Icon)) content.Children.Add(icon);
            content.Children.Add(label);

            var tab = new RadioButton { GroupName = "settings-group", Content = content, Padding = new Thickness(10, 0, 10, 0) };
            tab.SetResourceReference(FrameworkElement.StyleProperty, "Segment");
            var g = group;
            tab.Checked += (_, _) => Select(g);
            tabs.Children.Add(tab);
            if (group == selected) tab.IsChecked = true;
        }

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack, Padding = new Thickness(0, 0, 12, 0) };
    }
}
