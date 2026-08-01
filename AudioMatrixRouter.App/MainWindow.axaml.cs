using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AudioMatrixRouter.Audio;
using NAudio.CoreAudioApi;

namespace AudioMatrixRouter.App;

public partial class MainWindow : Window
{
    private readonly DeviceEnumerator _enumerator = new();

    public MainWindow()
    {
        InitializeComponent();

        // Drag + double-click-maximize come from the TitleBar element role (see XAML);
        // only the caption buttons need wiring.
        MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaxButton.Click += (_, _) => ToggleMaximize();
        CloseButton.Click += (_, _) => Close();

        VersionText.Text = "v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

        Loaded += OnLoaded;
        Closed += (_, _) => _enumerator.Dispose();
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Core-linkage proof: enumerate real WASAPI endpoints through the shared engine code.
        var inputs = _enumerator.GetDevices(DataFlow.Capture);
        var outputs = _enumerator.GetDevices(DataFlow.Render);

        InputList.ItemsSource = inputs.Select(d => DeviceRow(d.Name, d.Channels, d.SampleRate)).ToList();
        OutputList.ItemsSource = outputs.Select(d => DeviceRow(d.Name, d.Channels, d.SampleRate)).ToList();

        StatusText.Text = $"Standby · {inputs.Count} inputs · {outputs.Count} outputs";
    }

    private static Control DeviceRow(string name, int channels, int sampleRate) =>
        new Border
        {
            Margin = new Avalonia.Thickness(0, 0, 0, 6),
            Padding = new Avalonia.Thickness(10, 8),
            CornerRadius = new Avalonia.CornerRadius(5),
            BorderThickness = new Avalonia.Thickness(1),
            BorderBrush = new SolidColorBrush(Color.Parse("#39404D")),
            Background = new SolidColorBrush(Color.Parse("#1D2127")),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = name,
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.Parse("#C6CDD8")),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    new TextBlock
                    {
                        Text = $"{channels}ch · {sampleRate / 1000.0:0.#}kHz",
                        FontSize = 10,
                        Foreground = new SolidColorBrush(Color.Parse("#9AA4B2"))
                    }
                }
            }
        };
}
