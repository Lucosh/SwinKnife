using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

namespace SwinKnife.Controls;

/// <summary>Editor di testo/codice (AvalonEdit) con evidenziazione della sintassi leggibile su sfondo scuro.</summary>
public static class CodeEditor
{
    private static bool _themed;

    public static TextEditor Create()
    {
        DarkenHighlighting();
        var ed = new TextEditor
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontSize = 13.5,
            ShowLineNumbers = true,
            WordWrap = true,
            Background = (Brush)Application.Current.FindResource("CanvasBrush"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE7)),
            LineNumbersForeground = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x76)),
            Padding = new Thickness(8),
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
        };
        ed.Options.EnableHyperlinks = true;
        ed.Options.HighlightCurrentLine = true;
        ed.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255));
        ed.TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        ed.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(110, 0xE5, 0x48, 0x4D));
        ed.TextArea.SelectionForeground = null;
        ed.TextArea.TextView.LinkTextForegroundBrush = new SolidColorBrush(Color.FromRgb(0x7A, 0xB7, 0xFF));
        return ed;
    }

    public static IHighlightingDefinition? ForExtension(string ext) =>
        ext switch
        {
            ".json" or ".jsonl" => HighlightingManager.Instance.GetDefinition("Json") ?? HighlightingManager.Instance.GetDefinition("JavaScript"),
            ".ts" or ".tsx" or ".jsx" or ".mjs" => HighlightingManager.Instance.GetDefinition("JavaScript"),
            ".yml" or ".yaml" or ".toml" or ".ini" or ".cfg" or ".conf" or ".env" or ".properties" => null,
            ".bat" or ".cmd" => null,
            ".xaml" or ".csproj" or ".props" or ".targets" or ".config" or ".manifest" or ".slnx" => HighlightingManager.Instance.GetDefinition("XML"),
            _ => HighlightingManager.Instance.GetDefinitionByExtension(ext),
        };

    /// <summary>Schiarisce i colori dei temi predefiniti, pensati per sfondo bianco.</summary>
    private static void DarkenHighlighting()
    {
        if (_themed) return;
        _themed = true;
        foreach (var def in HighlightingManager.Instance.HighlightingDefinitions)
        {
            foreach (var c in def.NamedHighlightingColors)
            {
                if (c.Foreground?.GetColor(null) is not { } col) continue;
                var lum = (0.2126 * col.R + 0.7152 * col.G + 0.0722 * col.B) / 255;
                if (lum < 0.55) c.Foreground = new SimpleHighlightingBrush(Ui.Lighten(col, 0.55 - lum * 0.4));
                c.Background = null;
            }
        }
    }
}
