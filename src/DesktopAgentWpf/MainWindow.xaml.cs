using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Runtime.InteropServices;
using WpfClipboard = System.Windows.Clipboard;
using System.Windows.Media.Imaging;
using QRCoder;
using DesktopAgentWpf.Services;
using NAudio.CoreAudioApi;

namespace DesktopAgentWpf;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private ServerHost? _server;
    private const int HotkeyId = 9000;
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private HwndSource? _hwndSource;
    private bool _hotkeyRegistered;

    public MainWindow()
    {
        InitializeComponent();
        ShowInTaskbar = false;
        DataContext = _vm;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoadOutputDevices();
        LoadOutputDevices();
        LoadInputDevices();
        _server = new ServerHost();
        _server.Log += OnLog;
        _server.StatusChanged += status => Dispatcher.Invoke(() => _vm.ServerStatus = status);
        _server.RagProgress += (percent, message) =>
            Dispatcher.Invoke(() =>
            {
                _vm.RagVisible = true;
                _vm.RagProgress = percent;
                _vm.RagStatus = message;
            });
        _server.RagStatus += message =>
            Dispatcher.Invoke(() =>
            {
                _vm.RagVisible = true;
                _vm.RagStatus = message;
            });

        try
        {
            _vm.ServerStatus = "Starting...";
            await _server.StartAsync();
            _vm.ServerStatus = "Running";
            _vm.ServerUrl = _server.Url;
            _vm.QrImage = CreateQrCode(_server.Url);
            OnLog($"Server started at {_server.Url}");
            _vm.RagVisible = _server.IsRagEnabled;
            if (_server.IsRagEnabled)
            {
                _vm.RagStatus = "RAG: starting...";
                _vm.RagProgress = 0;
            }
        }
        catch (Exception ex)
        {
            _vm.ServerStatus = "Error";
            OnLog($"Failed to start server: {ex.Message}");
        }

        RegisterHotkey();

        if (_server != null)
        {
            _server.SetOutputDevice(_vm.SelectedOutputDeviceId);
            _server.SetInputDevice(_vm.SelectedInputDeviceId);
        }
    }

    private void OutputDevice_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_server == null)
        {
            return;
        }

        _server.SetOutputDevice(_vm.SelectedOutputDeviceId);
    }

    private void InputDevice_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_server == null)
        {
            return;
        }

        _server.SetInputDevice(_vm.SelectedInputDeviceId);
    }

    private async void TestDevices_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null)
        {
            return;
        }

        try
        {
            OnLog("Testing devices for 2 seconds...");
            var result = await _server.ProbeAsync(TimeSpan.FromSeconds(2));
            OnLog($"Mic level: {FormatLevel(result.MicPeak)} | System level: {FormatLevel(result.SystemPeak)}");
        }
        catch (Exception ex)
        {
            OnLog($"Device test failed: {ex.Message}");
        }
    }

    private void LoadOutputDevices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            _vm.OutputDevices.Clear();
            foreach (var device in devices)
            {
                _vm.OutputDevices.Add(new OutputDeviceInfo(device.ID, device.FriendlyName));
            }

            var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (defaultDevice != null && _vm.OutputDevices.Count > 0)
            {
                var match = _vm.OutputDevices.FirstOrDefault(d => d.Id == defaultDevice.ID);
                _vm.SelectedOutputDeviceId = match?.Id ?? _vm.OutputDevices[0].Id;
            }
        }
        catch (Exception ex)
        {
            OnLog($"Failed to load output devices: {ex.Message}");
        }
    }

    private void LoadInputDevices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            _vm.InputDevices.Clear();
            foreach (var device in devices)
            {
                _vm.InputDevices.Add(new OutputDeviceInfo(device.ID, device.FriendlyName));
            }

            MMDevice? defaultDevice = null;
            try
            {
                defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            }
            catch
            {
                // ignore
            }

            if (defaultDevice == null)
            {
                try
                {
                    defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                }
                catch
                {
                    // ignore
                }
            }

            if (defaultDevice != null && _vm.InputDevices.Count > 0)
            {
                var match = _vm.InputDevices.FirstOrDefault(d => d.Id == defaultDevice.ID);
                _vm.SelectedInputDeviceId = match?.Id ?? _vm.InputDevices[0].Id;
            }
        }
        catch (Exception ex)
        {
            OnLog($"Failed to load input devices: {ex.Message}");
        }
    }

    private static string FormatLevel(float peak)
    {
        if (peak <= 0.0001f)
        {
            return "silence";
        }

        var db = 20 * Math.Log10(peak);
        return $"{db:F1} dB";
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        UnregisterHotkey();
        if (_server != null)
        {
            await _server.StopAsync();
            _server.Dispose();
        }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            ShowInTaskbar = false;
        }
    }

    private void RegisterHotkey()
    {
        var helper = new WindowInteropHelper(this);
        var handle = helper.Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _hwndSource = HwndSource.FromHwnd(handle);
        _hwndSource?.AddHook(WndProc);

        var vk = (uint)KeyInterop.VirtualKeyFromKey(System.Windows.Input.Key.S);
        _hotkeyRegistered = RegisterHotKey(handle, HotkeyId, ModControl, vk);
        if (!_hotkeyRegistered)
        {
            OnLog("Hotkey Ctrl+S is already in use. Close other apps or pick a different hotkey.");
        }
    }

    private void UnregisterHotkey()
    {
        try
        {
            if (_hotkeyRegistered)
            {
                var handle = new WindowInteropHelper(this).Handle;
                UnregisterHotKey(handle, HotkeyId);
            }
        }
        catch
        {
        }

        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        _hotkeyRegistered = false;
    }

    private void RestoreFromHotkey()
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            RestoreFromHotkey();
            handled = true;
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private void OnLog(string message)
    {
        Dispatcher.Invoke(() =>
        {
            var line = $"{DateTime.Now:HH:mm:ss} {message}";
            _vm.Logs.Add(line);
            while (_vm.Logs.Count > 200)
            {
                _vm.Logs.RemoveAt(0);
            }
        });
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_vm.ServerUrl))
        {
            WpfClipboard.SetText(_vm.ServerUrl);
            OnLog("URL copied to clipboard");
        }
    }

    private static BitmapImage CreateQrCode(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        var qrCode = new PngByteQRCode(data);
        var bytes = qrCode.GetGraphic(20);

        var image = new BitmapImage();
        using var mem = new MemoryStream(bytes);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = mem;
        image.EndInit();
        image.Freeze();
        return image;
    }
}

