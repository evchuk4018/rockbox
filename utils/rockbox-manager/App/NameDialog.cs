using System.Windows;
using System.Windows.Controls;

namespace RockboxManager;

internal static class NameDialog
{
    public static string? Ask(Window owner, string title, string initial = "")
    {
        var dialog = new Window { Owner = owner, Title = title, Width = 440, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 21, Margin = new Thickness(0, 0, 0, 16) });
        var input = new TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 20) };
        panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var save = new Button { Content = "Save", IsDefault = true, Style = (Style)Application.Current.FindResource("Primary") };
        save.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text : null;
    }
}
