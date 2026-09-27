using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AvionParTerre.Revit.UI;

/// <summary>
/// Fenêtre d'attente du mode avion par terre : l'avion animé (tools/icon-anim, 60 images à 12 i/s), l'étape en cours et le journal.
/// Deux usages : <see cref="RunAsync{T}"/> pour un travail hors Revit (appels OpenRouter), <see cref="Show"/> + <see cref="Step"/>
/// pour suivre un travail Revit sur le fil principal.
/// </summary>
internal sealed class ProgressWindow
{
    private readonly Window _win;
    private readonly Image _image = new() { Width = 96, Height = 96, Margin = new Thickness(0, 0, 18, 0) };
    private readonly TextBlock _status = new() { FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Theme.B(Theme.Gris), TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _log = new()
    {
        IsReadOnly = true, Height = 170, Margin = new Thickness(0, 12, 0, 0), FontFamily = new FontFamily("Consolas"), FontSize = 11,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap, Background = Theme.B(Theme.GrisClair),
    };
    private readonly Button _cancel = Theme.Button("Annuler");
    private readonly DispatcherTimer _timer;
    private readonly CroppedBitmap[] _frames;
    private int _frame;
    private CancellationTokenSource? _cts;
    private bool _allowClose;

    public ProgressWindow(string title)
    {
        _win = Theme.Window(title, 620, 380);
        _win.ResizeMode = ResizeMode.NoResize;
        var sprite = Theme.Png("avion_sprite.png");
        _frames = new CroppedBitmap[sprite == null ? 0 : sprite.PixelWidth / 64];
        for (int i = 0; i < _frames.Length; i++) _frames[i] = new CroppedBitmap(sprite, new Int32Rect(i * 64, 0, 64, 64));
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        if (_frames.Length > 0) _image.Source = _frames[0];
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(1000.0 / 12), DispatcherPriority.Render, (_, _) =>
        {
            if (_frames.Length == 0) return;
            _frame = (_frame + 1) % _frames.Length;
            _image.Source = _frames[_frame];
        }, _win.Dispatcher);

        var root = new DockPanel { Margin = new Thickness(16) };
        var header = Theme.Header(title, "Mode avion par terre — l'IA décide de ce qui n'a été décidé ni par vous ni dans la maquette.");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        _cancel.Click += (_, _) => { _cts?.Cancel(); _cancel.IsEnabled = false; _status.Text = "Annulation demandée…"; };
        buttons.Children.Add(_cancel);
        root.Children.Add(buttons);
        var body = new DockPanel();
        DockPanel.SetDock(_image, Dock.Left);
        body.Children.Add(_image);
        var right = new StackPanel();
        right.Children.Add(_status);
        right.Children.Add(_log);
        body.Children.Add(right);
        root.Children.Add(body);
        _win.Content = root;
        _win.Closing += (_, e) => { if (!_allowClose) { e.Cancel = true; _cts?.Cancel(); } };
    }

    public void Log(string line)
    {
        _status.Text = line;
        _log.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        _log.ScrollToEnd();
    }

    /// <summary>Exécute un travail asynchrone (hors API Revit) en affichant la fenêtre ; renvoie son résultat ou relance son exception.</summary>
    public T RunAsync<T>(Func<IProgress<string>, CancellationToken, Task<T>> work)
    {
        _cts = new CancellationTokenSource();
        T result = default!;
        Exception? error = null;
        var progress = new Progress<string>(Log);
        _win.Loaded += async (_, _) =>
        {
            _timer.Start();
            try { result = await Task.Run(() => work(progress, _cts.Token)); }
            catch (Exception ex) { error = ex; }
            finally
            {
                _timer.Stop();
                _allowClose = true;
                _win.Close();
            }
        };
        _win.ShowDialog();
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }

    // ------------------------------------------------------------------ suivi d'un travail Revit (fil principal)

    public void Show()
    {
        _cancel.Content = "Arrêter après l'étape";
        _cts = new CancellationTokenSource();
        _win.Show();
        _timer.Start();
        Pump();
    }

    public bool Cancelled => _cts?.IsCancellationRequested == true;

    /// <summary>Affiche une étape et laisse l'interface se rafraîchir (animation, journal) entre deux opérations Revit.</summary>
    public void Step(string line)
    {
        Log(line);
        Pump();
    }

    public void Pump()
    {
        for (int i = 0; i < 3; i++)
            _win.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
    }

    public Window Window => _win;

    public void Close()
    {
        _timer.Stop();
        _allowClose = true;
        _win.Close();
    }
}
