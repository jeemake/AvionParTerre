using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AvionParTerre.Revit.UI;

/// <summary>Charte K&amp;D appliquée à l'interface : Century Gothic, vert #7AB648, gris #66747C, fond blanc.</summary>
internal static class Theme
{
    public static readonly Color Vert = Color.FromRgb(0x7A, 0xB6, 0x48);
    public static readonly Color Gris = Color.FromRgb(0x66, 0x74, 0x7C);
    public static readonly Color Noir = Color.FromRgb(0x0B, 0x0C, 0x12);
    public static readonly Color GrisClair = Color.FromRgb(0xF2, 0xF2, 0xF2);
    public static readonly Color Rouge = Color.FromRgb(0xC0, 0x50, 0x4D);
    public static readonly FontFamily Font = new("Century Gothic, Segoe UI");

    public static SolidColorBrush B(Color c) => new(c);

    public static BitmapImage? Logo()
    {
        var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("AvionParTerre.Revit.Resources.LogoKDA.jpg");
        if (s == null) return null;
        var img = new BitmapImage();
        img.BeginInit();
        img.StreamSource = s;
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return img;
    }

    /// <summary>Image PNG intégrée au plugin (Resources/*.png).</summary>
    public static BitmapSource? Png(string name)
    {
        var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"AvionParTerre.Revit.Resources.{name}");
        if (s == null) return null;
        var dec = new PngBitmapDecoder(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var f = dec.Frames[0];
        f.Freeze();
        return f;
    }

    /// <summary>Icône de ruban : pastille verte et lettres blanches.</summary>
    public static BitmapSource Icon(string letters, int size)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRoundedRectangle(B(Vert), null, new Rect(1, 1, size - 2, size - 2), size / 6.0, size / 6.0);
            var ft = new FormattedText(letters, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Font, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), size * (letters.Length > 2 ? 0.36 : 0.46),
                Brushes.White, 1.0);
            dc.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2));
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    public static Button Button(string text, bool primary = false)
    {
        return new Button
        {
            Content = text,
            Padding = new Thickness(14, 5, 14, 5),
            Margin = new Thickness(6, 0, 0, 0),
            FontFamily = Font,
            Background = primary ? B(Vert) : B(GrisClair),
            Foreground = primary ? Brushes.White : B(Noir),
            BorderBrush = primary ? B(Vert) : B(Gris),
            FontWeight = primary ? FontWeights.Bold : FontWeights.Normal,
        };
    }

    public static Window Window(string title, double w, double h)
    {
        var win = new Window
        {
            Title = $"Avion par terre — {title}",
            Width = w,
            Height = h,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontFamily = Font,
            FontSize = 12,
            Background = Brushes.White,
            Foreground = B(Noir),
            ShowInTaskbar = false,
        };
        try { new WindowInteropHelper(win).Owner = Process.GetCurrentProcess().MainWindowHandle; } catch { }
        return win;
    }

    /// <summary>En-tête : logo K&amp;D en haut à gauche, titre gris, sous-titre vert.</summary>
    public static UIElement Header(string title, string subtitle)
    {
        var sp = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = true };
        var logo = Logo();
        if (logo != null)
        {
            var img = new Image { Source = logo, Height = 38, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(img, Dock.Left);
            sp.Children.Add(img);
        }
        var texts = new StackPanel();
        texts.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.Bold, Foreground = B(Gris) });
        texts.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = B(Vert), TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(texts);
        return sp;
    }

    /// <summary>Grille auto-générée : colonnes d'après les propriétés publiques ([DisplayName], [Browsable(false)]).</summary>
    public static DataGrid Grid(IEnumerable items, bool readOnly = false)
    {
        var g = new DataGrid
        {
            ItemsSource = items,
            AutoGenerateColumns = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            IsReadOnly = readOnly,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = B(GrisClair),
            AlternatingRowBackground = B(Color.FromRgb(0xFA, 0xFA, 0xFA)),
            SelectionMode = DataGridSelectionMode.Extended,
            FontFamily = Font,
            RowHeaderWidth = 0,
        };
        var headerStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, B(Vert)));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        headerStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Bold));
        headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4)));
        headerStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.White));
        headerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 0)));
        g.ColumnHeaderStyle = headerStyle;
        g.AutoGeneratedColumns += (_, _) =>
        {
            // La case à cocher (propriété héritée, générée en dernier) passe en première colonne
            var check = g.Columns.FirstOrDefault(c => Equals(c.Header, "✓"));
            if (check != null && g.Columns.IndexOf(check) > 0) { g.Columns.Remove(check); g.Columns.Insert(0, check); }
        };
        g.AutoGeneratingColumn += (_, e) =>
        {
            var pd = e.PropertyDescriptor as PropertyDescriptor;
            if (pd == null) return;
            if (!pd.IsBrowsable) { e.Cancel = true; return; }
            if (pd.PropertyType == typeof(bool) && !pd.IsReadOnly)
            {
                // Case à cocher active au premier clic, y compris dans une grille en lecture seule
                e.Column = CheckColumn(pd.Name, string.IsNullOrEmpty(pd.DisplayName) ? pd.Name : pd.DisplayName);
                return;
            }
            if (!string.IsNullOrEmpty(pd.DisplayName)) e.Column.Header = pd.DisplayName;
            if (pd.IsReadOnly && e.Column is DataGridBoundColumn bc) bc.IsReadOnly = true;
            if (pd.PropertyType == typeof(string) && e.Column is DataGridTextColumn tc)
                tc.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap) } };
            e.Column.MaxWidth = 380;
        };
        return g;
    }

    public static DataGridTemplateColumn CheckColumn(string path, string header)
    {
        var f = new FrameworkElementFactory(typeof(CheckBox));
        f.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        f.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        f.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        f.SetValue(UIElement.FocusableProperty, false);
        return new DataGridTemplateColumn
        {
            Header = header,
            CellTemplate = new DataTemplate { VisualTree = f },
            Width = new DataGridLength(34),
            CanUserSort = true,
            SortMemberPath = path,
        };
    }

    /// <summary>Colonne de liste déroulante modifiable (on choisit ou on tape), liée à une propriété texte et à sa liste d'options.</summary>
    public static DataGridTemplateColumn EditableComboColumn(string header, string textPath, string optionsPath, double width)
    {
        var f = new FrameworkElementFactory(typeof(ComboBox));
        f.SetValue(ComboBox.IsEditableProperty, true);
        f.SetValue(ComboBox.IsTextSearchEnabledProperty, false);
        f.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        f.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        f.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(optionsPath));
        f.SetBinding(ComboBox.TextProperty, new Binding(textPath) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        f.SetValue(ComboBox.ToolTipProperty, "Choisir une finition du référentiel ou taper une autre désignation");
        return new DataGridTemplateColumn
        {
            Header = header,
            CellTemplate = new DataTemplate { VisualTree = f },
            Width = new DataGridLength(width),
            SortMemberPath = textPath,
        };
    }
}