public sealed record OutputDeviceInfo(string Id, string Name);

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _serverStatus = "";
    private string _serverUrl = "";
    private BitmapImage? _qrImage;
    private string? _selectedOutputDeviceId;
    private string? _selectedInputDeviceId;
    private string _ragStatus = "";
    private int _ragProgress;
    private bool _ragVisible;

    public string ServerStatus
    {
        get => _serverStatus;
        set
        {
            if (_serverStatus != value)
            {
                _serverStatus = value;
                OnPropertyChanged(nameof(ServerStatus));
            }
        }
    }

    public string ServerUrl
    {
        get => _serverUrl;
        set
        {
            if (_serverUrl != value)
            {
                _serverUrl = value;
                OnPropertyChanged(nameof(ServerUrl));
            }
        }
    }

    public BitmapImage? QrImage
    {
        get => _qrImage;
        set
        {
            if (_qrImage != value)
            {
                _qrImage = value;
                OnPropertyChanged(nameof(QrImage));
            }
        }
    }

    public ObservableCollection<OutputDeviceInfo> OutputDevices { get; } = new();

    public string? SelectedOutputDeviceId
    {
        get => _selectedOutputDeviceId;
        set
        {
            if (_selectedOutputDeviceId != value)
            {
                _selectedOutputDeviceId = value;
                OnPropertyChanged(nameof(SelectedOutputDeviceId));
            }
        }
    }

    public ObservableCollection<OutputDeviceInfo> InputDevices { get; } = new();

    public string? SelectedInputDeviceId
    {
        get => _selectedInputDeviceId;
        set
        {
            if (_selectedInputDeviceId != value)
            {
                _selectedInputDeviceId = value;
                OnPropertyChanged(nameof(SelectedInputDeviceId));
            }
        }
    }

    public ObservableCollection<string> Logs { get; } = new();

    public string RagStatus
    {
        get => _ragStatus;
        set
        {
            if (_ragStatus != value)
            {
                _ragStatus = value;
                OnPropertyChanged(nameof(RagStatus));
            }
        }
    }

    public int RagProgress
    {
        get => _ragProgress;
        set
        {
            if (_ragProgress != value)
            {
                _ragProgress = value;
                OnPropertyChanged(nameof(RagProgress));
            }
        }
    }

    public bool RagVisible
    {
        get => _ragVisible;
        set
        {
            if (_ragVisible != value)
            {
                _ragVisible = value;
                OnPropertyChanged(nameof(RagVisible));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
