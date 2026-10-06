using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FRPMonitor;

public static class Theme
{
    public static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    public static readonly SolidColorBrush Background = Brush("#0C1422"), Surface = Brush("#141F30"), Border = Brush("#253348"),
        Text = Brush("#E7EDF8"), Muted = Brush("#94A5BE"), Upload = Brush("#60D5C5"), Download = Brush("#8AA8FF");
    public static TextBlock Label(string text, double size = 13, Brush? color = null, FontWeight? weight = null) => new()
    { Text = text, FontSize = size, Foreground = color ?? Text, FontWeight = weight ?? FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
    public static Border Card(UIElement child, Thickness? padding = null) => new()
    { Background = Surface, BorderBrush = Border, BorderThickness = new(1), CornerRadius = new(16), Padding = padding ?? new(20), Child = child };
    public static Button Button(string text, RoutedEventHandler action)
    {
        var b = new Button { Content = text, Padding = new(14, 9, 14, 9), Margin = new(6, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand };
        b.Click += action; return b;
    }
    public static void Install(System.Windows.Application app)
    {
        var window = new Style(typeof(Window));
        window.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Microsoft YaHei UI")));
        window.Setters.Add(new Setter(Control.ForegroundProperty, Text));
        window.Setters.Add(new Setter(Control.BackgroundProperty, Background));
        app.Resources.Add(typeof(Window), window);
        var button = new Style(typeof(Button));
        button.Setters.Add(new Setter(Control.ForegroundProperty, Text));
        button.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#202E43")));
        button.Setters.Add(new Setter(Control.BorderBrushProperty, Border));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(System.Windows.Controls.Border.BackgroundProperty, new System.Windows.TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new System.Windows.TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(1));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.MarginProperty, new System.Windows.TemplateBindingExtension(Control.PaddingProperty));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content); template.VisualTree = border;
        button.Setters.Add(new Setter(Control.TemplateProperty, template));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#30415A"))); button.Triggers.Add(hover);
        app.Resources.Add(typeof(Button), button);
    }
}
