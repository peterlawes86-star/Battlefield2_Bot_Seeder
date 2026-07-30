using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace BF2BotManager
{
    public class BotDisplayModel
    {
        public string Nickname { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string CDKey { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public BF2BotClient? ClientInstance { get; set; }
    }

    public partial class MainWindow : Window
    {
        private BotConfig _config = new BotConfig();
        private readonly ObservableCollection<BotDisplayModel> _botModels = new ObservableCollection<BotDisplayModel>();
        private CancellationTokenSource? _sequentialStartCts;
        private BotDisplayModel? _editingBotModel;
        private CancellationTokenSource? _statusCheckCts;
        private bool _autoReconnectEnabled = false;

        public MainWindow()
        {
            InitializeComponent();
            DgBots.ItemsSource = _botModels;

            TxtLogFilePath.Text = $"Packet Log Path: {PacketLogger.LogFilePath}";

            if (File.Exists("client.xml"))
            {
                LoadXmlConfig("client.xml");
            }
        }

        private void LoadXmlConfig(string filePath)
        {
            try
            {
                _config = BotConfig.LoadFromFile(filePath);
                TxtAddress.Text = _config.Server.Address;
                TxtPort.Text = _config.Server.Port.ToString();
                TxtLoginServer.Text = string.IsNullOrWhiteSpace(_config.Server.LoginServer) ? "gpcm.gamespy.com" : _config.Server.LoginServer;
                TxtMod.Text = _config.Server.Mod;

                // Restore auto-reconnect state from config
                ChkAutoReconnect.IsChecked = _config.Server.AutoReconnect;

                _sequentialStartCts?.Cancel();
                foreach (var model in _botModels)
                {
                    model.ClientInstance?.Stop();
                }

                _botModels.Clear();

                // Sort clients by nickname (A-Z) before adding
                var sortedClients = _config.Server.Clients
                    .OrderBy(c => c.Nickname, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var clientCfg in sortedClients)
                {
                    var botClient = new BF2BotClient(clientCfg, _config.Server);
                    botClient.OnLogMessage += AppendLog;
                    botClient.OnStateChanged += BotClient_OnStateChanged;

                    _botModels.Add(new BotDisplayModel
                    {
                        Nickname = clientCfg.Nickname,
                        Password = clientCfg.Password,
                        CDKey = clientCfg.CDKey,
                        Status = ConnectionState.Disconnected.ToString(),
                        ClientInstance = botClient
                    });
                }

                AppendLog("SYSTEM", $"Loaded configuration with {_botModels.Count} bots (sorted A-Z).");
                PacketLogger.LogSystemEvent($"Loaded configuration with {_botModels.Count} bots from {filePath}.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load XML: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BotClient_OnStateChanged(string nickname, ConnectionState newState)
        {
            Dispatcher.Invoke(() =>
            {
                foreach (var model in _botModels)
                {
                    if (model.Nickname == nickname)
                    {
                        // Show "Stopped" instead of "Disconnected" if manually stopped
                        if (newState == ConnectionState.Disconnected && model.ClientInstance?.ManuallyStopped == true)
                            model.Status = "Stopped";
                        else
                            model.Status = newState.ToString();
                        DgBots.Items.Refresh();
                        break;
                    }
                }
            });
        }

        private void AppendLog(string sender, string message)
        {
            Dispatcher.Invoke(() =>
            {
                TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] [{sender}] {message}{Environment.NewLine}");
                TxtLog.ScrollToEnd();
            });
        }

        private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(TxtLog.Text))
            {
                Clipboard.SetText(TxtLog.Text);
                AppendLog("SYSTEM", "Console log copied to clipboard.");
            }
        }

        private void BtnAddOrUpdateBot_Click(object sender, RoutedEventArgs e)
        {
            string nick = TxtNewNickname.Text.Trim();
            string pass = TxtNewPassword.Text.Trim();
            string cdkey = TxtNewCDKey.Text.Trim();

            if (string.IsNullOrWhiteSpace(nick))
            {
                MessageBox.Show("Please enter a Nickname for the bot.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            UpdateServerConfigFromUI();

            if (_editingBotModel != null)
            {
                // Updating existing bot
                foreach (var model in _botModels)
                {
                    if (model != _editingBotModel && model.Nickname.Equals(nick, StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show($"A bot with nickname '{nick}' already exists in the list.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }

                _editingBotModel.ClientInstance?.Stop();

                _editingBotModel.Nickname = nick;
                _editingBotModel.Password = pass;
                _editingBotModel.CDKey = cdkey;

                var clientCfg = new ClientConfig
                {
                    Nickname = nick,
                    Password = pass,
                    CDKey = cdkey
                };

                var botClient = new BF2BotClient(clientCfg, _config.Server);
                botClient.OnLogMessage += AppendLog;
                botClient.OnStateChanged += BotClient_OnStateChanged;

                _editingBotModel.ClientInstance = botClient;
                _editingBotModel.Status = ConnectionState.Disconnected.ToString();

                DgBots.Items.Refresh();

                AppendLog("SYSTEM", $"Updated bot: {nick}");
                PacketLogger.LogSystemEvent($"Updated bot: {nick}");

                ResetAddEditForm();
            }
            else
            {
                // Adding new bot
                foreach (var model in _botModels)
                {
                    if (model.Nickname.Equals(nick, StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show($"A bot with nickname '{nick}' is already in the list.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }

                var clientCfg = new ClientConfig
                {
                    Nickname = nick,
                    Password = pass,
                    CDKey = cdkey
                };

                var botClient = new BF2BotClient(clientCfg, _config.Server);
                botClient.OnLogMessage += AppendLog;
                botClient.OnStateChanged += BotClient_OnStateChanged;

                _botModels.Add(new BotDisplayModel
                {
                    Nickname = nick,
                    Password = pass,
                    CDKey = cdkey,
                    Status = ConnectionState.Disconnected.ToString(),
                    ClientInstance = botClient
                });

                AppendLog("SYSTEM", $"Added bot: {nick}");
                PacketLogger.LogSystemEvent($"Added bot: {nick}");

                ResetAddEditForm();
            }
        }

        private void MenuEditBot_Click(object sender, RoutedEventArgs e)
        {
            if (DgBots.SelectedItem is BotDisplayModel selectedBot)
            {
                // Prevent editing a bot that is currently connecting
                if (selectedBot.ClientInstance != null)
                {
                    var s = selectedBot.ClientInstance.State;
                    if (s == ConnectionState.TcpLogin || s == ConnectionState.CdKeyAuth ||
                        s == ConnectionState.Connecting || s == ConnectionState.Handshake ||
                        s == ConnectionState.Reconnecting)
                    {
                        MessageBox.Show($"Cannot edit bot '{selectedBot.Nickname}' while it is connecting ({s}). Stop the bot first.",
                            "Edit Blocked", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }

                _editingBotModel = selectedBot;
                TxtNewNickname.Text = selectedBot.Nickname;
                TxtNewPassword.Text = selectedBot.Password;
                TxtNewCDKey.Text = selectedBot.CDKey;

                GrpAddEditBot.Header = "Edit Bot";
                BtnAddOrUpdateBot.Content = "Update Bot";

                AppendLog("SYSTEM", $"Editing bot: {selectedBot.Nickname}");
            }
        }

        private void ResetAddEditForm()
        {
            _editingBotModel = null;
            TxtNewNickname.Clear();
            TxtNewPassword.Clear();
            TxtNewCDKey.Clear();

            GrpAddEditBot.Header = "Add Bot";
            BtnAddOrUpdateBot.Content = "Add Bot";
        }

        private void MenuStartBot_Click(object sender, RoutedEventArgs e)
        {
            if (DgBots.SelectedItem is BotDisplayModel selectedBot && selectedBot.ClientInstance != null)
            {
                UpdateServerConfigFromUI();
                AppendLog("SYSTEM", $"Starting individual bot: {selectedBot.Nickname}...");
                _ = selectedBot.ClientInstance.StartAsync();
            }
        }

        private void MenuStopBot_Click(object sender, RoutedEventArgs e)
        {
            if (DgBots.SelectedItem is BotDisplayModel selectedBot)
            {
                AppendLog("SYSTEM", $"Stopping individual bot: {selectedBot.Nickname}...");
                selectedBot.ClientInstance?.Stop();
            }
        }

        private void MenuRemoveBot_Click(object sender, RoutedEventArgs e)
        {
            if (DgBots.SelectedItem is BotDisplayModel selectedBot)
            {
                if (selectedBot == _editingBotModel)
                {
                    ResetAddEditForm();
                }

                selectedBot.ClientInstance?.Stop();
                _botModels.Remove(selectedBot);
                AppendLog("SYSTEM", $"Removed bot {selectedBot.Nickname} from manager.");
            }
        }

        private async void BtnStartAll_Click(object sender, RoutedEventArgs e)
        {
            UpdateServerConfigFromUI();

            _sequentialStartCts?.Cancel();
            _sequentialStartCts = new CancellationTokenSource();
            var token = _sequentialStartCts.Token;

            AppendLog("SYSTEM", "Starting sequential bot connection sequence (1 bot at a time)...");
            PacketLogger.LogSystemEvent("Starting sequential bot connection sequence (1 bot at a time)");

            var botList = _botModels.ToList();

            foreach (var model in botList)
            {
                if (token.IsCancellationRequested)
                {
                    AppendLog("SYSTEM", "Sequential connection process stopped.");
                    break;
                }

                if (model.ClientInstance != null)
                {
                    if (model.ClientInstance.State == ConnectionState.Connected)
                    {
                        AppendLog("SYSTEM", $"Bot {model.Nickname} is already connected. Skipping...");
                        continue;
                    }

                    AppendLog("SYSTEM", $"Connecting bot {model.Nickname} (waiting for full connection before next)...");

                    // Run task asynchronously but await its connection task directly
                    var startTask = model.ClientInstance.StartAsync();

                    try
                    {
                        bool connected = await model.ClientInstance.WaitForConnectedAsync();
                        if (connected)
                        {
                            AppendLog("SYSTEM", $"Bot {model.Nickname} fully connected to game server! Proceeding to next bot...");
                            PacketLogger.LogSystemEvent($"Bot {model.Nickname} fully connected to game server. Proceeding to next bot.");
                        }
                        else
                        {
                            AppendLog("SYSTEM", $"Bot {model.Nickname} failed to connect. Proceeding to next bot...");
                            PacketLogger.LogSystemEvent($"Bot {model.Nickname} failed to connect. Proceeding to next bot.");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    try
                    {
                        await Task.Delay(2000, token); // 2 second delay between bots to allow master server registration
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            if (!token.IsCancellationRequested)
            {
                AppendLog("SYSTEM", "All bot connection attempts completed.");
                PacketLogger.LogSystemEvent("All bot connection attempts completed.");
            }
        }

        private void BtnStopAll_Click(object sender, RoutedEventArgs e)
        {
            _sequentialStartCts?.Cancel();
            AppendLog("SYSTEM", "Stopping all bots...");
            foreach (var model in _botModels)
            {
                model.ClientInstance?.Stop();
            }
        }

        private void UpdateServerConfigFromUI()
        {
            _config.Server.Address = TxtAddress.Text.Trim();
            if (int.TryParse(TxtPort.Text, out int port))
                _config.Server.Port = port;
            _config.Server.LoginServer = TxtLoginServer.Text.Trim();
            _config.Server.Mod = TxtMod.Text.Trim();
        }

        private void BtnLoadXml_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "XML Files (*.xml)|*.xml|All Files (*.*)|*.*",
                FileName = "client.xml"
            };

            if (dialog.ShowDialog() == true)
            {
                LoadXmlConfig(dialog.FileName);
            }
        }

        private void BtnSaveXml_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UpdateServerConfigFromUI();

                // Save auto-reconnect state
                _config.Server.AutoReconnect = ChkAutoReconnect.IsChecked == true;

                _config.Server.Clients.Clear();
                // Sort bots A-Z before saving
                var sortedModels = _botModels
                    .OrderBy(m => m.Nickname, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var model in sortedModels)
                {
                    _config.Server.Clients.Add(new ClientConfig
                    {
                        Nickname = model.Nickname,
                        Password = model.Password,
                        CDKey = model.CDKey
                    });
                }

                _config.SaveToFile("client.xml");
                MessageBox.Show("Configuration saved to client.xml successfully.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save XML: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnOpenLogFile_Click(object sender, RoutedEventArgs e)
        {
            if (File.Exists(PacketLogger.LogFilePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = PacketLogger.LogFilePath,
                    UseShellExecute = true
                });
            }
            else
            {
                MessageBox.Show("Packet log file has not been created yet.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // ============================================================================
        // Auto-Reconnect Toggle
        // ============================================================================
        private void ChkAutoReconnect_Changed(object sender, RoutedEventArgs e)
        {
            _autoReconnectEnabled = ChkAutoReconnect.IsChecked == true;

            if (_autoReconnectEnabled)
            {
                AppendLog("SYSTEM", "Auto-Reconnect ENABLED. Disconnected bots will automatically reconnect.");
                PacketLogger.LogSystemEvent("Auto-Reconnect ENABLED.");
                StartStatusChecker();
            }
            else
            {
                AppendLog("SYSTEM", "Auto-Reconnect DISABLED. Bots will stay disconnected.");
                PacketLogger.LogSystemEvent("Auto-Reconnect DISABLED.");
                StopStatusChecker();
            }
        }

        // ============================================================================
        // 30-Second Status Checker & Auto-Reconnect Loop (Sequential)
        // ============================================================================
        private void StartStatusChecker()
        {
            StopStatusChecker();
            _statusCheckCts = new CancellationTokenSource();
            var token = _statusCheckCts.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(30000, token);
                    }
                    catch (OperationCanceledException) { break; }

                    if (token.IsCancellationRequested) break;

                    // Collect bots needing reconnect on UI thread
                    List<BotDisplayModel> disconnectedToReconnect = new();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        foreach (var model in _botModels)
                        {
                            if (model.ClientInstance == null) continue;

                            var actualState = model.ClientInstance.State;
                            var displayedStatus = model.Status;

                            // Update displayed status if it doesn't match actual state
                            if (displayedStatus != actualState.ToString())
                            {
                                model.Status = actualState.ToString();
                                DgBots.Items.Refresh();
                            }

                            // Only auto-reconnect bots that were NOT manually stopped
                            if (_autoReconnectEnabled &&
                                !model.ClientInstance.ManuallyStopped &&
                                (actualState == ConnectionState.Disconnected || actualState == ConnectionState.Error))
                            {
                                disconnectedToReconnect.Add(model);
                            }
                        }
                    });

                    // Reconnect sequentially, one at a time
                    foreach (var model in disconnectedToReconnect)
                    {
                        if (token.IsCancellationRequested) break;
                        if (model.ClientInstance == null) continue;

                        AppendLog("SYSTEM", $"Auto-reconnecting bot: {model.Nickname}...");
                        PacketLogger.LogSystemEvent($"Auto-reconnecting bot: {model.Nickname} (state was {model.ClientInstance.State})");

                        _ = model.ClientInstance.StartAsync();

                        try
                        {
                            bool connected = await model.ClientInstance.WaitForConnectedAsync();
                            if (connected)
                            {
                                AppendLog("SYSTEM", $"Bot {model.Nickname} auto-reconnected successfully.");
                                PacketLogger.LogSystemEvent($"Bot {model.Nickname} auto-reconnected successfully.");
                            }
                            else
                            {
                                AppendLog("SYSTEM", $"Bot {model.Nickname} auto-reconnect failed.");
                                PacketLogger.LogSystemEvent($"Bot {model.Nickname} auto-reconnect failed.");
                            }
                        }
                        catch (OperationCanceledException) { break; }

                        try
                        {
                            await Task.Delay(2000, token);
                        }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }, token);
        }

        private void StopStatusChecker()
        {
            _statusCheckCts?.Cancel();
            _statusCheckCts = null;
        }
    }
}