using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

public partial class HomePage : UserControl, IToolPage
{
    private readonly MainWindow _main;

    public HomePage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        foreach (var tool in Tools.All.Skip(1))
        {
            var content = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            head.Children.Add(new SymbolIcon
            {
                Symbol = tool.Icon, FontSize = 26, Margin = new Thickness(0, 0, 12, 0),
                Foreground = (System.Windows.Media.Brush)FindResource("SwinAccentBrush"),
            });
            head.Children.Add(new TextBlock { Text = tool.Title, FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(head);
            content.Children.Add(new TextBlock
            {
                Text = tool.Description, TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush"),
            });
            var card = new Button
            {
                Style = (Style)FindResource("HomeCard"), Content = content, Margin = new Thickness(0, 0, 14, 14),
                MinHeight = 128, VerticalContentAlignment = VerticalAlignment.Top,
            };
            var key = tool.Key;
            card.Click += (_, _) => _main.ShowPage(key);
            Cards.Items.Add(card);
        }
    }

    private void DropArea_Click(object sender, MouseButtonEventArgs e)
    {
        var path = Dlg.OpenFile(L.T("Apri un file"), key: "viewer");
        if (path != null) _main.OpenFile(path, "viewer");
    }
}
