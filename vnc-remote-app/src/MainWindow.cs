using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.IO.Path;

namespace Asynchronous
{
    // =======================================================================
    // PERSISTENT SETTINGS MODEL
    // =======================================================================
    public class AppSettings
    {
        public string DeviceId { get; set; }
        public string Password { get; set; }
        public string RelayHost { get; set; }
        public string ControlPort { get; set; }
        public string RelayPort { get; set; }
        public bool UseTls { get; set; }
        public string LastTargetId { get; set; }
        public string LastTargetPassword { get; set; }
        public string Quality { get; set; }
        public bool AutoStartHost { get; set; }

        public AppSettings()
        {
            RelayHost = "127.0.0.1";
            ControlPort = "8443";
            RelayPort = "8444";
            UseTls = true;
            LastTargetId = "";
            LastTargetPassword = "";
            Quality = "balanced";
            AutoStartHost = true;
        }

        public static string GetSettingsPath()
        {
            try
            {
                // 1. Portable mode: check if settings.json exists in application folder
                string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
                if (File.Exists(localPath)) return localPath;

                // 2. Standard path: %APPDATA%\asynchronous\settings.json
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string newDir = Path.Combine(appData, "asynchronous");
                string newPath = Path.Combine(newDir, "settings.json");

                // Backward compatibility: Migrate settings from earlier VNCremoteconnect install if present
                string oldPath = Path.Combine(appData, "VNCremoteconnect", "settings.json");
                if (!File.Exists(newPath) && File.Exists(oldPath))
                {
                    if (!Directory.Exists(newDir)) Directory.CreateDirectory(newDir);
                    File.Copy(oldPath, newPath, true);
                    return newPath;
                }

                if (!Directory.Exists(newDir))
                {
                    Directory.CreateDirectory(newDir);
                }
                return newPath;
            }
            catch
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            }
        }

        public static AppSettings Load()
        {
            try
            {
                string path = GetSettingsPath();
                if (File.Exists(path))
                {
                    string jsonStr = File.ReadAllText(path, Encoding.UTF8);
                    var js = new JavaScriptSerializer();
                    var loaded = js.Deserialize<AppSettings>(jsonStr);
                    if (loaded != null && !string.IsNullOrEmpty(loaded.DeviceId) && !string.IsNullOrEmpty(loaded.Password))
                    {
                        return loaded;
                    }
                }
            }
            catch { }

            var created = new AppSettings();
            created.DeviceId = GeneratePermanentDeviceId();
            created.Password = GeneratePermanentPassword();
            created.Save();
            return created;
        }

        public void Save()
        {
            try
            {
                string path = GetSettingsPath();
                var js = new JavaScriptSerializer();
                string jsonStr = js.Serialize(this);
                File.WriteAllText(path, jsonStr, Encoding.UTF8);
            }
            catch { }
        }

        public static string GeneratePermanentDeviceId()
        {
            var r = new Random();
            int n1 = r.Next(100, 999);
            int n2 = r.Next(100, 999);
            int n3 = r.Next(100, 999);
            return string.Format("{0}-{1}-{2}", n1, n2, n3);
        }

