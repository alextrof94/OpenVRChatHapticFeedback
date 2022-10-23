﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

using TwitchLib;
using TwitchLib.Client.Models;
using TwitchLib.Client.Events;

namespace OpenVRChatHapticFeedback
{
    public partial class MainWindow : Window
    {
        private static Mutex _mutex = null;
        private readonly static string DEFAULT_KEY_LABEL = "Unbound: Click to bind keys to simulate";
        private MainController _controller;
        private List<BindingItem> _items = new List<BindingItem>();
        private object _activeElement;
        private string _currentlyRunningAppId = MainModel.CONFIG_DEFAULT;
        private System.Windows.Forms.NotifyIcon _notifyIcon;
        private HashSet<string> _activeKeys = new HashSet<string>();
        private bool _initDone = false;
        private bool _dashboardIsVisible = false;

        private bool loaded = false;

        List<HapticAnimation> animations;

        public TwitchLib.Client.TwitchClient client;

        public MainWindow()
        {
            InitWindow();
            InitializeComponent();
            //Title = Properties.Resources.AppName;

            // Prevent multiple instances running at once
            _mutex = new Mutex(true, Properties.Resources.AppName, out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show(
                    Application.Current.MainWindow,
                    "This application is already running!",
                    Properties.Resources.AppName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
                Application.Current.Shutdown();
            }


            _controller = new MainController
            {
                // Reports on the status of OpenVR
                StatusUpdateAction = (connected) =>
                {
                    Debug.WriteLine($"Status Update Action: connected={connected}");
                    var message = connected ? "Connected" : "Disconnected";
                    var color = connected ? Brushes.OliveDrab : Brushes.Tomato;
                    Dispatcher.Invoke(() =>
                    {
                        Label_OpenVR.Content = message;
                        Label_OpenVR.Background = color;
                        if (!connected && _initDone && (bool)MainModel.LoadSetting(MainModel.Setting.ExitWithSteam)) {
                            if (_notifyIcon != null) _notifyIcon.Dispose();
                            System.Windows.Application.Current.Shutdown();
                        }
                    });
                },

                // Triggered when a new scene app is detected
                AppUpdateAction = (appId) =>
                {
                    Debug.WriteLine($"App Update Action: appId={appId}");
                    _currentlyRunningAppId = appId;
                    var color = Brushes.OliveDrab;
                    if (appId == MainModel.CONFIG_DEFAULT)
                    {
                        color = Brushes.Gray;
                    }
                    var appIdFixed = appId.Replace("_", "__"); // Single underscores makes underlined chars
                    Dispatcher.Invoke(() =>
                    {
                        Debug.WriteLine($"Setting AppID to: {appId}");
                        Label_Application.Content = appIdFixed;
                        Label_Application.Background = color;
                    });
                },

                // We should update the text on the current binding we are recording
                KeyTextUpdateAction = (keyText, cancel) =>
                {
                    Debug.WriteLine($"Key Text Update Action: keyText={keyText}");
                    Dispatcher.Invoke(() =>
                    {
                        if (_activeElement != null)
                        {
                            (_activeElement as Label).Content = keyText;
                            if (cancel) UpdateLabel(_activeElement as Label, false);
                        }
                    });
                },

                // We have loaded a config
                ConfigRetrievedAction = (config, forceButtonOff) =>
                {
                    var loaded = config != null;
                    if (loaded) Debug.WriteLine($"Config Retrieved Action: count()={config.Count}");
                    Dispatcher.Invoke(() =>
                    {
                        if (loaded) InitList(config);
                    });
                },

                KeyActivatedAction = (key, on) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (!_dashboardIsVisible) {
                            if (on) _activeKeys.Add(key);
                            else _activeKeys.Remove(key);
                        }
                    });
                },

                // Dashboard Visible
                DashboardVisibleAction = (visible) => {
                    Dispatcher.Invoke(() =>
                    {
                        _dashboardIsVisible = visible;
                    });
                }
            };

            // Receives error messages from OpenVR
            _controller.SetDebugLogAction((message) =>
            {
                Dispatcher.Invoke(() =>
                {
                    WriteToLog(message);
                });
            });

