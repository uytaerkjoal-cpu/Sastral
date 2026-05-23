using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Sastral
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer _uiTimer;
        private DispatcherTimer _popupCloseTimer;

        public MainWindow()
        {
            InitializeComponent();
            InitializeWebViews();
            InitializeMonitoring();
            InitializePopupTimer();
        }

        private async void InitializeWebViews()
        {
            try
            {
                var options = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions("--disable-web-security");
                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, null, options);

                await Editor.EnsureCoreWebView2Async(env);
                await Script.EnsureCoreWebView2Async(env);

                Editor.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
                Script.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

                Script.WebMessageReceived += Script_WebMessageReceived;

                string editorPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Bin", "Html", "Editor.html");
                string scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Bin", "Html", "Script.html");

                if (File.Exists(editorPath))
                    Editor.CoreWebView2.Navigate(new Uri(editorPath).AbsoluteUri);

                if (File.Exists(scriptPath))
                    Script.CoreWebView2.Navigate(new Uri(scriptPath).AbsoluteUri);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }

        private void SwitchToEditor()
        {
            if (EditorContainer != null && ScriptContainer != null)
            {
                EditorContainer.Visibility = Visibility.Visible;
                EditorContainer.Opacity = 1;
                ScriptContainer.Visibility = Visibility.Collapsed;
                ScriptContainer.Opacity = 0;
            }
        }

        private void SwitchToScripts()
        {
            if (EditorContainer != null && ScriptContainer != null)
            {
                ScriptContainer.Visibility = Visibility.Visible;
                ScriptContainer.Opacity = 1;
                EditorContainer.Visibility = Visibility.Collapsed;
                EditorContainer.Opacity = 0;
            }
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
                if (e.Key == Key.E)
                {
                    SwitchToEditor();
                    e.Handled = true;
                }
                else if (e.Key == Key.S)
                {
                    SwitchToScripts();
                    e.Handled = true;
                }
            }
        }

        private async void btnExecute_Click(object sender, RoutedEventArgs e)
        {
            if (Editor.CoreWebView2 != null)
            {
                string code = await Editor.CoreWebView2.ExecuteScriptAsync(
                    "window.editor ? window.editor.getValue() : '';"
                );

                if (!string.IsNullOrWhiteSpace(code))
                {
                    code = System.Text.RegularExpressions.Regex.Unescape(
                        code.Trim('"')
                    );

                    API.Execute(code);
                }
            }
        }

        private async void btnAttach_Click(object sender, RoutedEventArgs e)
        {
            await API.Attach();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
        }

        private void btnMinimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void btnMaximize_Click(object sender, RoutedEventArgs e)
        {
            if (this.WindowState == WindowState.Maximized)
            {
                this.WindowState = WindowState.Normal;
            }
            else
            {
                Storyboard sb = (Storyboard)this.Resources["FullscreenAnimation"];
                if (sb != null)
                {
                    sb.Begin();
                }
                this.WindowState = WindowState.Maximized;
            }
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void btnLogo_Click(object sender, RoutedEventArgs e)
        {
            string url = "https://discord.gg/qF43JNunM9";
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception)
            {
            }
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

            if (!LeftGutter.IsMouseOver && !SidePanelPopup.IsMouseOver)
            {
                SidePanelPopup.IsOpen = false;
            }
        }

        private void LeftGutter_MouseEnter(object sender, MouseEventArgs e)
        {
            _popupCloseTimer.Stop();
            SidePanelPopup.IsOpen = true;
        }

        private void LeftGutter_MouseLeave(object sender, MouseEventArgs e)
        {
            _popupCloseTimer.Start();
        }

        private void LeftGutter_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            SidePanelPopup.IsOpen = true;
        }

        private void SidePanelPopup_MouseEnter(object sender, MouseEventArgs e)
        {
            _popupCloseTimer.Stop();
        }

        private void SidePanelPopup_MouseLeave(object sender, MouseEventArgs e)
        {
            _popupCloseTimer.Start();
        }

        private void btnSelectEditor_Click(object sender, RoutedEventArgs e)
        {
            SwitchToEditor();
            SidePanelPopup.IsOpen = false;
        }

        private void btnSelectScript_Click(object sender, RoutedEventArgs e)
        {
            SwitchToScripts();
            SidePanelPopup.IsOpen = false;
        }

        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);
            if (SidePanelPopup != null)
                SidePanelPopup.IsOpen = false;
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (this.WindowState == WindowState.Minimized && SidePanelPopup != null)
                SidePanelPopup.IsOpen = false;
        }
    }
}