internal abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Ligne sélectionnable d'une liste.</summary>
internal abstract class SelectableRow : Observable
{
    private bool _selected;

    [DisplayName("✓")]
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}

/// <summary>
/// Fenêtre générique « choisir → prévisualiser → exécuter » : liste cochable, options, aperçu des opérations.
/// Aucune écriture n'a lieu tant que l'utilisateur n'a pas cliqué sur le bouton d'action.
/// </summary>
internal sealed class ListWindow<T> where T : SelectableRow
{
    private readonly Window _win;
    private readonly List<T> _rows;
    private readonly TextBox _preview;
    private readonly Func<IReadOnlyList<T>, string>? _previewFn;
    public bool Confirmed { get; private set; }
    public StackPanel Options { get; } = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
    public StackPanel ExtraButtons { get; } = new() { Orientation = Orientation.Horizontal };
    public DataGrid GridControl { get; }

    public ListWindow(string title, string subtitle, IEnumerable<T> rows, string actionLabel, Func<IReadOnlyList<T>, string>? preview)
    {
        _rows = rows.ToList();
        _previewFn = preview;
        _win = Theme.Window(title, 1100, 720);
        var root = new DockPanel { Margin = new Thickness(14) };

        var header = Theme.Header(title, subtitle);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        DockPanel.SetDock(Options, Dock.Top);
        root.Children.Add(Options);

        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        var all = Theme.Button("Tout cocher");
        all.Margin = new Thickness(0);
        all.Click += (_, _) => { foreach (var r in _rows) r.Selected = true; };
        var none = Theme.Button("Tout décocher");
        none.Click += (_, _) => { foreach (var r in _rows) r.Selected = false; };
        var sel = Theme.Button("Cocher la sélection");
        sel.Click += (_, _) => { foreach (var r in GridControl!.SelectedItems.OfType<T>()) r.Selected = true; };
        left.Children.Add(all);
        left.Children.Add(none);
        left.Children.Add(sel);
        left.Children.Add(ExtraButtons);
        buttons.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (preview != null)
        {
            var pv = Theme.Button("Prévisualiser");
            pv.Click += (_, _) => RefreshPreview();
            right.Children.Add(pv);
        }
        var cancel = Theme.Button("Fermer");
        cancel.Click += (_, _) => _win.Close();
        right.Children.Add(cancel);
        if (!string.IsNullOrEmpty(actionLabel))
        {
            var go = Theme.Button(actionLabel, primary: true);
            go.Click += (_, _) =>
            {
                if (!Selection.Any())
                {
                    MessageBox.Show(_win, "Aucune ligne cochée.", "Avion par terre", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                Confirmed = true;
                _win.Close();
            };
            right.Children.Add(go);
        }
        buttons.Children.Add(right);
        root.Children.Add(buttons);

        _preview = new TextBox
        {
            IsReadOnly = true,
            Height = 130,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            Margin = new Thickness(0, 8, 0, 0),
            Background = Theme.B(Theme.GrisClair),
            Text = preview != null ? "Cocher des lignes puis « Prévisualiser » pour voir les opérations prévues (aucune écriture)." : "",
            Visibility = preview != null ? Visibility.Visible : Visibility.Collapsed,
        };
        DockPanel.SetDock(_preview, Dock.Bottom);
        root.Children.Add(_preview);

        GridControl = Theme.Grid(_rows);
        root.Children.Add(GridControl);
        _win.Content = root;
    }

    public IReadOnlyList<T> Selection => _rows.Where(r => r.Selected).ToList();

    public void RefreshPreview()
    {
        if (_previewFn == null) return;
        try { _preview.Text = _previewFn(Selection); }
        catch (Exception ex) { _preview.Text = "Prévisualisation impossible : " + ex.Message; }
    }

    public Window Window => _win;

    public bool ShowDialog()
    {
        _win.ShowDialog();
        return Confirmed;
    }
}

/// <summary>Rapport d'opérations en fin de génération.</summary>
internal static class ReportWindow
{
    public static void Show(string title, string text, string? folder)
    {
        var win = Theme.Window(title, 860, 640);
        var root = new DockPanel { Margin = new Thickness(14) };
        var header = Theme.Header(title, "Rapport d'opérations — une génération réussie n'est pas une validation de conception.");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        if (folder != null && Directory.Exists(folder))
        {
            var open = Theme.Button("Ouvrir le dossier");
            open.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            buttons.Children.Add(open);
        }
        var copy = Theme.Button("Copier");
        copy.Click += (_, _) => Clipboard.SetText(text);
        buttons.Children.Add(copy);
        var close = Theme.Button("Fermer", primary: true);
        close.Click += (_, _) => win.Close();
        buttons.Children.Add(close);
        root.Children.Add(buttons);
        root.Children.Add(new TextBox
        {
            Text = text,
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        win.Content = root;
        win.ShowDialog();
    }
}

internal static class Controls
{
    public static StackPanel Labeled(string label, UIElement control)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Foreground = Theme.B(Theme.Gris), FontWeight = FontWeights.Bold });
        sp.Children.Add(control);
        return sp;
    }

    public static ComboBox Combo<TItem>(IEnumerable<TItem> items, Func<TItem, string> display, int selected = 0, double width = 320)
    {
        var list = items.ToList();
        var cb = new ComboBox { Width = width, DisplayMemberPath = "Item1", SelectedValuePath = "Item2" };
        cb.ItemsSource = list.Select(i => Tuple.Create(display(i), (object?)i)).ToList();
        if (list.Count > 0) cb.SelectedIndex = Math.Min(selected, list.Count - 1);
        return cb;
    }

    public static TItem? Value<TItem>(ComboBox cb) => cb.SelectedValue is TItem t ? t : default;
}
