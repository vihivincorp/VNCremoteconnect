using System;
using System.Diagnostics;
using System.IO;
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


namespace VNCRemoteConnect
{
    public class MainWindow : Window
    {
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

        // Active State
        private Process hostProcess = null;
        private Process viewerProcess = null;
        private Process relayProcess = null;
        private JavaScriptSerializer json = new JavaScriptSerializer();

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

        // UI Controls - Host Tab
        private TextBlock txtMyDeviceId;
        private TextBox txtMyPin;
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

        public MainWindow()
        {
            this.Title = "VNC Remote Connect - Secure Cross-Network Desktop";
            this.Width = 860;
            this.Height = 640;
            this.MinWidth = 800;
            this.MinHeight = 580;
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            this.Background = BgBrush;
            this.Foreground = TextWhite;
            this.FontFamily = new FontFamily("Segoe UI");

            BuildUI();

            this.Closing += (s, e) => CleanupProcesses();
            CheckTightVncServerInstallation();
        }


        private void BuildUI()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(64) }); // Header
            root.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(48) }); // Tab bar
            root.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(1, GridUnitType.Star) }); // Content
            root.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(32) }); // Footer

            // 1. Header
            var header = CreateHeader();
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // 2. Tab Bar
            var tabBar = CreateTabBar();
            Grid.SetRow(tabBar, 1);
            root.Children.Add(tabBar);

            // 3. Tab Contents
            var contentContainer = new Grid();
            Grid.SetRow(contentContainer, 2);
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
            Grid.SetRow(footer, 3);
            root.Children.Add(footer);

            this.Content = root;

            // Show default tab
            SwitchTab(0);
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
            var logoText = new TextBlock() { Text = "VNC", FontWeight = FontWeights.Bold, FontSize = 16, Foreground = TextWhite };
            logoBadge.Child = logoText;
            brandStack.Children.Add(logoBadge);

            var titleStack = new StackPanel() { VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock() { Text = "VNC Remote Connect", FontWeight = FontWeights.SemiBold, FontSize = 18, Foreground = TextWhite };
            var subtitle = new TextBlock() { Text = "Cross-Network Desktop • Zero-Knowledge E2EE • TightVNC Engine", FontSize = 11, Foreground = TextMuted };
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
            statusDot = new Ellipse() { Width = 8, Height = 8, Fill = TextDim, Margin = new Thickness(0, 0, 8, 0) };
            statusPill = new TextBlock() { Text = "Ready", FontSize = 12, FontWeight = FontWeights.Medium, Foreground = TextMuted };
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

            tabHostBtn = CreateTabButton("  💻  Share Screen (Host)", 0);
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
        // TAB 1: HOST PANEL (SHARE SCREEN)
// ===================================================================
        private Grid CreateHostPanel()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1.1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(20) }); // Spacing
            grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1.0, GridUnitType.Star) });

            // Left Card: Credentials & Controls
            var leftCard = CreateCard("Allow Remote Access (This PC)");
            var leftStack = new StackPanel();

            // Device ID Box
            leftStack.Children.Add(new TextBlock() { Text = "YOUR DEVICE ID", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = TextMuted, Margin = new Thickness(0, 8, 0, 4) });
            var idContainer = new Border()
            {
                Background = InputBg,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10)
            };
            var idStack = new Grid();
            idStack.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            idStack.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });

            txtMyDeviceId = new TextBlock()
            {
                Text = "Click 'Start Sharing' to obtain ID",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = PrimaryIndigo,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(txtMyDeviceId, 0);
            idStack.Children.Add(txtMyDeviceId);

            var btnCopyId = CreateIconButton("📋 Copy", () =>
            {
                if (!string.IsNullOrEmpty(txtMyDeviceId.Text) && !txtMyDeviceId.Text.Contains("Click"))
                {
                    Clipboard.SetText(txtMyDeviceId.Text);
                    MessageBox.Show("Device ID copied to clipboard!", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            });
            Grid.SetColumn(btnCopyId, 1);
            idStack.Children.Add(btnCopyId);
            idContainer.Child = idStack;
            leftStack.Children.Add(idContainer);

            // Session PIN Box
            leftStack.Children.Add(new TextBlock() { Text = "ONE-TIME SESSION PIN", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = TextMuted, Margin = new Thickness(0, 16, 0, 4) });
            var pinContainer = new Border()
            {
                Background = InputBg,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 6, 12, 6)
            };
            var pinStack = new Grid();
            pinStack.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            pinStack.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });
            pinStack.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });

            txtMyPin = new TextBox()
            {
                Text = GenerateRandomPin(),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = EmeraldGreen,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(txtMyPin, 0);
            pinStack.Children.Add(txtMyPin);

            var btnRefreshPin = CreateIconButton("🔄 New", () => { txtMyPin.Text = GenerateRandomPin(); });
            btnRefreshPin.Margin = new Thickness(4, 0, 4, 0);
            Grid.SetColumn(btnRefreshPin, 1);
            pinStack.Children.Add(btnRefreshPin);

            var btnCopyPin = CreateIconButton("📋 Copy", () =>
            {
                Clipboard.SetText(txtMyPin.Text);
                MessageBox.Show("Session PIN copied to clipboard!", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
            });
            Grid.SetColumn(btnCopyPin, 2);
            pinStack.Children.Add(btnCopyPin);
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
                Margin = new Thickness(0, 16, 0, 16)
            };
            txtTvnServerStatus = new TextBlock()
            {
                Text = "TightVNC Server: Checking...",
                FontSize = 11,
                Foreground = TextMuted
            };
            vncEngineBox.Child = txtTvnServerStatus;
            leftStack.Children.Add(vncEngineBox);

            // Big Start/Stop Button
            btnToggleHost = new Button()
            {
                Content = "🟢  Start Sharing (Allow Connections)",
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
                Text = "Status: Idle (Not sharing)",
                FontSize = 12,
                Foreground = TextMuted,
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
                Text = "🔒 End-to-End Encrypted: X25519 ECDH + AES-256-GCM AEAD.\nNo unencrypted desktop pixels or keystrokes touch the relay.",
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
                Text = "VNC Remote Connect Host Ready.\nConfigure PIN above and click 'Start Sharing' to connect.\n"
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
            grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1.1, GridUnitType.Star) });
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
                Text = ""
            };
            leftStack.Children.Add(txtTargetId);

            leftStack.Children.Add(new TextBlock() { Text = "SESSION PIN", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = TextMuted, Margin = new Thickness(0, 16, 0, 4) });
            txtTargetPin = new TextBox()
            {
                Background = InputBg,
                Foreground = TextWhite,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                FontSize = 15,
                Padding = new Thickness(10, 8, 10, 8),
                Text = ""
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
            cmbQuality.Items.Add(new ComboBoxItem() { Content = "⚡ Fast (College / Mobile Wi-Fi - Optimized)", Tag = "fast", IsSelected = true });
            cmbQuality.Items.Add(new ComboBoxItem() { Content = "⚖️ Balanced (Recommended for Home/Broadband)", Tag = "balanced" });
            cmbQuality.Items.Add(new ComboBoxItem() { Content = "💎 Maximum Quality (Lossless Colors)", Tag = "best" });
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
                Text = "Status: Idle",
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
            var rightCard = CreateCard("Connection Progress");
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
                Text = "Once verified, VNC Remote Connect establishes an authenticated E2EE tunnel and automatically launches TightVNC Viewer window.",
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
                Text = "Enter the Partner Device ID and PIN to start session.\n"
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
            grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1.1, GridUnitType.Star) });
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
                Text = "127.0.0.1" // Default test relay
            };
            leftStack.Children.Add(txtRelayHost);

            var portGrid = new Grid() { Margin = new Thickness(0, 12, 0, 0) };
            portGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            portGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(12) });
            portGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });

            var p1Stack = new StackPanel();
            p1Stack.Children.Add(new TextBlock() { Text = "CONTROL PORT", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = TextMuted, Margin = new Thickness(0, 0, 0, 4) });
            txtControlPort = new TextBox() { Background = InputBg, Foreground = TextWhite, BorderBrush = CardBorder, FontSize = 14, Padding = new Thickness(8, 6, 8, 6), Text = "8443" };
            p1Stack.Children.Add(txtControlPort);
            Grid.SetColumn(p1Stack, 0);
            portGrid.Children.Add(p1Stack);

            var p2Stack = new StackPanel();
            p2Stack.Children.Add(new TextBlock() { Text = "RELAY DATA PORT", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = TextMuted, Margin = new Thickness(0, 0, 0, 4) });
            txtDataPort = new TextBox() { Background = InputBg, Foreground = TextWhite, BorderBrush = CardBorder, FontSize = 14, Padding = new Thickness(8, 6, 8, 6), Text = "8444" };
            p2Stack.Children.Add(txtDataPort);
            Grid.SetColumn(p2Stack, 2);
            portGrid.Children.Add(p2Stack);

            leftStack.Children.Add(portGrid);

            chkUseTls = new CheckBox()
            {
                Content = "Use TLS Transport Encryption (Encapsulated)",
                Foreground = TextWhite,
                IsChecked = true,
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
                Text = "You can turn this PC into a private Relay Server with 1 click!\nFriends or laptops on your Wi-Fi or hotspot can connect by setting your IP address as the Relay Host.",
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
        // TAB 4: ABOUT & LICENSING
        // ===================================================================
        private Grid CreateAboutPanel()
        {
            var grid = new Grid();
            var card = CreateCard("About VNC Remote Connect & Open-Source Compliance");
            var stack = new StackPanel();

            var heading = new TextBlock()
            {
                Text = "VNC Remote Connect - Windows Remote Desktop Solution",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = PrimaryIndigo,
                Margin = new Thickness(0, 0, 0, 8)
            };
            stack.Children.Add(heading);

            var desc = new TextBlock()
            {
                Text = "VNC Remote Connect is an open, private remote desktop system engineered for low-latency desktop control across NATs, firewalls, and campus networks without port forwarding.\n\n" +
                       "Key Architectural Components:\n" +
                       "• Zero-Knowledge Relay Architecture: The rendezvous and relay servers act solely as blind byte conduits. They cannot inspect, record, or tamper with screen frames or input.\n" +
                       "• Authenticated End-to-End Encryption: Ephemeral X25519 Elliptic-Curve Diffie-Hellman combined with SHA-256 HKDF and AES-256-GCM authenticated encryption.\n" +
                       "• Reverse Loopback Bridge: TightVNC Server is strictly isolated from the external network and commanded over loopback, preventing unauthorized external exposure.\n\n" +
                       "TightVNC License Compliance Notice:\n" +
                       "VNC Remote Connect integrates with TightVNC 2.8.90, distributed under the GNU General Public License (GPL v2). Upstream copyright belongs to GlavSoft LLC and original authors. Full source code, build scripts, and license texts are included in this distribution in accordance with GPL v2 Section 3.",
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
                Text = "VNC Remote Connect v1.0.0 • 256-bit AES-GCM • GPL v2 Licensed • Native Windows",
                FontSize = 11,
                Foreground = TextDim,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            footerGrid.Children.Add(text);
            return footerGrid;
        }

        private string GenerateRandomPin()
        {
            var r = new Random();
            return r.Next(100000, 999999).ToString();
        }

        private void CheckTightVncServerInstallation()
        {
            string installedPath = @"C:\Program Files\TightVNC\tvnserver.exe";
            string portablePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vnc", "tvnserver.exe");
            string binPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "tightvnc-bin", "WinVNC.exe");

            if (File.Exists(installedPath))
            {
                txtTvnServerStatus.Text = "✅ TightVNC Server: Detected (Installed Service)";
                txtTvnServerStatus.Foreground = EmeraldGreen;
            }
            else if (File.Exists(portablePath) || File.Exists(binPath))
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
        // HOST PROCESS LIFECYCLE
        // ===================================================================
        private void ToggleHostSharing()
        {
            if (hostProcess != null && !hostProcess.HasExited)
            {
                // Stop sharing
                try { hostProcess.Kill(); } catch { }
                hostProcess = null;
                btnToggleHost.Content = "🟢  Start Sharing (Allow Connections)";
                btnToggleHost.Background = EmeraldGreen;
                txtHostStatus.Text = "Status: Idle";
                statusDot.Fill = TextDim;
                statusPill.Text = "Ready";
                AppendHostLog("Hosting stopped.");
                return;
            }

            // Start sharing
            string engineJs = FindEngineScript();
            string nodeExe = FindNodeExecutable();

            if (string.IsNullOrEmpty(nodeExe))
            {
                MessageBox.Show("Node.js runtime was not found. Please ensure Node.js is installed or bundled.", "Runtime Missing", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string relay = txtRelayHost.Text.Trim();
            string port = txtControlPort.Text.Trim();
            string pin = txtMyPin.Text.Trim();

            var psi = new ProcessStartInfo()
            {
                FileName = nodeExe,
                Arguments = string.Format("\"{0}\" host --relay {1} --control-port {2} --pin {3} --ipc {4}",
                    engineJs, relay, port, pin, (chkUseTls.IsChecked == true ? "" : "--no-tls")),
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

                hostProcess.Start();
                hostProcess.BeginOutputReadLine();
                hostProcess.BeginErrorReadLine();

                btnToggleHost.Content = "🛑  Stop Sharing";
                btnToggleHost.Background = RoseRed;
                txtHostStatus.Text = "Status: Connecting to Relay...";
                statusDot.Fill = AmberYellow;
                statusPill.Text = "Connecting...";
                AppendHostLog("Connecting to Relay Server at " + relay + ":" + port + "...");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to start host agent: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
                        string devId = dict["deviceId"].ToString();
                        txtMyDeviceId.Text = devId;
                        txtHostStatus.Text = "Status: Online & Ready for Connections";
                        statusDot.Fill = EmeraldGreen;
                        statusPill.Text = "Online (Sharing)";
                        AppendHostLog("Host Online! Registered Device ID: " + devId);
                        break;

                    case "HOST_INCOMING":
                        AppendHostLog("Incoming connection request from remote partner...");
                        break;

                    case "HOST_SESSION_CONNECTED":
                        txtHostStatus.Text = "Status: Client Connected! (E2EE Active)";
                        statusDot.Fill = CyanBlue;
                        statusPill.Text = "In Session";
                        AppendHostLog(">>> Partner connected! Encrypted desktop stream active. <<<");
                        break;

                    case "HOST_SESSION_CLOSED":
                        txtHostStatus.Text = "Status: Online & Ready for Connections";
                        statusDot.Fill = EmeraldGreen;
                        statusPill.Text = "Online (Sharing)";
                        AppendHostLog("Partner session disconnected. Waiting for next connection.");
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
            txtHostLog.AppendText(string.Format("[{0}] {1}\r\n", ts, line));
            txtHostLog.ScrollToEnd();
        }

        // ===================================================================
        // VIEWER PROCESS LIFECYCLE
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
                AppendViewerLog("Viewer session cancelled.");
                return;
            }

            string targetId = txtTargetId.Text.Trim();
            string pin = txtTargetPin.Text.Trim();

            if (string.IsNullOrEmpty(targetId) || string.IsNullOrEmpty(pin))
            {
                MessageBox.Show("Please enter both Partner Device ID and Session PIN.", "Required Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

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

            var psi = new ProcessStartInfo()
            {
                FileName = nodeExe,
                Arguments = string.Format("\"{0}\" viewer --relay {1} --control-port {2} --target {3} --pin {4} --quality {5} --ipc {6}",
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
                        AppendViewerLog("TightVNC Viewer window launched! Remote session active.");
                        break;

                    case "VIEWER_SESSION_CLOSED":
                        txtViewerStatus.Text = "Status: Session Closed";
                        btnConnect.Content = "🚀  Connect to Remote PC";
                        btnConnect.Background = PrimaryIndigo;
                        AppendViewerLog("Viewer session closed.");
                        break;

                    case "ERROR":
                        if (dict.ContainsKey("message"))
                        {
                            txtViewerStatus.Text = "Error: " + dict["message"].ToString();
                            btnConnect.Content = "🚀  Connect to Remote PC";
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
            txtViewerLog.AppendText(string.Format("[{0}] {1}\r\n", ts, line));
            txtViewerLog.ScrollToEnd();
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

            string engineJs = FindEngineScript();
            string nodeExe = FindNodeExecutable();
            string cPort = txtControlPort.Text.Trim();
            string rPort = txtDataPort.Text.Trim();

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
                btnToggleLocalRelay.Content = "🛑  Stop Built-in Relay Server";
                btnToggleLocalRelay.Background = RoseRed;
                txtLocalRelayStatus.Text = string.Format("Relay Status: Running on Ports {0} & {1}", cPort, rPort);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not start local relay: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "engine", "msk-engine.js");
            if (File.Exists(local)) return local;

            string poc = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "msk-remote-poc", "msk-engine.js");
            if (File.Exists(poc)) return Path.GetFullPath(poc);

            string sameDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "msk-engine.js");
            if (File.Exists(sameDir)) return sameDir;

            return @"s:\gravity projects\VNC remote connect\msk-remote-poc\msk-engine.js";
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
