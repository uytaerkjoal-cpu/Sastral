using Microsoft.Win32;
using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Sastral
{
    public partial class MainWindow : Window
    {
        public class AppSettings
        {
            public bool TopMost { get; set; } = false;
            public bool FastLoading { get; set; } = false;
            public bool AutoAttach { get; set; } = false;
            public string LastActiveTab { get; set; } = "Home";

            public string Nickname { get; set; } = "";
            public string AvatarPath { get; set; } = "";
            public double AvatarOffsetX { get; set; } = 0;
            public double AvatarOffsetY { get; set; } = 0;
        }

        private DispatcherTimer _uiTimer;
        private DispatcherTimer _popupCloseTimer;
        private bool _isEditorLoaded = false;
        private bool _isLoading = true;
        private bool _isTerminalOpen = false;

        private AppSettings _settings = new AppSettings();
        private string _settingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

        private Point _avatarDragStart;
        private double _avatarStartOffsetX;
        private double _avatarStartOffsetY;
        private bool _isDraggingAvatar = false;

        private bool _isTerminalWebViewInitialized = false;
        private readonly System.Text.StringBuilder _pendingLogs = new System.Text.StringBuilder();

        public MainWindow()
        {
            InitializeComponent();
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsFile))
                {
                    string json = File.ReadAllText(_settingsFile);
                    _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(_settings.Nickname))
                _settings.Nickname = Environment.UserName;

            ToggleTopMost.IsChecked = _settings.TopMost;

            // Откладываем применение Topmost, чтобы ОС применила его после полной отрисовки окна
            Dispatcher.BeginInvoke(new Action(() =>
            {
                this.Topmost = _settings.TopMost;
            }), DispatcherPriority.ApplicationIdle);

            ToggleFastLoading.IsChecked = _settings.FastLoading;
            ToggleAutoAttach.IsChecked = _settings.AutoAttach;

            ProfileNickname.Text = _settings.Nickname;
            ProfileBrushTranslate.X = _settings.AvatarOffsetX;
            ProfileBrushTranslate.Y = _settings.AvatarOffsetY;
            LoadAvatarImage(_settings.AvatarPath);

            if (_settings.AutoAttach)
            {
                try { API.AutoAttach(); } catch { }
            }
        }

        private void SaveSettings()
        {
            try
            {
                string json = JsonSerializer.Serialize(_settings);
                File.WriteAllText(_settingsFile, json);
            }
            catch { }
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Mouse.OverrideCursor = Cursors.Arrow;
            LoadSettings();

            InitializeMonitoring();
            InitializePopupTimer();
            SetupHomeInfo();
            UpdateWeatherAsync();

            API.Roblox.Log += OnRobloxLog;

            if (_settings.FastLoading)
            {
                Electron1.Visibility = Visibility.Collapsed;
                Electron2.Visibility = Visibility.Collapsed;
                Electron3.Visibility = Visibility.Collapsed;
            }
            else
            {
                Storyboard loadingAnim = (Storyboard)LoadingScreen.Resources["LoadingAnimation"];
                loadingAnim?.Begin(LoadingScreen);
            }

            await Task.Delay(100);
            _ = InitializeWebViewsAsync();

            if (_settings.FastLoading)
            {
                while (!_isEditorLoaded) await Task.Delay(50);
                Mouse.OverrideCursor = null;
                RestoreLastTab();
            }
            else
            {
                await Task.Delay(10000);
                while (!_isEditorLoaded) await Task.Delay(50);

                Mouse.OverrideCursor = null;

                DoubleAnimation fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(2));
                LoadingScreen.BeginAnimation(UIElement.OpacityProperty, fadeOut);

                await Task.Delay(2000);
                LoadingScreen.BeginAnimation(UIElement.OpacityProperty, null);
                RestoreLastTab();
            }
        }

        private void OnRobloxLog(string logText)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<string>(OnRobloxLog), logText);
                return;
            }

            AppendColoredLog(logText);
        }

        private async void AppendColoredLog(string text)
        {
            if (!_isTerminalWebViewInitialized)
            {
                lock (_pendingLogs)
                {
                    _pendingLogs.AppendLine(text);
                }
                return;
            }

            if (TerminalWebView == null || TerminalWebView.CoreWebView2 == null) return;

            try
            {
                string escapedText = JsonSerializer.Serialize(text);
                await TerminalWebView.CoreWebView2.ExecuteScriptAsync($"window.appendLog?.({escapedText});");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
        }

        private void btnToggleTerminal_Click(object sender, RoutedEventArgs e)
        {
            if (_settings.LastActiveTab != "Editor") return;

            _isTerminalOpen = !_isTerminalOpen;

            double targetHeight = _isTerminalOpen ? 140 : 0;

            if (_isTerminalOpen)
            {
                TerminalContainer.Visibility = Visibility.Visible;
            }

            DoubleAnimation heightAnimation = new DoubleAnimation
            {
                To = targetHeight,
                Duration = TimeSpan.FromSeconds(0.25),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };

            if (!_isTerminalOpen)
            {
                heightAnimation.Completed += (s, args) =>
                {
                    if (!_isTerminalOpen)
                    {
                        TerminalContainer.Visibility = Visibility.Collapsed;
                    }
                };
            }

            TerminalContainer.BeginAnimation(FrameworkElement.HeightProperty, heightAnimation);
        }

        private void UpdateTerminalVisibility(bool activeTabIsEditor)
        {
            if (TerminalContainer == null || btnToggleTerminal == null) return;

            if (activeTabIsEditor)
            {
                btnToggleTerminal.Visibility = Visibility.Visible;

                if (_isTerminalOpen)
                {
                    TerminalContainer.Visibility = Visibility.Visible;
                    TerminalContainer.Height = 140;
                }
            }
            else
            {
                btnToggleTerminal.Visibility = Visibility.Collapsed;

                TerminalContainer.Visibility = Visibility.Collapsed;
                TerminalContainer.Height = 0;
            }
        }

        private void LoadAvatarImage(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    BitmapImage bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(path);
                    bmp.EndInit();
                    ProfileBrush.ImageSource = bmp;
                }
                catch { }
            }
        }

        private void ChangeAvatar()
        {
            OpenFileDialog ofd = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.gif", Title = "Выберите изображение профиля" };
            if (ofd.ShowDialog() == true)
            {
                try
                {
                    string otherDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Bin", "Other");
                    if (!Directory.Exists(otherDir)) Directory.CreateDirectory(otherDir);

                    string ext = Path.GetExtension(ofd.FileName);
                    string destFile = Path.Combine(otherDir, "profile" + ext);

                    File.Copy(ofd.FileName, destFile, true);

                    _settings.AvatarPath = destFile;
                    _settings.AvatarOffsetX = 0;
                    _settings.AvatarOffsetY = 0;
                    ProfileBrushTranslate.X = 0;
                    ProfileBrushTranslate.Y = 0;
                    SaveSettings();

                    LoadAvatarImage(destFile);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
            }
        }

        private void ProfileAvatar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ChangeAvatar();
                return;
            }

            _isDraggingAvatar = true;
            _avatarDragStart = e.GetPosition(ProfileAvatar);
            _avatarStartOffsetX = ProfileBrushTranslate.X;
            _avatarStartOffsetY = ProfileBrushTranslate.Y;
            ProfileAvatar.CaptureMouse();
        }

        private void ProfileAvatar_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingAvatar)
            {
                Point current = e.GetPosition(ProfileAvatar);
                double dx = current.X - _avatarDragStart.X;
                double dy = current.Y - _avatarDragStart.Y;

                ProfileBrushTranslate.X = _avatarStartOffsetX + (dx / ProfileAvatar.ActualWidth);
                ProfileBrushTranslate.Y = _avatarStartOffsetY + (dy / ProfileAvatar.ActualHeight);
            }
        }

        private void ProfileAvatar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingAvatar)
            {
                _isDraggingAvatar = false;
                ProfileAvatar.ReleaseMouseCapture();
                _settings.AvatarOffsetX = ProfileBrushTranslate.X;
                _settings.AvatarOffsetY = ProfileBrushTranslate.Y;
                SaveSettings();
            }
        }

        private void ProfileNickname_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isLoading)
            {
                _settings.Nickname = ProfileNickname.Text;
                WelcomeText.Text = $"Welcome back, {_settings.Nickname}!";
                SaveSettings();
            }
        }

        private void SetupHomeInfo()
        {
            WelcomeText.Text = $"Welcome back, {_settings.Nickname}!";

            string[] quotes = {
                "Is it a beautiful day today, or what?",
                "Use Sastral!",
                "helloworld\"(print)\".",
                "Join the discord channel!",
                "Ready when you're ready.",
                "Glad to see you.",
                "Press start, regret later.",
                "Coffee loaded. Brain not found.",
                "No thoughts. Only functions.",
                "Error 404: motivation missing.",
                "Launching questionable decisions...",
                "Trust the process. Fear the outcome.",
                "Everything is fine. I guess.",
                "Please don't press random buttons. Or press them.",
            };
            QuoteText.Text = quotes[new Random().Next(quotes.Length)];
        }

        private async void UpdateWeatherAsync()
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
                System.Net.ServicePointManager.Expect100Continue = true;

                using (var handler = new HttpClientHandler())
                {
                    handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;
                    handler.AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate;

                    using (HttpClient client = new HttpClient(handler))
                    {
                        client.Timeout = TimeSpan.FromSeconds(10);
                        client.DefaultRequestHeaders.Add("User-Agent", "curl/7.88.1");

                        HttpResponseMessage response = await client.GetAsync("http://wttr.in/?format=%C+%t&");

                        if (response.IsSuccessStatusCode)
                        {
                            string weather = await response.Content.ReadAsStringAsync();

                            if (!weather.Contains("<html") && !string.IsNullOrWhiteSpace(weather))
                            {
                                WeatherText.Text = weather.Trim();
                            }
                            else
                            {
                                WeatherText.Text = "Error Format";
                            }
                        }
                        else
                        {
                            WeatherText.Text = $"HTTP {(int)response.StatusCode}";
                        }
                    }
                }
            }
            catch (TaskCanceledException)
            {
                WeatherText.Text = "Time-out";
            }
            catch (Exception)
            {
                WeatherText.Text = "Error Request";
            }
        }

        private async Task InitializeWebViewsAsync()
        {
            try
            {
                var options = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions("--disable-web-security");
                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, null, options);

                await Editor.EnsureCoreWebView2Async(env);
                await Script.EnsureCoreWebView2Async(env);
                await TerminalWebView.EnsureCoreWebView2Async(env);

                Editor.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
                Script.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
                TerminalWebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

                Script.WebMessageReceived += Script_WebMessageReceived;

                Editor.NavigationCompleted += (s, e) => _isEditorLoaded = true;

                TerminalWebView.NavigationCompleted += async (s, e) =>
                {
                    _isTerminalWebViewInitialized = true;
                    string logsToFlush;
                    lock (_pendingLogs)
                    {
                        logsToFlush = _pendingLogs.ToString();
                        _pendingLogs.Clear();
                    }
                    if (!string.IsNullOrEmpty(logsToFlush))
                    {
                        string escapedText = JsonSerializer.Serialize(logsToFlush.TrimEnd());
                        await TerminalWebView.CoreWebView2.ExecuteScriptAsync($"window.appendLog?.({escapedText});");
                    }
                };

                string editorPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Bin", "Html", "Editor.html");
                string scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Bin", "Html", "Script.html");
                string terminalPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Bin", "Html", "Terminal.html");

                if (File.Exists(editorPath)) Editor.CoreWebView2.Navigate(new Uri(editorPath).AbsoluteUri);
                if (File.Exists(scriptPath)) Script.CoreWebView2.Navigate(new Uri(scriptPath).AbsoluteUri);
                if (File.Exists(terminalPath)) TerminalWebView.CoreWebView2.Navigate(new Uri(terminalPath).AbsoluteUri);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
        }

        private void RestoreLastTab()
        {
            _isLoading = false;
            switch (_settings.LastActiveTab)
            {
                case "Editor": SwitchToEditor(); break;
                case "Scripts": SwitchToScripts(); break;
                case "Settings": SwitchToSettings(); break;
                case "Home":
                default: SwitchToHome(); break;
            }

            // Повторно форсируем Z-order после того, как все вкладки восстановили свое состояние и прошел загрузочный экран
            this.Topmost = _settings.TopMost;
        }

        private void HideAllContainers()
        {
            if (!_isLoading && LoadingScreen != null)
            {
                LoadingScreen.Visibility = Visibility.Collapsed;
                LoadingScreen.Opacity = 0;
            }
            if (HomeContainer != null) { HomeContainer.Visibility = Visibility.Collapsed; HomeContainer.Opacity = 0; }
            if (EditorContainer != null) { EditorContainer.Visibility = Visibility.Collapsed; EditorContainer.Opacity = 0; }
            if (ScriptContainer != null) { ScriptContainer.Visibility = Visibility.Collapsed; ScriptContainer.Opacity = 0; }
            if (SettingsContainer != null) { SettingsContainer.Visibility = Visibility.Collapsed; SettingsContainer.Opacity = 0; }
        }

        private void SwitchToHome()
        {
            HideAllContainers();
            UpdateTerminalVisibility(false);
            if (HomeContainer != null)
            {
                HomeContainer.Visibility = Visibility.Visible;
                HomeContainer.Opacity = 1;
            }
            if (!_isLoading) { _settings.LastActiveTab = "Home"; SaveSettings(); }
        }

        private void SwitchToEditor()
        {
            HideAllContainers();
            UpdateTerminalVisibility(true);
            if (EditorContainer != null)
            {
                EditorContainer.Visibility = Visibility.Visible;
                EditorContainer.Opacity = 1;
            }
            if (!_isLoading) { _settings.LastActiveTab = "Editor"; SaveSettings(); }
        }

        private void SwitchToScripts()
        {
            HideAllContainers();
            UpdateTerminalVisibility(false);
            if (ScriptContainer != null)
            {
                ScriptContainer.Visibility = Visibility.Visible;
                ScriptContainer.Opacity = 1;
            }
            if (!_isLoading) { _settings.LastActiveTab = "Scripts"; SaveSettings(); }
        }

        private void SwitchToSettings()
        {
            HideAllContainers();
            UpdateTerminalVisibility(false);
            if (SettingsContainer != null)
            {
                SettingsContainer.Visibility = Visibility.Visible;
                SettingsContainer.Opacity = 1;
            }
            if (!_isLoading) { _settings.LastActiveTab = "Settings"; SaveSettings(); }
        }

        private async void Script_WebMessageReceived(object sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string rawJson = e.WebMessageAsJson;
                using (JsonDocument doc = JsonDocument.Parse(rawJson))
                {
                    JsonElement root = doc.RootElement;
                    string action = root.GetProperty("action").GetString();
                    string content = root.GetProperty("content").GetString();

                    if (action == "execute")
                    {
                        if (!string.IsNullOrWhiteSpace(content)) API.Execute(content);
                    }
                    else if (action == "send")
                    {
                        string title = root.GetProperty("title").GetString();
                        string jsTitle = JsonSerializer.Serialize(title);
                        string jsContent = JsonSerializer.Serialize(content);
                        await Editor.ExecuteScriptAsync($"addTab({jsTitle}, {jsContent})");
                        SwitchToEditor();
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }

        private void InitializeMonitoring()
        {
            _uiTimer = new DispatcherTimer();
            _uiTimer.Interval = TimeSpan.FromMilliseconds(500);
            _uiTimer.Tick += UI_Timer_Tick;
            _uiTimer.Start();
        }

        private void UI_Timer_Tick(object sender, EventArgs e)
        {
            if (ClockText != null && DateText != null)
            {
                ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
                DateText.Text = DateTime.Now.ToString("dddd, MMMM dd, yyyy", new CultureInfo("en-US"));
            }

            if (API.Roblox.Run)
            {
                RobloxStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#47F47A"));
                RobloxStatusText.Text = "Roblox: Active";
            }
            else
            {
                RobloxStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4747"));
                RobloxStatusText.Text = "Roblox: Closed";
            }

            if (API.Roblox.Attached)
            {
                AttachStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#47F47A"));
                AttachStatusText.Text = "Attached";
            }
            else
            {
                AttachStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4747"));
                AttachStatusText.Text = "Not Attached";
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.H) { SwitchToHome(); e.Handled = true; }
                else if (e.Key == Key.E) { SwitchToEditor(); e.Handled = true; }
                else if (e.Key == Key.S) { SwitchToScripts(); e.Handled = true; }
            }
        }

        private async void btnExecute_Click(object sender, RoutedEventArgs e)
        {
            if (Editor.CoreWebView2 != null)
            {
                string code = await Editor.CoreWebView2.ExecuteScriptAsync("window.editor ? window.editor.getValue() : '';");
                if (!string.IsNullOrWhiteSpace(code) && code != "null")
                {
                    code = System.Text.RegularExpressions.Regex.Unescape(code.Trim('"'));
                    API.Execute(code);
                }
            }
        }

        private async void btnOpen_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog { Filter = "Script Files (*.txt;*.lua)|*.txt;*.lua|All Files (*.*)|*.*", Title = "Open Script" };
            if (ofd.ShowDialog() == true)
            {
                try
                {
                    string content = File.ReadAllText(ofd.FileName);
                    if (Editor.CoreWebView2 != null)
                    {
                        string safeContent = JsonSerializer.Serialize(content);
                        await Editor.CoreWebView2.ExecuteScriptAsync($"if(window.editor) window.editor.setValue({safeContent});");
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
            }
        }

        private async void btnSave_Click(object sender, RoutedEventArgs e)
        {
            if (Editor.CoreWebView2 != null)
            {
                string code = await Editor.CoreWebView2.ExecuteScriptAsync("window.editor ? window.editor.getValue() : '';");
                if (!string.IsNullOrWhiteSpace(code) && code != "null")
                {
                    code = System.Text.RegularExpressions.Regex.Unescape(code.Trim('"'));
                    SaveFileDialog sfd = new SaveFileDialog { Filter = "Lua Script (*.lua)|*.lua", Title = "Save Script" };
                    if (sfd.ShowDialog() == true)
                    {
                        try { File.WriteAllText(sfd.FileName, code); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
                    }
                }
            }
        }

        private async void btnAuto_Click(object sender, RoutedEventArgs e)
        {
            if (Editor.CoreWebView2 != null)
            {
                string code = await Editor.CoreWebView2.ExecuteScriptAsync("window.editor ? window.editor.getValue() : '';");
                if (!string.IsNullOrWhiteSpace(code) && code != "null")
                {
                    code = System.Text.RegularExpressions.Regex.Unescape(code.Trim('"'));
                    try
                    {
                        string targetDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Bin", "Velocity", "AutoExec");
                        if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                        string jsGetTabName = @"
                            (function() {
                                let activeTab = document.querySelector('.tab.active, .active-tab, .monaco-tab.active, li.active');
                                if (activeTab) return activeTab.innerText.trim();
                                return 'Script';
                            })();
                        ";
                        string res = await Editor.CoreWebView2.ExecuteScriptAsync(jsGetTabName);
                        string baseName = "Script";

                        if (!string.IsNullOrWhiteSpace(res) && res != "null")
                            baseName = System.Text.RegularExpressions.Regex.Unescape(res.Trim('"'));

                        foreach (char c in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c.ToString(), "");
                        if (baseName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) baseName = baseName.Substring(0, baseName.Length - 4);
                        if (string.IsNullOrWhiteSpace(baseName)) baseName = "Script";

                        string targetFile = Path.Combine(targetDir, $"{baseName}.lua");
                        int counter = 2;
                        while (File.Exists(targetFile))
                        {
                            targetFile = Path.Combine(targetDir, $"{baseName} ({counter}).lua");
                            counter++;
                        }
                        File.WriteAllText(targetFile, code);
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
                }
            }
        }

        private async void btnClear_Click(object sender, RoutedEventArgs e)
        {
            if (Editor.CoreWebView2 != null) await Editor.CoreWebView2.ExecuteScriptAsync("if(window.editor) window.editor.setValue('');");
        }

        private async void btnAttach_Click(object sender, RoutedEventArgs e) { await API.Attach(); }

        private void btnKillRoblox_Click(object sender, RoutedEventArgs e)
        {
            try { foreach (var process in System.Diagnostics.Process.GetProcessesByName("RobloxPlayerBeta")) { try { process.Kill(); } catch { } } }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) this.DragMove(); }
        private void btnMinimize_Click(object sender, RoutedEventArgs e) { this.WindowState = WindowState.Minimized; }
        private void btnMaximize_Click(object sender, RoutedEventArgs e) { this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }
        private void btnClose_Click(object sender, RoutedEventArgs e) { this.Close(); }

        private void btnLogo_Click(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "https://discord.gg/qF43JNunM9", UseShellExecute = true }); }
            catch { }
        }

        private void InitializePopupTimer()
        {
            _popupCloseTimer = new DispatcherTimer();
            _popupCloseTimer.Interval = TimeSpan.FromMilliseconds(350);
            _popupCloseTimer.Tick += PopupCloseTimer_Tick;
        }

        private void PopupCloseTimer_Tick(object sender, EventArgs e)
        {
            _popupCloseTimer.Stop();
            if (MenuTriggerButton != null && SidePanelPopup != null)
            {
                if (!MenuTriggerButton.IsMouseOver && !SidePanelPopup.IsMouseOver) SidePanelPopup.IsOpen = false;
            }
        }

        private void MenuTriggerButton_MouseEnter(object sender, MouseEventArgs e) { _popupCloseTimer.Stop(); SidePanelPopup.IsOpen = true; }
        private void MenuTriggerButton_MouseLeave(object sender, MouseEventArgs e) { _popupCloseTimer.Start(); }
        private void MenuTriggerButton_Click(object sender, RoutedEventArgs e) { SidePanelPopup.IsOpen = true; }
        private void SidePanelPopup_MouseEnter(object sender, MouseEventArgs e) { _popupCloseTimer.Stop(); }
        private void SidePanelPopup_MouseLeave(object sender, MouseEventArgs e) { _popupCloseTimer.Start(); }

        private void btnSelectHome_Click(object sender, RoutedEventArgs e) { SwitchToHome(); SidePanelPopup.IsOpen = false; }
        private void btnSelectEditor_Click(object sender, RoutedEventArgs e) { SwitchToEditor(); SidePanelPopup.IsOpen = false; }
        private void btnSelectScript_Click(object sender, RoutedEventArgs e) { SwitchToScripts(); SidePanelPopup.IsOpen = false; }
        private void btnSelectSettings_Click(object sender, RoutedEventArgs e) { SwitchToSettings(); SidePanelPopup.IsOpen = false; }

        private void ToggleTopMost_Click(object sender, RoutedEventArgs e) { _settings.TopMost = ToggleTopMost.IsChecked ?? false; this.Topmost = _settings.TopMost; SaveSettings(); }
        private void ToggleAutoAttach_Click(object sender, RoutedEventArgs e)
        {
            _settings.AutoAttach = ToggleAutoAttach.IsChecked ?? false;
            if (_settings.AutoAttach) try { API.AutoAttach(); } catch { }
            SaveSettings();
        }
        private void ToggleFastLoading_Click(object sender, RoutedEventArgs e) { _settings.FastLoading = ToggleFastLoading.IsChecked ?? false; SaveSettings(); }

        protected override void OnDeactivated(EventArgs e) { base.OnDeactivated(e); if (SidePanelPopup != null) SidePanelPopup.IsOpen = false; }
        protected override void OnStateChanged(EventArgs e) { base.OnStateChanged(e); if (this.WindowState == WindowState.Minimized && SidePanelPopup != null) SidePanelPopup.IsOpen = false; }
    }
}
