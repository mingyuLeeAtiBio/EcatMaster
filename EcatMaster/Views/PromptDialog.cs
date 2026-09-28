using System.Windows;
using System.Windows.Controls;

namespace EcatMaster.Views;

/// <summary>한 줄 입력 대화상자 (별칭 변경 등). 코드로만 구성해 XAML 파일을 늘리지 않는다.</summary>
public static class PromptDialog
{
    public static string? Show(Window? owner, string title, string label, string initial)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 12), MinWidth = 260, Height = 26, VerticalContentAlignment = VerticalAlignment.Center };
        var ok = new Button { Content = "확인", IsDefault = true, MinWidth = 72, Height = 28, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "취소", IsCancel = true, MinWidth = 72, Height = 28 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var dlg = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => { dlg.DialogResult = true; };
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };

        return dlg.ShowDialog() == true ? box.Text.Trim() : null;
    }
}