        public static string GeneratePermanentPassword()
        {
            var r = new Random();
            return r.Next(100000, 999999).ToString();
        }
    }

    // =======================================================================
    // MAIN APPLICATION WINDOW
    // =======================================================================
    public class MainWindow : Window
    {
        // Version & Repository
        public const string CurrentVersion = "1.0.0";
        public const string GitHubRepo = "vihivincorp/VNCremoteconnect";

        // Colors & Brushes (Curated Dark Slate & Indigo Palette)
        private static readonly Brush BgBrush = new SolidColorBrush(Color.FromRgb(15, 23, 42));       // #0F172A
        private static readonly Brush HeaderBrush = new SolidColorBrush(Color.FromRgb(30, 41, 59));   // #1E293B
        private static readonly Brush CardBrush = new SolidColorBrush(Color.FromRgb(30, 41, 59));     // #1E293B
        private static readonly Brush CardBorder = new SolidColorBrush(Color.FromRgb(51, 65, 85));    // #334155
        private static readonly Brush InputBg = new SolidColorBrush(Color.FromRgb(15, 23, 42));       // #0F172A
        private static readonly Brush InputBorder = new SolidColorBrush(Color.FromRgb(71, 85, 105));  // #475569
        private static readonly Brush TextWhite = new SolidColorBrush(Color.FromRgb(248, 250, 252));  // #F8FAFC
        private static readonly Brush TextMuted = new SolidColorBrush(Color.FromRgb(148, 163, 184));  // #94A3B8
        private static readonly Brush TextDim = new SolidColorBrush(Color.FromRgb(100, 116, 139));    // #64748B
        private static readonly Brush PrimaryIndigo = new SolidColorBrush(Color.FromRgb(99, 102, 241)); // #6366F1
        private static readonly Brush PrimaryHover = new SolidColorBrush(Color.FromRgb(79, 70, 229));  // #4F46E5
        private static readonly Brush EmeraldGreen = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // #10B981
        private static readonly Brush RoseRed = new SolidColorBrush(Color.FromRgb(244, 63, 94));       // #F43F5E
        private static readonly Brush AmberYellow = new SolidColorBrush(Color.FromRgb(245, 158, 11));  // #F59E0B
        private static readonly Brush CyanBlue = new SolidColorBrush(Color.FromRgb(6, 182, 212));      // #06B6D4
        private static readonly Brush UpdateBannerBg = new SolidColorBrush(Color.FromRgb(49, 46, 129)); // Dark Indigo

        // State & Settings
        private AppSettings settings;
        private Process hostProcess = null;
        private Process viewerProcess = null;
        private Process relayProcess = null;
        private JavaScriptSerializer json = new JavaScriptSerializer();
        private bool isHostManuallyPaused = false;
        private string latestReleaseDownloadUrl = "https://github.com/vihivincorp/VNCremoteconnect/releases";

        // UI Controls - Navigation
        private Button tabHostBtn;
        private Button tabViewerBtn;
        private Button tabRelayBtn;
        private Button tabAboutBtn;
        private Grid panelHost;
        private Grid panelViewer;
        private Grid panelRelay;
        private Grid panelAbout;
        private TextBlock statusPill;
        private Ellipse statusDot;

        // UI Controls - Update Banner
        private Border updateBanner;
        private TextBlock txtUpdateBanner;

        // UI Controls - Host Tab
        private TextBox txtMyDeviceId;
        private TextBox txtMyPassword;
        private Button btnToggleHost;
        private TextBlock txtHostStatus;
        private TextBox txtHostLog;
        private TextBlock txtTvnServerStatus;

        // UI Controls - Viewer Tab
        private TextBox txtTargetId;
        private TextBox txtTargetPin;
        private ComboBox cmbQuality;
        private Button btnConnect;
        private TextBlock txtViewerStatus;
        private TextBox txtViewerLog;

        // UI Controls - Relay Tab
        private TextBox txtRelayHost;
        private TextBox txtControlPort;
        private TextBox txtDataPort;
        private CheckBox chkUseTls;
        private Button btnPingRelay;
        private TextBlock txtPingResult;
        private Button btnToggleLocalRelay;
        private TextBlock txtLocalRelayStatus;

        // UI Controls - About Tab
        private TextBlock txtManualUpdateResult;

        public MainWindow()
        {
            this.Title = "asynchronous - Secure Cross-Network Desktop";
            this.Width = 860;
            this.Height = 650;
            this.MinWidth = 800;
            this.MinHeight = 580;
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            this.Background = BgBrush;
            this.Foreground = TextWhite;
            this.FontFamily = new FontFamily("Segoe UI");

            settings = AppSettings.Load();

            BuildUI();

            this.Closing += (s, e) =>
            {
                SaveCurrentSettings();
                CleanupProcesses();
            };

            CheckTightVncServerInstallation();

            // Auto-start hosting immediately on launch for zero-click unattended access
            if (settings.AutoStartHost)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    StartHostSharing();
                }));
            }

            // Check for updates asynchronously in background (non-blocking)
            CheckForUpdatesAsync(isManualCheck: false);
        }

        private void BuildUI()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto }); // 0. Update Banner
            root.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(64) }); // 1. Header
            root.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(48) }); // 2. Tab bar
            root.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(1, GridUnitType.Star) }); // 3. Content
            root.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(32) }); // 4. Footer

            // 0. Update Notification Banner
            updateBanner = CreateUpdateBanner();
            Grid.SetRow(updateBanner, 0);
            root.Children.Add(updateBanner);

            // 1. Header
            var header = CreateHeader();
            Grid.SetRow(header, 1);
            root.Children.Add(header);

            // 2. Tab Bar
            var tabBar = CreateTabBar();
            Grid.SetRow(tabBar, 2);
            root.Children.Add(tabBar);

            // 3. Tab Contents
            var contentContainer = new Grid();
            Grid.SetRow(contentContainer, 3);
            contentContainer.Margin = new Thickness(24, 16, 24, 8);

            panelHost = CreateHostPanel();
            panelViewer = CreateViewerPanel();
            panelRelay = CreateRelayPanel();
            panelAbout = CreateAboutPanel();

            contentContainer.Children.Add(panelHost);
            contentContainer.Children.Add(panelViewer);
            contentContainer.Children.Add(panelRelay);
            contentContainer.Children.Add(panelAbout);
            root.Children.Add(contentContainer);

            // 4. Footer
            var footer = CreateFooter();
            Grid.SetRow(footer, 4);
            root.Children.Add(footer);

            this.Content = root;

            // Show default tab (Host)
            SwitchTab(0);
        }

        private Border CreateUpdateBanner()
        {
            var border = new Border()
            {
                Background = UpdateBannerBg,
                BorderBrush = PrimaryIndigo,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(20, 8, 20, 8),
                Visibility = Visibility.Collapsed
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });

            txtUpdateBanner = new TextBlock()
            {
                Text = "🎉 New version available! Your current version remains fully compatible to connect.",
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = TextWhite,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(txtUpdateBanner, 0);
            grid.Children.Add(txtUpdateBanner);

            var btnDownload = new Button()
            {
                Content = "⬇️  Download Update",
                Background = PrimaryIndigo,
                Foreground = TextWhite,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(12, 4, 12, 4),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Margin = new Thickness(10, 0, 8, 0)
            };
            btnDownload.Click += (s, e) =>
            {
                try { Process.Start(latestReleaseDownloadUrl); } catch { }
            };
            Grid.SetColumn(btnDownload, 1);
            grid.Children.Add(btnDownload);

            var btnDismiss = new Button()
            {
                Content = "✕",
                Background = Brushes.Transparent,
                Foreground = TextMuted,
                FontSize = 12,
                Padding = new Thickness(8, 4, 8, 4),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            btnDismiss.Click += (s, e) => { border.Visibility = Visibility.Collapsed; };
            Grid.SetColumn(btnDismiss, 2);
            grid.Children.Add(btnDismiss);

            border.Child = grid;
            return border;
        }

        private UIElement CreateHeader()
        {
            var headerGrid = new Grid() { Background = HeaderBrush };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });

            // Brand Stack
            var brandStack = new StackPanel() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 0, 0) };

            var logoBadge = new Border()
            {
                Background = PrimaryIndigo,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 12, 0)
            };
            var logoText = new TextBlock() { Text = "async", FontWeight = FontWeights.Bold, FontSize = 15, Foreground = TextWhite };
            logoBadge.Child = logoText;
            brandStack.Children.Add(logoBadge);

            var titleStack = new StackPanel() { VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock() { Text = "asynchronous", FontWeight = FontWeights.SemiBold, FontSize = 18, Foreground = TextWhite };
            var subtitle = new TextBlock() { Text = "Cross-Network Desktop • Permanent Unattended Access • Zero-Knowledge E2EE", FontSize = 11, Foreground = TextMuted };
            titleStack.Children.Add(title);
            titleStack.Children.Add(subtitle);
            brandStack.Children.Add(titleStack);

            Grid.SetColumn(brandStack, 0);
            headerGrid.Children.Add(brandStack);

            // Status Pill (Right side)
            var pillBorder = new Border()
            {
                Background = CardBorder,
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 24, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var pillStack = new StackPanel() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            statusDot = new Ellipse() { Width = 8, Height = 8, Fill = AmberYellow, Margin = new Thickness(0, 0, 8, 0) };
            statusPill = new TextBlock() { Text = "Initializing...", FontSize = 12, FontWeight = FontWeights.Medium, Foreground = TextMuted };
            pillStack.Children.Add(statusDot);
            pillStack.Children.Add(statusPill);
            pillBorder.Child = pillStack;

            Grid.SetColumn(pillBorder, 1);
            headerGrid.Children.Add(pillBorder);

            return headerGrid;
        }

        private UIElement CreateTabBar()
        {
            var tabContainer = new Border()
            {
                Background = HeaderBrush,
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = CardBorder
            };

            var stack = new StackPanel() { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 0, 0, 0) };

            tabHostBtn = CreateTabButton("  💻  This PC (Host)", 0);
            tabViewerBtn = CreateTabButton("  🔗  Connect to Remote (Viewer)", 1);
            tabRelayBtn = CreateTabButton("  ⚙️  Relay & Network", 2);
            tabAboutBtn = CreateTabButton("  ℹ️  About & License", 3);

            stack.Children.Add(tabHostBtn);
            stack.Children.Add(tabViewerBtn);
            stack.Children.Add(tabRelayBtn);
            stack.Children.Add(tabAboutBtn);

            tabContainer.Child = stack;
            return tabContainer;
        }

        private Button CreateTabButton(string label, int index)
        {
            var btn = new Button()
            {
                Content = label,
                Foreground = TextMuted,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 2),
                BorderBrush = Brushes.Transparent,
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                Padding = new Thickness(16, 0, 16, 0),
                Cursor = Cursors.Hand
            };
            btn.Click += (s, e) => SwitchTab(index);
            return btn;
        }

        private void SwitchTab(int index)
        {
            panelHost.Visibility = (index == 0) ? Visibility.Visible : Visibility.Collapsed;
            panelViewer.Visibility = (index == 1) ? Visibility.Visible : Visibility.Collapsed;
            panelRelay.Visibility = (index == 2) ? Visibility.Visible : Visibility.Collapsed;
            panelAbout.Visibility = (index == 3) ? Visibility.Visible : Visibility.Collapsed;

            Button[] btns = new Button[] { tabHostBtn, tabViewerBtn, tabRelayBtn, tabAboutBtn };
            for (int i = 0; i < btns.Length; i++)
            {
                if (i == index)
                {
                    btns[i].Foreground = TextWhite;
                    btns[i].BorderBrush = PrimaryIndigo;
                }
                else
                {
                    btns[i].Foreground = TextMuted;
                    btns[i].BorderBrush = Brushes.Transparent;
                }
            }
        }

        // ===================================================================
        // TAB 1: HOST PANEL (PERMANENT UNATTENDED ACCESS)
        // ===================================================================
        private Grid CreateHostPanel()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1.15, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(20) }); // Spacing
            grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1.0, GridUnitType.Star) });

            // Left Card: Permanent Credentials & Auto-Hosting Status
            var leftCard = CreateCard("Permanent Remote Access (This PC)");
            var leftStack = new StackPanel();

            // Unattended Access Notification Badge
            var unattendedBadge = new Border()
            {
                Background = InputBg,
                BorderBrush = PrimaryIndigo,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 12)
            };
            var unattendedText = new TextBlock()
            {
                Text = "⚡ Unattended Access Active: Your Device ID and Password are permanent. Remote viewers connect directly without requiring you to click or confirm sharing prompts.",
                FontSize = 11,
                Foreground = CyanBlue,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 16
            };
            unattendedBadge.Child = unattendedText;
            leftStack.Children.Add(unattendedBadge);

            // Device ID Box
            leftStack.Children.Add(new TextBlock() { Text = "YOUR PERMANENT DEVICE ID", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = TextMuted, Margin = new Thickness(0, 4, 0, 4) });
            var idContainer = new Border()
            {
                Background = InputBg,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6)
            };
            var idStack = new Grid();
            idStack.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            idStack.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });
            idStack.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });

            txtMyDeviceId = new TextBox()
            {
                Text = settings.DeviceId,
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = PrimaryIndigo,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(txtMyDeviceId, 0);
            idStack.Children.Add(txtMyDeviceId);

            var btnCopyId = CreateIconButton("📋 Copy ID", () =>
            {
                if (!string.IsNullOrEmpty(txtMyDeviceId.Text))
                {
                    Clipboard.SetText(txtMyDeviceId.Text.Trim());
                    MessageBox.Show("Permanent Device ID copied to clipboard!", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            });
            btnCopyId.Margin = new Thickness(4, 0, 4, 0);
            Grid.SetColumn(btnCopyId, 1);
            idStack.Children.Add(btnCopyId);

            var btnNewId = CreateIconButton("🔄 New ID", () =>
            {
                var res = MessageBox.Show("Generate a new Permanent Device ID for this PC?\n(Remote partners will need your new ID to connect)", "New Device ID", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res == MessageBoxResult.Yes)
                {
                    string newId = AppSettings.GeneratePermanentDeviceId();
                    settings.DeviceId = newId;
                    txtMyDeviceId.Text = newId;
                    settings.Save();
                    AppendHostLog("Permanent Device ID changed to: " + newId);
                    if (hostProcess != null && !hostProcess.HasExited)
                    {
                        StartHostSharing();
                    }
                }
            });
            Grid.SetColumn(btnNewId, 2);
            idStack.Children.Add(btnNewId);

            idContainer.Child = idStack;
            leftStack.Children.Add(idContainer);

            // Permanent Password Box
            leftStack.Children.Add(new TextBlock() { Text = "YOUR PERMANENT PASSWORD", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = TextMuted, Margin = new Thickness(0, 14, 0, 4) });
            var pinContainer = new Border()
            {
                Background = InputBg,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6)
            };
            var pinStack = new Grid();
            pinStack.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            pinStack.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });
            pinStack.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });

            txtMyPassword = new TextBox()
            {
                Text = settings.Password,
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = EmeraldGreen,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(txtMyPassword, 0);
            pinStack.Children.Add(txtMyPassword);

            var btnCopyPin = CreateIconButton("📋 Copy Password", () =>
            {
                Clipboard.SetText(txtMyPassword.Text.Trim());
                MessageBox.Show("Permanent Password copied to clipboard!", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
            });
            btnCopyPin.Margin = new Thickness(4, 0, 4, 0);
            Grid.SetColumn(btnCopyPin, 1);
            pinStack.Children.Add(btnCopyPin);

            var btnSavePin = CreateIconButton("💾 Save Password", () =>
            {
                string newPwd = txtMyPassword.Text.Trim();
                if (string.IsNullOrEmpty(newPwd))
                {
                    MessageBox.Show("Password cannot be blank.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                settings.Password = newPwd;
                settings.Save();
                AppendHostLog("Permanent password updated to: " + newPwd);
                if (hostProcess != null && !hostProcess.HasExited)
                {
                    StartHostSharing();
                }
                MessageBox.Show("Permanent password saved successfully!\nViewers will use this password to connect.", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            });
            Grid.SetColumn(btnSavePin, 2);
            pinStack.Children.Add(btnSavePin);

            pinContainer.Child = pinStack;
            leftStack.Children.Add(pinContainer);

            // TightVNC Server Engine Indicator
            var vncEngineBox = new Border()
            {
                Background = InputBg,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 14, 0, 14)
            };
            txtTvnServerStatus = new TextBlock()
            {
                Text = "TightVNC Server: Checking...",
                FontSize = 11,
                Foreground = TextMuted
            };
            vncEngineBox.Child = txtTvnServerStatus;
            leftStack.Children.Add(vncEngineBox);

            // Host Toggle / Pause Button
            btnToggleHost = new Button()
            {
                Content = "🟢  Hosting Online (Click to Pause)",
                Background = EmeraldGreen,
                Foreground = TextWhite,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Height = 44,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            btnToggleHost.Click += (s, e) => ToggleHostSharing();
            leftStack.Children.Add(btnToggleHost);

            txtHostStatus = new TextBlock()
            {
                Text = "Status: Online & Ready for Incoming Connections",
                FontSize = 12,
                Foreground = EmeraldGreen,
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            leftStack.Children.Add(txtHostStatus);

            leftCard.Child = leftStack;
            Grid.SetColumn(leftCard, 0);
            grid.Children.Add(leftCard);

            // Right Card: Live Security & Activity Log
            var rightCard = CreateCard("Security & Live Activity");
            var rightStack = new Grid();
            rightStack.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            rightStack.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(1, GridUnitType.Star) });

            var secBadge = new Border()
            {
                Background = InputBg,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var secText = new TextBlock()
            {
                Text = "🔒 End-to-End Encrypted (X25519 ECDH + AES-256-GCM AEAD)\nAll remote desktop frames are encrypted directly with your permanent password.",
                FontSize = 11,
                Foreground = CyanBlue,
                TextWrapping = TextWrapping.Wrap
            };
            secBadge.Child = secText;
            Grid.SetRow(secBadge, 0);
            rightStack.Children.Add(secBadge);

            txtHostLog = new TextBox()
            {
                Background = InputBg,
                Foreground = TextMuted,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                IsReadOnly = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                TextWrapping = TextWrapping.Wrap,
                Text = "asynchronous Host Engine Initialized.\nPermanent credentials active. Ready for connections.\n"
            };
            Grid.SetRow(txtHostLog, 1);
            rightStack.Children.Add(txtHostLog);

            rightCard.Child = rightStack;
            Grid.SetColumn(rightCard, 2);
            grid.Children.Add(rightCard);

            return grid;
        }

        // ===================================================================
        // TAB 2: VIEWER PANEL (CONNECT TO PARTNER)
        // ===================================================================
        private Grid CreateViewerPanel()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1.15, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(20) });
            grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1.0, GridUnitType.Star) });

            // Left Card: Connect Inputs
            var leftCard = CreateCard("Connect to Remote Computer");
            var leftStack = new StackPanel();

            leftStack.Children.Add(new TextBlock() { Text = "PARTNER DEVICE ID", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = TextMuted, Margin = new Thickness(0, 8, 0, 4) });
            txtTargetId = new TextBox()
            {
                Background = InputBg,
                Foreground = TextWhite,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                FontSize = 15,
                Padding = new Thickness(10, 8, 10, 8),
                Text = settings.LastTargetId ?? ""
            };
            leftStack.Children.Add(txtTargetId);

            leftStack.Children.Add(new TextBlock() { Text = "PARTNER PASSWORD", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = TextMuted, Margin = new Thickness(0, 16, 0, 4) });
            txtTargetPin = new TextBox()
            {
                Background = InputBg,
                Foreground = TextWhite,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                FontSize = 15,
                Padding = new Thickness(10, 8, 10, 8),
                Text = settings.LastTargetPassword ?? ""
            };
            leftStack.Children.Add(txtTargetPin);

            leftStack.Children.Add(new TextBlock() { Text = "CONNECTION PROFILE", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = TextMuted, Margin = new Thickness(0, 16, 0, 4) });
            cmbQuality = new ComboBox()
            {
                Background = InputBg,
                Foreground = TextWhite,
                BorderBrush = CardBorder,
                FontSize = 13,
                Padding = new Thickness(8, 6, 8, 6)
            };
            cmbQuality.Items.Add(new ComboBoxItem() { Content = "⚡ Fast (College / Mobile Wi-Fi - Optimized)", Tag = "fast", IsSelected = (settings.Quality == "fast") });
            cmbQuality.Items.Add(new ComboBoxItem() { Content = "⚖️ Balanced (Recommended for Home/Broadband)", Tag = "balanced", IsSelected = (settings.Quality == "balanced" || string.IsNullOrEmpty(settings.Quality)) });
            cmbQuality.Items.Add(new ComboBoxItem() { Content = "💎 Maximum Quality (Lossless Colors)", Tag = "best", IsSelected = (settings.Quality == "best") });
            leftStack.Children.Add(cmbQuality);

            btnConnect = new Button()
            {
                Content = "🚀  Connect to Remote PC",
                Background = PrimaryIndigo,
                Foreground = TextWhite,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Height = 44,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 24, 0, 0)
            };
            btnConnect.Click += (s, e) => ToggleViewerConnection();
            leftStack.Children.Add(btnConnect);

            txtViewerStatus = new TextBlock()
            {
                Text = "Status: Ready to connect",
                FontSize = 12,
                Foreground = TextMuted,
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            leftStack.Children.Add(txtViewerStatus);

            leftCard.Child = leftStack;
            Grid.SetColumn(leftCard, 0);
            grid.Children.Add(leftCard);

            // Right Card: Viewer Status & Logs
            var rightCard = CreateCard("Connection Progress & Reconnection");
            var rightStack = new Grid();
            rightStack.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            rightStack.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(1, GridUnitType.Star) });

            var tipBadge = new Border()
            {
                Background = InputBg,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var tipText = new TextBlock()
            {
                Text = "💡 Seamless Reconnect: If a session disconnects, the remote PC remains online. Simply click 'Reconnect' anytime with the same ID and Password.",
                FontSize = 11,
                Foreground = EmeraldGreen,
                TextWrapping = TextWrapping.Wrap
            };
            tipBadge.Child = tipText;
            Grid.SetRow(tipBadge, 0);
            rightStack.Children.Add(tipBadge);

            txtViewerLog = new TextBox()
            {
                Background = InputBg,
                Foreground = TextMuted,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                IsReadOnly = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                TextWrapping = TextWrapping.Wrap,
                Text = "Enter the Partner Device ID and Password above to start session.\nCredentials are remembered for instant 1-click reconnect.\n"
            };
            Grid.SetRow(txtViewerLog, 1);
            rightStack.Children.Add(txtViewerLog);

            rightCard.Child = rightStack;
            Grid.SetColumn(rightCard, 2);
            grid.Children.Add(rightCard);

            return grid;
        }

        // ===================================================================
        // TAB 3: RELAY & NETWORK SETTINGS
        // ===================================================================
        private Grid CreateRelayPanel()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1.15, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(20) });
            grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1.0, GridUnitType.Star) });

            // Left Card: Relay Configuration
            var leftCard = CreateCard("Relay & Rendezvous Server");
            var leftStack = new StackPanel();

            leftStack.Children.Add(new TextBlock() { Text = "RELAY SERVER HOST / IP", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = TextMuted, Margin = new Thickness(0, 8, 0, 4) });
            txtRelayHost = new TextBox()
            {
                Background = InputBg,
                Foreground = TextWhite,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                FontSize = 14,
                Padding = new Thickness(10, 6, 10, 6),
                Text = settings.RelayHost ?? "127.0.0.1"
            };
            leftStack.Children.Add(txtRelayHost);

            var portGrid = new Grid() { Margin = new Thickness(0, 12, 0, 0) };
            portGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            portGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(12) });
            portGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });

            var p1Stack = new StackPanel();
            p1Stack.Children.Add(new TextBlock() { Text = "CONTROL PORT", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = TextMuted, Margin = new Thickness(0, 0, 0, 4) });
            txtControlPort = new TextBox() { Background = InputBg, Foreground = TextWhite, BorderBrush = CardBorder, FontSize = 14, Padding = new Thickness(8, 6, 8, 6), Text = settings.ControlPort ?? "8443" };
            p1Stack.Children.Add(txtControlPort);
            Grid.SetColumn(p1Stack, 0);
            portGrid.Children.Add(p1Stack);

            var p2Stack = new StackPanel();
            p2Stack.Children.Add(new TextBlock() { Text = "RELAY DATA PORT", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = TextMuted, Margin = new Thickness(0, 0, 0, 4) });
            txtDataPort = new TextBox() { Background = InputBg, Foreground = TextWhite, BorderBrush = CardBorder, FontSize = 14, Padding = new Thickness(8, 6, 8, 6), Text = settings.RelayPort ?? "8444" };
            p2Stack.Children.Add(txtDataPort);
            Grid.SetColumn(p2Stack, 2);
            portGrid.Children.Add(p2Stack);

            leftStack.Children.Add(portGrid);

            chkUseTls = new CheckBox()
            {
                Content = "Use TLS Transport Encryption (Encapsulated)",
                Foreground = TextWhite,
                IsChecked = settings.UseTls,
                Margin = new Thickness(0, 16, 0, 16)
            };
            leftStack.Children.Add(chkUseTls);

            btnPingRelay = new Button()
            {
                Content = "⚡  Test Relay Connection (Ping)",
                Background = HeaderBrush,
                Foreground = TextWhite,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                Height = 36,
                Cursor = Cursors.Hand
            };
            btnPingRelay.Click += (s, e) => PingRelayServer();
            leftStack.Children.Add(btnPingRelay);

            txtPingResult = new TextBlock()
            {
                Text = "",
                FontSize = 12,
                Foreground = EmeraldGreen,
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            leftStack.Children.Add(txtPingResult);

            leftCard.Child = leftStack;
            Grid.SetColumn(leftCard, 0);
            grid.Children.Add(leftCard);

            // Right Card: Built-in Relay Runner
            var rightCard = CreateCard("Run Built-in Relay On This PC");
            var rightStack = new StackPanel();

            var infoText = new TextBlock()
            {
                Text = "Turn this PC into a private Relay Server with 1 click!\nFriends or laptops on your Wi-Fi, LAN, or hotspot can connect by setting your IP address as their Relay Host.",
                FontSize = 12,
                Foreground = TextMuted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16)
            };
            rightStack.Children.Add(infoText);

            btnToggleLocalRelay = new Button()
            {
                Content = "🌐  Start Built-in Relay Server",
                Background = CyanBlue,
                Foreground = TextWhite,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Height = 40,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            btnToggleLocalRelay.Click += (s, e) => ToggleBuiltInRelay();
            rightStack.Children.Add(btnToggleLocalRelay);

            txtLocalRelayStatus = new TextBlock()
            {
                Text = "Relay Status: Stopped",
                FontSize = 12,
                Foreground = TextMuted,
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            rightStack.Children.Add(txtLocalRelayStatus);

            rightCard.Child = rightStack;
            Grid.SetColumn(rightCard, 2);
            grid.Children.Add(rightCard);

            return grid;
        }

        // ===================================================================
        // TAB 4: ABOUT, UPDATES & LICENSING
        // ===================================================================
        private Grid CreateAboutPanel()
        {
            var grid = new Grid();
            var card = CreateCard("About asynchronous & Version Info");
            var stack = new StackPanel();

            var heading = new TextBlock()
            {
                Text = "asynchronous - Secure Windows Remote Desktop Solution",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = PrimaryIndigo,
                Margin = new Thickness(0, 0, 0, 8)
            };
            stack.Children.Add(heading);

            var verBox = new Border()
            {
                Background = InputBg,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 14)
            };
            var verGrid = new Grid();
            verGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            verGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });

            var verStack = new StackPanel();
            verStack.Children.Add(new TextBlock() { Text = "Installed Version: v" + CurrentVersion + " (Native WPF)", FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = TextWhite });
            verStack.Children.Add(new TextBlock() { Text = "Cross-Version Compatibility: Fully compatible with older and newer client versions.", FontSize = 11, Foreground = EmeraldGreen, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(verStack, 0);
            verGrid.Children.Add(verStack);

            var btnCheckUpdates = CreateIconButton("🔍 Check for Updates", () =>
            {
                txtManualUpdateResult.Text = "Checking GitHub for updates...";
                txtManualUpdateResult.Foreground = AmberYellow;
                CheckForUpdatesAsync(isManualCheck: true);
            });
            Grid.SetColumn(btnCheckUpdates, 1);
            verGrid.Children.Add(btnCheckUpdates);

            verBox.Child = verGrid;
            stack.Children.Add(verBox);

            txtManualUpdateResult = new TextBlock()
            {
                Text = "",
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 12)
            };
            stack.Children.Add(txtManualUpdateResult);

            var desc = new TextBlock()
            {
                Text = "asynchronous is an open, private remote desktop system engineered for low-latency desktop control across NATs, firewalls, and campus networks without port forwarding.\n\n" +
                       "Key Architectural Components:\n" +
                       "• Permanent Credentials & Unattended Access: Permanent unique 9-digit Device ID and permanent password per PC. Viewers connect without manual confirmation popups.\n" +
                       "• Backward & Forward Compatibility: Automatically checks for new versions while remaining fully capable of connecting with earlier or updated releases.\n" +
                       "• Zero-Knowledge Relay Architecture: The rendezvous and relay servers act solely as blind byte conduits. They cannot inspect, record, or tamper with screen frames or input.\n" +
                       "• Authenticated End-to-End Encryption: Ephemeral X25519 Elliptic-Curve Diffie-Hellman combined with SHA-256 HKDF and AES-256-GCM authenticated encryption.\n" +
                       "• Reverse Loopback Bridge: TightVNC Server is strictly isolated from the external network and commanded over loopback, preventing unauthorized external exposure.\n\n" +
                       "TightVNC License Compliance Notice:\n" +
                       "asynchronous integrates with TightVNC 2.8.90, distributed under the GNU General Public License (GPL v2). Upstream copyright belongs to GlavSoft LLC and original authors. Full source code, build scripts, and license texts are included in this distribution in accordance with GPL v2 Section 3.",
                FontSize = 12,
                Foreground = TextMuted,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18
            };
            stack.Children.Add(desc);

            card.Child = stack;
            grid.Children.Add(card);
            return grid;
        }

        // ===================================================================
        // AUTOMATIC & MANUAL UPDATE CHECKER
        // ===================================================================
        private void CheckForUpdatesAsync(bool isManualCheck)
        {
            Task.Factory.StartNew(() =>
            {
                try
                {
                    // Support TLS 1.2 for GitHub API on .NET 4.x
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

                    string apiUrl = "https://api.github.com/repos/" + GitHubRepo + "/releases/latest";
                    var req = (HttpWebRequest)WebRequest.Create(apiUrl);
                    req.UserAgent = "asynchronous-desktop-app";
                    req.Timeout = 7000;

                    using (var resp = req.GetResponse())
                    using (var stream = resp.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string jsonText = reader.ReadToEnd();
                        var dict = json.Deserialize<System.Collections.Generic.Dictionary<string, object>>(jsonText);
                        if (dict != null && dict.ContainsKey("tag_name"))
                        {
                            string tagName = dict["tag_name"].ToString().Trim();
                            string cleanVer = tagName.TrimStart('v', 'V');
                            string releaseUrl = dict.ContainsKey("html_url") ? dict["html_url"].ToString() : "https://github.com/" + GitHubRepo + "/releases";

                            if (IsNewerVersion(cleanVer, CurrentVersion))
                            {
                                Dispatcher.Invoke(new Action(() =>
                                {
                                    latestReleaseDownloadUrl = releaseUrl;
                                    txtUpdateBanner.Text = string.Format("🎉 New version available: asynchronous v{0}! (Your current version remains fully compatible to connect)", cleanVer);
                                    updateBanner.Visibility = Visibility.Visible;

                                    if (isManualCheck)
                                    {
                                        txtManualUpdateResult.Text = string.Format("🎉 A new update (v{0}) is available! Click 'Download Update' above.", cleanVer);
                                        txtManualUpdateResult.Foreground = EmeraldGreen;
                                    }

                                    var res = MessageBox.Show(
                                        string.Format("A new version of asynchronous (v{0}) is available!\n\nYour current version remains fully compatible to connect.\nWould you like to open the download page?", cleanVer),
                                        "asynchronous - Update Available",
                                        MessageBoxButton.YesNo,
                                        MessageBoxImage.Information);

                                    if (res == MessageBoxResult.Yes)
                                    {
                                        try { Process.Start(releaseUrl); } catch { }
                                    }
                                }));
                            }
                            else if (isManualCheck)
                            {
                                Dispatcher.Invoke(new Action(() =>
                                {
                                    txtManualUpdateResult.Text = "✅ You are running the latest version of asynchronous (v" + CurrentVersion + ").";
                                    txtManualUpdateResult.Foreground = EmeraldGreen;
                                }));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (isManualCheck)
                    {
                        Dispatcher.Invoke(new Action(() =>
                        {
                            txtManualUpdateResult.Text = "Could not reach update server: " + ex.Message;
                            txtManualUpdateResult.Foreground = AmberYellow;
                        }));
                    }
                }
            });
        }

        private bool IsNewerVersion(string remoteVersion, string localVersion)
        {
            try
            {
                var rParts = remoteVersion.Split('.');
                var lParts = localVersion.Split('.');
                int len = Math.Max(rParts.Length, lParts.Length);

                for (int i = 0; i < len; i++)
                {
                    int r = i < rParts.Length ? int.Parse(rParts[i]) : 0;
                    int l = i < lParts.Length ? int.Parse(lParts[i]) : 0;
                    if (r > l) return true;
                    if (r < l) return false;
                }
                return false;
            }
            catch
            {
                return string.Compare(remoteVersion, localVersion, StringComparison.OrdinalIgnoreCase) > 0;
            }
        }

        // ===================================================================
        // UI Helper Components
        // ===================================================================
        private Border CreateCard(string title)
        {
            var border = new Border()
            {
                Background = CardBrush,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(18)
            };
            return border;
        }

        private Button CreateIconButton(string text, Action onClick)
        {
            var btn = new Button()
            {
                Content = text,
                Background = CardBorder,
                Foreground = TextWhite,
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Padding = new Thickness(8, 4, 8, 4),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            btn.Click += (s, e) => onClick();
            return btn;
        }

        private UIElement CreateFooter()
        {
            var footerGrid = new Grid() { Background = HeaderBrush };
            var text = new TextBlock()
            {
                Text = "asynchronous v" + CurrentVersion + " • 256-bit AES-GCM • GPL v2 Licensed • Native Windows",
                FontSize = 11,
                Foreground = TextDim,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            footerGrid.Children.Add(text);
            return footerGrid;
        }

        private void CheckTightVncServerInstallation()
        {
            string installedPath = @"C:\Program Files\TightVNC\tvnserver.exe";
            string portablePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vnc", "tvnserver.exe");
            string binPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "tightvnc-bin", "WinVNC.exe");
            string localWinVnc = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vnc", "WinVNC.exe");

            if (File.Exists(installedPath))
            {
                txtTvnServerStatus.Text = "✅ TightVNC Server: Detected (Installed Service)";
                txtTvnServerStatus.Foreground = EmeraldGreen;
            }
            else if (File.Exists(portablePath) || File.Exists(binPath) || File.Exists(localWinVnc))
            {
                txtTvnServerStatus.Text = "✅ TightVNC Server: Detected (Portable)";
                txtTvnServerStatus.Foreground = EmeraldGreen;
            }
            else
            {
                txtTvnServerStatus.Text = "⚠️ TightVNC Server: Not detected in standard path";
                txtTvnServerStatus.Foreground = AmberYellow;
            }
        }

        // ===================================================================
        // HOST PROCESS LIFECYCLE (AUTO-HOSTING & UNATTENDED ACCESS)
        // ===================================================================
        private void ToggleHostSharing()
        {
            if (hostProcess != null && !hostProcess.HasExited)
            {
                // User manually paused hosting
                try { hostProcess.Kill(); } catch { }
                hostProcess = null;
                isHostManuallyPaused = true;
                btnToggleHost.Content = "▶️  Resume Hosting";
                btnToggleHost.Background = AmberYellow;
                txtHostStatus.Text = "Status: Paused by user (Incoming connections paused)";
                statusDot.Fill = AmberYellow;
                statusPill.Text = "Paused";
                AppendHostLog("Hosting paused by user. Click 'Resume Hosting' to re-enable.");
                return;
            }

            isHostManuallyPaused = false;
            StartHostSharing();
        }

        private void StartHostSharing()
        {
            if (hostProcess != null && !hostProcess.HasExited)
            {
                try { hostProcess.Kill(); } catch { }
                hostProcess = null;
            }

            string engineJs = FindEngineScript();
            string nodeExe = FindNodeExecutable();

            if (string.IsNullOrEmpty(nodeExe))
            {
                MessageBox.Show("Node.js runtime was not found. Please ensure Node.js is installed or bundled.", "Runtime Missing", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string relay = txtRelayHost != null ? txtRelayHost.Text.Trim() : settings.RelayHost;
            string port = txtControlPort != null ? txtControlPort.Text.Trim() : settings.ControlPort;
            string dataPort = txtDataPort != null ? txtDataPort.Text.Trim() : settings.RelayPort;
            string devId = settings.DeviceId;
            string pin = settings.Password;

            // Automatically ensure built-in relay is running when connecting to 127.0.0.1
            if (relay == "127.0.0.1" || relay == "localhost")
            {
                EnsureRelayRunning(port, dataPort);
            }

            var psi = new ProcessStartInfo()
            {
                FileName = nodeExe,
                Arguments = string.Format("\"{0}\" host --relay {1} --control-port {2} --device-id \"{3}\" --pin \"{4}\" --ipc {5}",
                    engineJs, relay, port, devId, pin, ((chkUseTls == null || chkUseTls.IsChecked == true) ? "" : "--no-tls")),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(engineJs)
            };

            try
            {
                hostProcess = new Process() { StartInfo = psi };
                hostProcess.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Dispatcher.Invoke(new Action(() => HandleHostIpc(e.Data)));
                    }
                };
                hostProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Dispatcher.Invoke(new Action(() => AppendHostLog("ERR: " + e.Data)));
                    }
                };

                hostProcess.EnableRaisingEvents = true;
                hostProcess.Exited += (s, e) =>
                {
                    Dispatcher.Invoke(new Action(() =>
                    {
                        if (!isHostManuallyPaused)
                        {
                            AppendHostLog("Host process ended unexpectedly. Auto-recovering in 2s...");
                            var timer = new DispatcherTimer() { Interval = TimeSpan.FromSeconds(2) };
                            timer.Tick += (ts, te) =>
                            {
                                timer.Stop();
                                if (!isHostManuallyPaused) StartHostSharing();
                            };
                            timer.Start();
                        }
                    }));
                };

                hostProcess.Start();
                hostProcess.BeginOutputReadLine();
                hostProcess.BeginErrorReadLine();

                btnToggleHost.Content = "🟢  Hosting Online (Click to Pause)";
                btnToggleHost.Background = EmeraldGreen;
                txtHostStatus.Text = "Status: Connecting to Relay...";
                statusDot.Fill = AmberYellow;
                statusPill.Text = "Connecting...";
                AppendHostLog("Host registering with Relay at " + relay + ":" + port + "...");
            }
            catch (Exception ex)
            {
                AppendHostLog("Failed to start host agent: " + ex.Message);
            }
        }

        private void HandleHostIpc(string jsonLine)
        {
            try
            {
                var dict = json.Deserialize<System.Collections.Generic.Dictionary<string, object>>(jsonLine);
                if (!dict.ContainsKey("event")) return;

                string ev = dict["event"].ToString();
                switch (ev)
                {
                    case "HOST_READY":
                        string devId = dict.ContainsKey("deviceId") ? dict["deviceId"].ToString() : settings.DeviceId;
                        txtMyDeviceId.Text = devId;
                        txtHostStatus.Text = "Status: Online & Ready for Incoming Connections";
                        statusDot.Fill = EmeraldGreen;
                        statusPill.Text = "Online (Ready)";
                        btnToggleHost.Content = "🟢  Hosting Online (Click to Pause)";
                        btnToggleHost.Background = EmeraldGreen;
                        AppendHostLog("Host Online! Registered Permanent ID: " + devId);
                        break;

                    case "HOST_INCOMING":
                        AppendHostLog("Incoming connection authenticated. Initializing E2EE stream...");
                        break;

                    case "HOST_SESSION_CONNECTED":
                        txtHostStatus.Text = "Status: Client Connected! (E2EE Active)";
                        statusDot.Fill = CyanBlue;
                        statusPill.Text = "In Session";
                        AppendHostLog(">>> Partner connected! Encrypted desktop stream active. <<<");
                        break;

                    case "HOST_SESSION_CLOSED":
                        txtHostStatus.Text = "Status: Online & Ready for Incoming Connections";
                        statusDot.Fill = EmeraldGreen;
                        statusPill.Text = "Online (Ready)";
                        AppendHostLog("Partner session disconnected. Host remains online & ready for reconnection.");
                        break;

                    case "HOST_OFFLINE":
                        txtHostStatus.Text = "Status: Reconnecting to Relay...";
                        statusDot.Fill = AmberYellow;
                        statusPill.Text = "Reconnecting...";
                        if (dict.ContainsKey("message")) AppendHostLog(dict["message"].ToString());
                        break;

                    case "LOG":
                        if (dict.ContainsKey("text")) AppendHostLog(dict["text"].ToString());
                        break;

                    case "ERROR":
                        if (dict.ContainsKey("message")) AppendHostLog("ERROR: " + dict["message"].ToString());
                        break;
                }
            }
            catch
            {
                AppendHostLog(jsonLine);
            }
        }

        private void AppendHostLog(string line)
        {
            string ts = DateTime.Now.ToString("HH:mm:ss");
            if (txtHostLog != null)
            {
                txtHostLog.AppendText(string.Format("[{0}] {1}\r\n", ts, line));
                txtHostLog.ScrollToEnd();
            }
        }

        // ===================================================================
        // VIEWER PROCESS LIFECYCLE (CONNECT & RECONNECT)
        // ===================================================================
        private void ToggleViewerConnection()
        {
            if (viewerProcess != null && !viewerProcess.HasExited)
            {
                try { viewerProcess.Kill(); } catch { }
                viewerProcess = null;
                btnConnect.Content = "🚀  Connect to Remote PC";
                btnConnect.Background = PrimaryIndigo;
                txtViewerStatus.Text = "Status: Disconnected";
                AppendViewerLog("Viewer session cancelled by user.");
                return;
            }

            string targetId = txtTargetId.Text.Trim();
            string pin = txtTargetPin.Text.Trim();

            if (string.IsNullOrEmpty(targetId) || string.IsNullOrEmpty(pin))
            {
                MessageBox.Show("Please enter both Partner Device ID and Partner Password.", "Required Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Save last connected partner for 1-click reconnect next time
            settings.LastTargetId = targetId;
            settings.LastTargetPassword = pin;
            settings.Save();

            string engineJs = FindEngineScript();
            string nodeExe = FindNodeExecutable();
            string relay = txtRelayHost.Text.Trim();
            string port = txtControlPort.Text.Trim();

            string quality = "balanced";
            var selectedItem = cmbQuality.SelectedItem as ComboBoxItem;
            if (selectedItem != null && selectedItem.Tag != null)
            {
                quality = selectedItem.Tag.ToString();
            }
            settings.Quality = quality;

            var psi = new ProcessStartInfo()
            {
                FileName = nodeExe,
                Arguments = string.Format("\"{0}\" viewer --relay {1} --control-port {2} --target \"{3}\" --pin \"{4}\" --quality {5} --ipc {6}",
                    engineJs, relay, port, targetId, pin, quality, (chkUseTls.IsChecked == true ? "" : "--no-tls")),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(engineJs)
            };

            try
            {
                viewerProcess = new Process() { StartInfo = psi };
                viewerProcess.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Dispatcher.Invoke(new Action(() => HandleViewerIpc(e.Data)));
                    }
                };
                viewerProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Dispatcher.Invoke(new Action(() => AppendViewerLog("ERR: " + e.Data)));
                    }
                };

                viewerProcess.Start();
                viewerProcess.BeginOutputReadLine();
                viewerProcess.BeginErrorReadLine();

                btnConnect.Content = "🛑  Cancel Connection";
                btnConnect.Background = RoseRed;
                txtViewerStatus.Text = "Status: Contacting Relay...";
                AppendViewerLog("Connecting to Relay for partner [" + targetId + "]...");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to initiate viewer: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void HandleViewerIpc(string jsonLine)
        {
            try
            {
                var dict = json.Deserialize<System.Collections.Generic.Dictionary<string, object>>(jsonLine);
                if (!dict.ContainsKey("event")) return;

                string ev = dict["event"].ToString();
                switch (ev)
                {
                    case "VIEWER_STATUS":
                        if (dict.ContainsKey("text"))
                        {
                            txtViewerStatus.Text = "Status: " + dict["text"].ToString();
                            AppendViewerLog(dict["text"].ToString());
                        }
                        break;

                    case "VIEWER_READY":
                        AppendViewerLog("Secure tunnel ready on 127.0.0.1:" + dict["localPort"].ToString() + "!");
                        break;

                    case "VIEWER_LAUNCHED":
                        txtViewerStatus.Text = "Status: TightVNC Viewer Active!";
                        btnConnect.Content = "🛑  End Connection";
                        btnConnect.Background = RoseRed;
                        AppendViewerLog("TightVNC Viewer window launched! Remote session active.");
                        break;

                    case "VIEWER_SESSION_CLOSED":
                        txtViewerStatus.Text = "Status: Disconnected. Ready to reconnect.";
                        btnConnect.Content = "🚀  Reconnect to Remote PC";
                        btnConnect.Background = PrimaryIndigo;
                        AppendViewerLog("Viewer session closed. Ready to reconnect anytime with same ID & Password.");
                        break;

                    case "ERROR":
                        if (dict.ContainsKey("message"))
                        {
                            txtViewerStatus.Text = "Error: " + dict["message"].ToString();
                            btnConnect.Content = "🚀  Reconnect to Remote PC";
                            btnConnect.Background = PrimaryIndigo;
                            AppendViewerLog("ERROR: " + dict["message"].ToString());
                            MessageBox.Show(dict["message"].ToString(), "Connection Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                        break;
                }
            }
            catch
            {
                AppendViewerLog(jsonLine);
            }
        }

        private void AppendViewerLog(string line)
        {
            string ts = DateTime.Now.ToString("HH:mm:ss");
            if (txtViewerLog != null)
            {
                txtViewerLog.AppendText(string.Format("[{0}] {1}\r\n", ts, line));
                txtViewerLog.ScrollToEnd();
            }
        }

        // ===================================================================
        // RELAY PING & LOCAL RUNNER
        // ===================================================================
        private void PingRelayServer()
        {
            txtPingResult.Text = "Pinging relay...";
            txtPingResult.Foreground = AmberYellow;

            string engineJs = FindEngineScript();
            string nodeExe = FindNodeExecutable();
            string relay = txtRelayHost.Text.Trim();
            string port = txtControlPort.Text.Trim();

            Task.Factory.StartNew(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo()
                    {
                        FileName = nodeExe,
                        Arguments = string.Format("\"{0}\" ping --relay {1} --control-port {2} --ipc {3}",
                            engineJs, relay, port, (chkUseTls.IsChecked == true ? "" : "--no-tls")),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };
                    var p = Process.Start(psi);
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(4000);

                    Dispatcher.Invoke(new Action(() =>
                    {
                        if (output.Contains("PING_OK"))
                        {
                            txtPingResult.Text = "✅ Connected to Relay! Handshake successful.";
                            txtPingResult.Foreground = EmeraldGreen;
                        }
                        else
                        {
                            txtPingResult.Text = "❌ Could not reach Relay Server.";
                            txtPingResult.Foreground = RoseRed;
                        }
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(new Action(() =>
                    {
                        txtPingResult.Text = "Error: " + ex.Message;
                        txtPingResult.Foreground = RoseRed;
                    }));
                }
            });
        }

        private void EnsureRelayRunning(string cPort, string rPort)
        {
            try
            {
                bool isPortOpen = false;
                try
                {
                    using (var tcp = new TcpClient())
                    {
                        var ar = tcp.BeginConnect("127.0.0.1", int.Parse(cPort), null, null);
                        isPortOpen = ar.AsyncWaitHandle.WaitOne(300);
                        if (isPortOpen) tcp.EndConnect(ar);
                    }
                }
                catch { isPortOpen = false; }

                if (!isPortOpen)
                {
                    AppendHostLog("Local relay not detected. Starting built-in relay automatically...");
                    StartBuiltInRelay();
                    Thread.Sleep(700);
                }
            }
            catch { }
        }

        private void StartBuiltInRelay()
        {
            if (relayProcess != null && !relayProcess.HasExited) return;

            string engineJs = FindEngineScript();
            string nodeExe = FindNodeExecutable();
            string cPort = txtControlPort != null ? txtControlPort.Text.Trim() : settings.ControlPort;
            string rPort = txtDataPort != null ? txtDataPort.Text.Trim() : settings.RelayPort;

            var psi = new ProcessStartInfo()
            {
                FileName = nodeExe,
                Arguments = string.Format("\"{0}\" relay --control-port {1} --relay-port {2} --ipc", engineJs, cPort, rPort),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(engineJs)
            };

            try
            {
                relayProcess = Process.Start(psi);
                if (btnToggleLocalRelay != null)
                {
                    btnToggleLocalRelay.Content = "🛑  Stop Built-in Relay Server";
                    btnToggleLocalRelay.Background = RoseRed;
                }
                if (txtLocalRelayStatus != null)
                {
                    txtLocalRelayStatus.Text = string.Format("Relay Status: Running on Ports {0} & {1}", cPort, rPort);
                }
            }
            catch (Exception ex)
            {
                AppendHostLog("Could not start built-in relay: " + ex.Message);
            }
        }

        private void ToggleBuiltInRelay()
        {
            if (relayProcess != null && !relayProcess.HasExited)
            {
                try { relayProcess.Kill(); } catch { }
                relayProcess = null;
                btnToggleLocalRelay.Content = "🌐  Start Built-in Relay Server";
                btnToggleLocalRelay.Background = CyanBlue;
                txtLocalRelayStatus.Text = "Relay Status: Stopped";
                return;
            }

            StartBuiltInRelay();
        }

        private void SaveCurrentSettings()
        {
            if (settings != null)
            {
                if (txtMyDeviceId != null) settings.DeviceId = txtMyDeviceId.Text.Trim();
                if (txtMyPassword != null) settings.Password = txtMyPassword.Text.Trim();
                if (txtRelayHost != null) settings.RelayHost = txtRelayHost.Text.Trim();
                if (txtControlPort != null) settings.ControlPort = txtControlPort.Text.Trim();
                if (txtDataPort != null) settings.RelayPort = txtDataPort.Text.Trim();
                if (chkUseTls != null) settings.UseTls = (chkUseTls.IsChecked == true);
                if (txtTargetId != null) settings.LastTargetId = txtTargetId.Text.Trim();
                if (txtTargetPin != null) settings.LastTargetPassword = txtTargetPin.Text.Trim();
                settings.Save();
            }
        }

        // ===================================================================
        // UTILITIES & CLEANUP
        // ===================================================================
        private string FindNodeExecutable()
        {
            string bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime", "node.exe");
            if (File.Exists(bundled)) return bundled;

            string standard = @"C:\Program Files\nodejs\node.exe";
            if (File.Exists(standard)) return standard;

            return "node.exe";
        }

        private string FindEngineScript()
        {
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "engine", "vnc-engine.js");
            if (File.Exists(local)) return local;

            string poc = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "vnc-remote-core", "vnc-engine.js");
            if (File.Exists(poc)) return Path.GetFullPath(poc);

            string sameDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vnc-engine.js");
            if (File.Exists(sameDir)) return sameDir;

            return @"s:\gravity projects\VNC remote connect\vnc-remote-core\vnc-engine.js";
        }

        private void CleanupProcesses()
        {
            try { if (hostProcess != null && !hostProcess.HasExited) hostProcess.Kill(); } catch { }
            try { if (viewerProcess != null && !viewerProcess.HasExited) viewerProcess.Kill(); } catch { }
            try { if (relayProcess != null && !relayProcess.HasExited) relayProcess.Kill(); } catch { }
        }
    }

    public class App
    {
        [STAThread]
        public static void Main()
        {
            var app = new Application();
            var window = new MainWindow();
            app.Run(window);
        }
    }
}