            // Init the things
            var actionKeys = InitList();
            _controller.Init(actionKeys);
            InitSettings();
            InitTrayIcon();
            _initDone = true;
        }

        private void ClientConnect()
        {
            if (client != null && client.IsConnected)
                client.Disconnect();

            ConnectionCredentials creds = new ConnectionCredentials(Secrets.bot_name, Secrets.bot_access_token);
            client = new TwitchLib.Client.TwitchClient();
            client.Initialize(creds, TextBoxChannelName.Text);

            client.OnMessageReceived += MyMessageReceivedFunction;

            client.Connect();
        }

		private void MyMessageReceivedFunction(object sender, OnMessageReceivedArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (cbShowMessages.IsChecked == true)
                    WriteToLog(e.ChatMessage.Username + ": " + e.ChatMessage.Message);

                bool needStartHaptics = false;
                var notifyMode = (NotifyMode)ComboBoxFeedbackBy.SelectedIndex;
                var channel_name = (string)TextBoxChannelName.Text;

                if (e.ChatMessage.Message[0] == '!' && e.ChatMessage.Message != "!hey")
                    return;

                switch (notifyMode)
                {
                    case NotifyMode.HEY:
                        if (e.ChatMessage.Message == "!hey")
                            needStartHaptics = true;
                        break;
                    case NotifyMode.MODERATORS:
                        if (e.ChatMessage.Username == channel_name || e.ChatMessage.IsMe || e.ChatMessage.IsBroadcaster || e.ChatMessage.IsModerator)
                            needStartHaptics = true;
                        break;
                    case NotifyMode.SUBSCRIBERS:
                        if (e.ChatMessage.Username == channel_name || e.ChatMessage.IsMe || e.ChatMessage.IsBroadcaster || e.ChatMessage.IsModerator || e.ChatMessage.IsSubscriber)
                            needStartHaptics = true;
                        break;
                    case NotifyMode.ALL_MESSAGES:
                        needStartHaptics = true;
                        break;
                }

                if (needStartHaptics)
                    _controller.PlayHaptic(animations[ComboBoxFeedbackType.SelectedIndex]);
            });
        }

		private void WriteToLog(String message)
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            var oldLog = TextBox_Log.Text;
            var lines = oldLog.Split('\n');
            Array.Resize(ref lines, 3);
            var newLog = string.Join("\n", lines);
            TextBox_Log.Text = $"{time}: {message}\n{newLog}";
        }

        private void InitWindow()
        {
            var shouldMinimize = (bool)MainModel.LoadSetting(MainModel.Setting.Minimize);
            var onlyInTray = (bool)MainModel.LoadSetting(MainModel.Setting.Tray);

            if (shouldMinimize)
            {
                Hide();
                WindowState = WindowState.Minimized;
                ShowInTaskbar = !onlyInTray;
            }
        }

        private void InitTrayIcon()
        {
            var icon = Properties.Resources.icon.Clone() as System.Drawing.Icon;
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            _notifyIcon.Click += NotifyIcon_Click;
            _notifyIcon.Text = $"Click to show the {Properties.Resources.AppName} window";
            _notifyIcon.Icon = icon;
            _notifyIcon.Visible = true;            
        }

        #region bindings

        // Fill list with entries
        private string[] InitList(Dictionary<string, Key[]> config = null)
        {
            var actionKeys = new List<string>();
            actionKeys.AddRange(GenerateActionKeyRange(16, 'L')); // Left
            actionKeys.AddRange(GenerateActionKeyRange(16, 'R')); // Right
            actionKeys.AddRange(GenerateActionKeyRange(16, 'C')); // Chord
            actionKeys.AddRange(GenerateActionKeyRange(16, 'T')); // Tracker
            string[] GenerateActionKeyRange(int count, char type)
            {
                var keys = new List<string>();
                for (var i = 1; i <= count; i++) keys.Add($"{type}{i}");
                return keys.ToArray();
            }

            if (config == null) config = new Dictionary<string, Key[]>();
            _items.Clear();
            foreach (var actionKey in actionKeys)
            {
                var text = config.ContainsKey(actionKey) ? _controller.GetKeysLabel(config[actionKey]) : string.Empty;
                if (text == string.Empty) text = DEFAULT_KEY_LABEL;
                _items.Add(new BindingItem()
                {
                    Key = actionKey,
                    Label = $"Key {actionKey}",
                    Text = text
                });
            }

            return actionKeys.ToArray();
        }

        // Binding data class
        public class BindingItem
        {
            public string Key { get; set; }
            public string Label { get; set; }
            public string Text { get; set; }
        }
        #endregion

        #region events
        // All key down events in the app
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            /*
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (!_controller.OnKeyDown(key))
            {
                string message = $"Key not mapped: " + key.ToString();
                WriteToLog(message);
            }
            */
        }

        // All key up events in the app
        private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            _controller.OnKeyUp(key);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _notifyIcon.Dispose();
            base.OnClosing(e);
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            var onlyInTray = (bool)MainModel.LoadSetting(MainModel.Setting.Tray);
            switch (WindowState)
            {
                case WindowState.Minimized: 
                    ShowInTaskbar = !onlyInTray;
                    break;
                default:
                    ShowInTaskbar = true;
                    Show();
                    break;
            }
        }

        #endregion

        #region actions


        private void Button_Folder_Click(object sender, RoutedEventArgs e)
        {
            _controller.OpenConfigFolder();
        }
        #endregion

        #region bindings
        // Main action that is clicked from the list to start and end registration of keys
        private void Label_RecordSave_Click(object sender, MouseButtonEventArgs e)
        {
            var element = sender as Label;
            var dataItem = element.DataContext as BindingItem;
            var active = _controller.ToggleRegisteringKey(dataItem.Key, element, out object activeElement);
            UpdateLabel(activeElement as Label, active);
            if (active) _activeElement = activeElement;
            else _activeElement = null;
        }

        private void UpdateLabel(Label label, bool active)
        {
            {
                label.Foreground = active ? Brushes.DarkRed : Brushes.Black;
                label.BorderBrush = active ? Brushes.Tomato : Brushes.DarkGray;
                label.Background = active ? Brushes.LightPink : Brushes.LightGray;
            }
        }

        private void Label_HighlightOn(object sender, RoutedEventArgs e)
        {
            if (_activeElement != sender) (sender as Label).Background = Brushes.WhiteSmoke;
        }

        private void Label_HighlightOff(object sender, RoutedEventArgs e)
        {
            if (_activeElement != sender) (sender as Label).Background = Brushes.LightGray;
        }

        // Clear the current binding
        private void Button_ClearCancel_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var dataItem = button.DataContext as BindingItem;
            MainModel.RemoveBinding(dataItem.Key);
            DockPanel sp = VisualTreeHelper.GetParent(button) as DockPanel;
            var element = sp.Children[2] as Label;
            element.Content = DEFAULT_KEY_LABEL;
        }
        #endregion

        #region settings
        // Load settings and apply them to the checkboxes
        private void InitSettings()
        {
            CheckBox_Minimize.IsChecked = (bool)MainModel.LoadSetting(MainModel.Setting.Minimize);
            CheckBox_Tray.IsChecked = (bool)MainModel.LoadSetting(MainModel.Setting.Tray);
            CheckBox_ExitWithSteamVR.IsChecked = (bool)MainModel.LoadSetting(MainModel.Setting.ExitWithSteam);

            ComboBoxFeedbackBy.SelectedIndex = (int)MainModel.LoadSetting(MainModel.Setting.FeedbackBy);
            ComboBoxWhichController.SelectedIndex = (int)MainModel.LoadSetting(MainModel.Setting.WhichController);
            TextBoxChannelName.Text = (string)MainModel.LoadSetting(MainModel.Setting.ChannelName);

            try
            {
                string json = "";
                if (System.IO.File.Exists("HapticAnimations.json"))
                {
                    json = System.IO.File.ReadAllText("HapticAnimations.json");
                }
                else
                {
                    json = "[ { \"name\": \"бз-бз бз-бз бз-бз\", \"hapticFrames\": [ { \"duration\": 100, \"delayAfterPlay\": 100 }, { \"duration\": 100, \"delayAfterPlay\": 200 }, { \"duration\": 100, \"delayAfterPlay\": 100 }, { \"duration\": 100, \"delayAfterPlay\": 200 }, { \"duration\": 100, \"delayAfterPlay\": 100 }, { \"duration\": 100, \"delayAfterPlay\": 0 } ] }, { \"name\": \"бз бз бзз\", \"hapticFrames\": [ { \"duration\": 100, \"delayAfterPlay\": 200 }, { \"duration\": 100, \"delayAfterPlay\": 200 }, { \"duration\": 200, \"delayAfterPlay\": 0 } ] }, { \"name\": \"бззззз\", \"hapticFrames\": [ { \"duration\": 500, \"delayAfterPlay\": 0 } ] } ]";
                    System.IO.File.WriteAllText("HapticAnimations.json", json);
                }
                animations = JsonConvert.DeserializeObject<List<HapticAnimation>>(json);

                ComboBoxFeedbackType.Items.Clear();
                foreach (var anim in animations)
				{
                    ComboBoxFeedbackType.Items.Add(anim.name);
                }
                ClientConnect();
            }
            catch (Exception ex)
			{
                WriteToLog(ex.Message);
            }

            ComboBoxFeedbackType.SelectedIndex = (int)MainModel.LoadSetting(MainModel.Setting.FeedbackType);
            if (ComboBoxFeedbackType.SelectedIndex < -1 || ComboBoxFeedbackType.SelectedIndex > animations.Count)
                ComboBoxFeedbackType.SelectedIndex = 0;
            _controller.PlayHaptic(animations[ComboBoxFeedbackType.SelectedIndex]);

            SwitchLanguage();

#if DEBUG
            Label_Version.Content = $"{MainModel.GetVersion()}";
#else
            Label_Version.Content = MainModel.GetVersion();
#endif
        }

		private void SwitchLanguage()
		{
            if ((bool)MainModel.LoadSetting(MainModel.Setting.English))
            {
                GbSettings.Header = "App settings";
                CheckBox_Minimize.Content = "Minimize on start";
                CheckBox_Tray.Content = "in tray";
                CheckBox_ExitWithSteamVR.Content = "Exit with SteamVr";
                LinkIssue.Inlines.Clear(); LinkIssue.Inlines.Add("Issue");
                ButtonLanguage.Content = "Переключиться на РУССКИЙ";

                GbStatus.Header = "Status";
                LabelApp.Content = "Current App Id";
                LabelStatus.Content = "OpenVR status";

                GbTwitch.Header = "Twitch settings";
                LabelChannelName.Content = "Channel name";
                LabelFeedbackBy.Content = "Haptic BY";
                LabelFeedbackType.Content = "Haptic TYPE";
                LabelWhichController.Content = "On controller";
                TwitchTestButton.Content = "TEST";
                ButtonConnect.Content = "Save & connect";

                GbAbout.Header = "About";
                tb1.Text = "Created by russian guy with twitch channel: GoodVrGames";
                tb2.Text = "It's free. If you want to thank me, just say that you are using this application written by a simple russian guy if asked.";
                tb3.Text = "I understand english text, so you can also visit my channel and ask me anything.";
                tb4.Text = "";

                GbLog.Header = "Log";
                cbShowMessages.Content = "Show messages in log";
            } 
            else
            {
                GbSettings.Header = "Настройки приложения";
                CheckBox_Minimize.Content = "Скрывать при запуске";
                CheckBox_Tray.Content = "в трей";
                CheckBox_ExitWithSteamVR.Content = "Закрывать при выходе из SteamVr";
                LinkIssue.Inlines.Clear(); LinkIssue.Inlines.Add("Сообщить об ошибке");
                ButtonLanguage.Content = "Switch to ENGLISH";

                GbStatus.Header = "Статус";
                LabelApp.Content = "Запущенное App Id";
                LabelStatus.Content = "Статус OpenVR";

                GbTwitch.Header = "Настройки Twitch";
                LabelChannelName.Content = "Название канала";
                LabelFeedbackBy.Content = "Оповещать о";
                LabelFeedbackType.Content = "Тип оповещения";
                LabelWhichController.Content = "На контроллере";
                TwitchTestButton.Content = "ТЕСТ";
                ButtonConnect.Content = "Сохранить и подключиться";

                GbAbout.Header = "О создателе";
                tb1.Text = "Приложение создано автором канала на Twitch: GoodVrGames";
                tb2.Text = "Оно полностью бесплатно, но я буду очень благодарен, если мой канал вырастет благодаря тебе! Поэтому прошу сообщить своим зрителям о моем канале.";
                tb3.Text = "Я стараюсь вести трансляции каждый день с началом в промежутке с 18 до полуночи и длительностью не менее часа. Играю преимущественно в VR игры.";
                tb4.Text = "Также мы можем скооперироваться с тобой. Такое я поддерживаю, но я не люблю мат \"через слово\". Если не замечал за собой излишнего сквернословия - пиши, не стесняйся, даже если мой канал вырос до миллиона подписчиков (лол, сейчас их 9).";

                GbLog.Header = "Лог";
                cbShowMessages.Content = "Показать сообщения в логе";
            }
		}

		private bool CheckboxValue(RoutedEventArgs e)
        {
            var name = e.RoutedEvent.Name;
            return name == "Checked";
        }
        private void CheckBox_Minimize_Checked(object sender, RoutedEventArgs e)
        {
            if (!loaded)
                return;
            MainModel.UpdateSetting(MainModel.Setting.Minimize, CheckboxValue(e));
        }

        private void CheckBox_Tray_Checked(object sender, RoutedEventArgs e)
        {
            if (!loaded)
                return;
            MainModel.UpdateSetting(MainModel.Setting.Tray, CheckboxValue(e));
        }

        private void ClickedURL(object sender, RoutedEventArgs e)
        {
            var link = (Hyperlink)sender;
            Process.Start(link.NavigateUri.ToString());
        }
        #endregion

        #region trayicon
        private void NotifyIcon_Click(object sender, EventArgs e)
        {
            WindowState = WindowState.Normal;
            ShowInTaskbar = true;
            Show();
            Activate();
        }
        #endregion

        private void CheckBox_ExitWithSteamVR_Checked(object sender, RoutedEventArgs e)
        {
            if (!loaded)
                return;
            MainModel.UpdateSetting(MainModel.Setting.ExitWithSteam, CheckboxValue(e));
        }

		private void TwitchTestButton_Click(object sender, RoutedEventArgs e)
		{
            _controller.PlayHaptic(animations[ComboBoxFeedbackType.SelectedIndex]);
        }

		private void TextBoxChannelName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!loaded)
                return;
            if (ButtonConnect != null)
                ButtonConnect.IsEnabled = true;
        }

		private void ComboBoxFeedbackBy_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!loaded)
                return;
            if (ComboBoxFeedbackBy.SelectedIndex > -1)
                MainModel.UpdateSetting(MainModel.Setting.FeedbackBy, ComboBoxFeedbackBy.SelectedIndex);
        }

		private void ComboBoxWhichController_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!loaded)
                return;
            if (ComboBoxWhichController.SelectedIndex > -1)
                MainModel.UpdateSetting(MainModel.Setting.WhichController, ComboBoxWhichController.SelectedIndex);
        }

		private void ComboBoxFeedbackType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!loaded)
                return;
            if (ComboBoxFeedbackType.SelectedIndex > -1)
            {
                MainModel.UpdateSetting(MainModel.Setting.FeedbackType, ComboBoxFeedbackType.SelectedIndex);
                if (animations != null && _controller != null)
                    _controller.PlayHaptic(animations[ComboBoxFeedbackType.SelectedIndex]);
            }
        }

		private void ButtonConnect_Click(object sender, RoutedEventArgs e)
        {
            ButtonConnect.IsEnabled = false;
            MainModel.UpdateSetting(MainModel.Setting.ChannelName, TextBoxChannelName.Text);
            ClientConnect();
        }

		private void Window_Loaded(object sender, RoutedEventArgs e)
		{
            loaded = true;
        }

		private void ButtonLanguage_Click(object sender, RoutedEventArgs e)
        {
            MainModel.UpdateSetting(MainModel.Setting.English, !(bool)MainModel.LoadSetting(MainModel.Setting.English));
            SwitchLanguage();
        }

		private void Window_Closing(object sender, CancelEventArgs e)
		{
            if (client != null && client.IsConnected)
                client.Disconnect();
		}
	}

	[Serializable]
    public class HapticAnimation
    {
        public string name { get; set; }
        public List<HapticFrame> hapticFrames { get; set; }
    }

    [Serializable]
    public class HapticFrame
	{
        public int duration { get; set; } = 100;
        public int delayAfterPlay { get; set; } = 100;
    }

    public enum NotifyMode
    {
        ALL_MESSAGES, SUBSCRIBERS, MODERATORS, HEY, NOFEEDBACK
    }

    public enum NotifyController
    {
        LEFT, RIGHT, BOTH
    }
}
