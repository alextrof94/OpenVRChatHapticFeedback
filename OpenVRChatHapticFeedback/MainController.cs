using BOLL7708;
using GregsStack.InputSimulatorStandard;
using GregsStack.InputSimulatorStandard.Native;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Timers;
using System.Windows;
using System.Windows.Input;
using Valve.VR;

namespace OpenVRChatHapticFeedback
{
    class MainController
    {
        private EasyOpenVRSingleton _ovr = EasyOpenVRSingleton.Instance;
        private InputSimulator _sim = new InputSimulator();
        private bool _shouldShutDown = false;

        // Active key registration
        private string _registeringKey = string.Empty;
        private object _registeringElement = null;
        private HashSet<Key> _keys = new HashSet<Key>();
        private HashSet<Key> _keysDown = new HashSet<Key>();

        // Actions
        public Action<bool> StatusUpdateAction { get; set; } = (status) => { Debug.WriteLine("No status action set."); };
        public Action<string> AppUpdateAction { get; set; } = (appId) => { Debug.WriteLine("No appID action set."); };
        public Action<string, bool> KeyTextUpdateAction { get; set; } = (status, cancel) => { Debug.WriteLine("No key text action set."); };
        public Action<Dictionary<string, Key[]>, bool> ConfigRetrievedAction { get; set; } = (config, forceButtonOff) => { Debug.WriteLine("No config loaded."); };
        public Action<string, bool> KeyActivatedAction { get; set; } = (key, on) => { Debug.WriteLine("No key simulated action set."); };
        public Action<bool> DashboardVisibleAction { get; set; } = (visible) => { Debug.WriteLine("No dashboard visible action set."); };

        // Other
        private string _currentApplicationId = "";
        private ulong _inputSourceHandleLeft = 0, _inputSourceHandleRight = 0;
        private ulong[] _inputSourceHandles = new ulong[10];
        private ulong _notificationOverlayHandle = 0;
        private string[] _actionKeys = new string[0];

        public MainController()
        {
        }

        public void Init(string[] actionKeys)
        {
            _actionKeys = actionKeys;

            // Sets default values for status labels
            StatusUpdateAction.Invoke(false);
            AppUpdateAction.Invoke(MainModel.CONFIG_DEFAULT);
            KeyActivatedAction.Invoke(string.Empty, false);

            // Loads default config
            LoadConfig(true);

            // Start background thread
            var workerThread = new Thread(WorkerThread);
            workerThread.Start();
        }
        public void SetDebugLogAction(Action<string> action)
        {
            _ovr.SetDebugLogAction(action);
        }

        #region bindings
        public bool ToggleRegisteringKey(string actionKey, object sender, out object activeElement)
        {
            var active = _registeringKey == string.Empty;
            if (active)
            {
                _registeringKey = actionKey;
                _registeringElement = sender;
                _keysDown.Clear();
                _keys.Clear();
                activeElement = sender;
            }
            else
            {
                activeElement = _registeringElement;
                MainModel.RegisterBinding(_registeringKey, _keys); // TODO: Should only save existing configs
                _registeringKey = string.Empty;
                _registeringElement = null;
            }
            return active;
        }

        private void StopRegisteringKeys()
        {
            UpdateCurrentObject(true);
            _keysDown.Clear();
            _keys.Clear();
            _registeringKey = string.Empty;
            _registeringElement = null;
        }

        // Add incoming keys to the current binding
        public bool OnKeyDown(Key key)
        {
            if (_registeringElement == null) return true;
            if (MainUtils.MatchVirtualKey(key) != null)
            {
                if (_keysDown.Count == 0) _keys.Clear();
                _keys.Add(key);
                _keysDown.Add(key);
                UpdateCurrentObject();
                return true;
            }
            else
            {
                return false;
            }
        }
        public void OnKeyUp(Key key)
        {
            if (_registeringElement == null) return;
            if (key == Key.RightAlt) _keysDown.Remove(Key.LeftCtrl); // Because AltGr records as RightAlt+LeftCtrl
            _keysDown.Remove(key);
            UpdateCurrentObject();
        }

        // Send text to UI to update label
        private void UpdateCurrentObject(bool cancel=false)
        {
            KeyTextUpdateAction.Invoke(GetKeysLabel(), cancel);
        }

        // Generate label text from keys
        public string GetKeysLabel(Key[] keys = null)
        {
            if (keys == null)
            {
                keys = new Key[_keys.Count];
                _keys.CopyTo(keys);
            }
            List<string> result = new List<string>();
            foreach (Key k in keys)
            {
                result.Add(k.ToString());
            }
            return string.Join(" + ", result.ToArray());
        }

        #endregion

        #region worker
        private void WorkerThread()
        {
            Thread.CurrentThread.IsBackground = true;
            bool initComplete = false;
            while (true)
            {
                Thread.Sleep(10);
                if (_ovr.IsInitialized())
                {
                    if (!initComplete)
                    {
                        initComplete = true;

                        _ovr.AddApplicationManifest("./app.vrmanifest", "boll7708.openvrchathapticfeedback", true);
                        _ovr.LoadActionManifest("./actions.json");
                        RegisterActions();
                        UpdateAppId();
                        StatusUpdateAction.Invoke(true);
                        UpdateInputSourceHandles();
                        _notificationOverlayHandle = _ovr.InitNotificationOverlay("OpenVRChatHapticFeedback");

                        _ovr.RegisterEvent(EVREventType.VREvent_Quit, (data) =>
                        {
                            _shouldShutDown = true;
                        });
                        _ovr.RegisterEvent(EVREventType.VREvent_SceneApplicationChanged, (data) =>
                        {
                            UpdateAppId();
                        });
                        _ovr.RegisterEvents(new EVREventType[] {
                            EVREventType.VREvent_TrackedDeviceActivated,
                            EVREventType.VREvent_TrackedDeviceRoleChanged,
                            EVREventType.VREvent_TrackedDeviceUpdated }, 
                            (data) =>
                            {
                                UpdateInputSourceHandles();
                            }
                        );
                        _ovr.RegisterEvent(EVREventType.VREvent_DashboardActivated, (data) =>
                        {
                            DashboardVisibleAction.Invoke(true);
                        });
                        _ovr.RegisterEvent(EVREventType.VREvent_DashboardDeactivated, (data) =>
                        {
                            DashboardVisibleAction.Invoke(false);
                        });
                        DashboardVisibleAction.Invoke(OpenVR.Overlay.IsDashboardVisible()); // To convey the initial state if the Dashboard is visible on launch of application.
                    }
                    else
                    {
                        _ovr.UpdateActionStates(_inputSourceHandles);

                        _ovr.UpdateEvents();

                        if (_shouldShutDown)
                        {
                            _shouldShutDown = false;
                            initComplete = false;
                            _ovr.AcknowledgeShutdown();
                            _ovr.Shutdown();
                            StatusUpdateAction.Invoke(false);
                        }
                    }
                }
                else
                {
                    _ovr.Init();
                    Thread.Sleep(1000);
                }
            }
        }

        // Controller roles have updated, refresh controller handles
        private void UpdateInputSourceHandles()
        {
            _inputSourceHandleLeft = _ovr.GetInputSourceHandle(EasyOpenVRSingleton.InputSource.LeftHand);
            _inputSourceHandleRight = _ovr.GetInputSourceHandle(EasyOpenVRSingleton.InputSource.RightHand);
            _inputSourceHandles[0] = _inputSourceHandleLeft;
            _inputSourceHandles[1] = _inputSourceHandleRight;
            _inputSourceHandles[2] = _ovr.GetInputSourceHandle(EasyOpenVRSingleton.InputSource.Chest);
            _inputSourceHandles[3] = _ovr.GetInputSourceHandle(EasyOpenVRSingleton.InputSource.LeftElbow);
            _inputSourceHandles[4] = _ovr.GetInputSourceHandle(EasyOpenVRSingleton.InputSource.LeftKnee);
            _inputSourceHandles[5] = _ovr.GetInputSourceHandle(EasyOpenVRSingleton.InputSource.LeftFoot);
            _inputSourceHandles[6] = _ovr.GetInputSourceHandle(EasyOpenVRSingleton.InputSource.RightElbow);
            _inputSourceHandles[7] = _ovr.GetInputSourceHandle(EasyOpenVRSingleton.InputSource.RightKnee);
            _inputSourceHandles[8] = _ovr.GetInputSourceHandle(EasyOpenVRSingleton.InputSource.RightFoot);
            _inputSourceHandles[9] = _ovr.GetInputSourceHandle(EasyOpenVRSingleton.InputSource.Waist);
        }

        // New app is running, distribute new app ID
        private void UpdateAppId()
        {
            StopRegisteringKeys();
            _currentApplicationId = _ovr.GetRunningApplicationId();
            if (_currentApplicationId == string.Empty) _currentApplicationId = MainModel.CONFIG_DEFAULT;
            AppUpdateAction.Invoke(_currentApplicationId);
            LoadConfig();
        }

        // Load config, if it exists
        public void LoadConfig(bool forceDefault=false)
        {
            var configName = forceDefault ? MainModel.CONFIG_DEFAULT : _currentApplicationId;
            var config = MainModel.RetrieveConfig(configName);
            if (config != null) MainModel.SetConfigName(configName);
            Debug.WriteLine($"Config for {configName} found: {config != null}");
            ConfigRetrievedAction.Invoke(config, _currentApplicationId == MainModel.CONFIG_DEFAULT);
        }

        public bool AppIsRunning()
        {
            Debug.WriteLine($"Running app: {_currentApplicationId}");
            return _currentApplicationId != MainModel.CONFIG_DEFAULT;
        }
        #endregion

        #region actions
        public void OpenConfigFolder() // TODO: This refuses to open the right folder so the button is hidden.
        {
            var folderPath = MainModel.GetConfigFolderPath();
            Debug.WriteLine(folderPath);
            if (Directory.Exists(folderPath))
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    Arguments = folderPath,
                    FileName = "explorer.exe"
                };
                Process.Start(startInfo);
            } 
            else
            {
                MessageBox.Show("Folder does not exist yet as no config has been saved.");
            }
        }
        #endregion

        #region vr_input

        // Register all actions with the input system
        private void RegisterActions()
        {
            _ovr.RegisterActionSet("/actions/keys");
            /*foreach (var actionKey in _actionKeys)
            {
                var localActionKey = actionKey;
                _ovr.RegisterDigitalAction($"/actions/keys/in/Key{actionKey}", (data, inputAction) => { OnAction(localActionKey, data, inputAction.handle); }, actionKey.Contains("C"));
            }*/
        }

        // Action was triggered, handle it
        private void OnAction(string actionKey, InputDigitalActionData_t data, ulong inputSourceHandle)
        {
            //KeyActivatedAction.Invoke(actionKey, data.bState);
        }
        #endregion


        private System.Timers.Timer timer;
        private System.Timers.Timer subTimer;
        private bool hapticPlaying = false;
        private int frameIndex = 0;
        private HapticAnimation hapticAnimation;

        public void PlayHaptic(HapticAnimation anim)
        {
            if (hapticPlaying)
                return;

            hapticPlaying = true;
            hapticAnimation = anim;

            if (timer == null)
			{
                timer = new System.Timers.Timer();
                timer.AutoReset = true;
                timer.Elapsed += HapticTimerElapsed;
            }

            frameIndex = 0;
            PlayHapticFrame(hapticAnimation.hapticFrames[frameIndex]);
            if (hapticAnimation.hapticFrames[frameIndex].delayAfterPlay + hapticAnimation.hapticFrames[frameIndex].duration > 0)
            {
                timer.Interval = hapticAnimation.hapticFrames[frameIndex].delayAfterPlay + hapticAnimation.hapticFrames[frameIndex].duration;
                timer.Start();
            } 
            else
			{
                hapticPlaying = false;
            }
        }

		private void HapticTimerElapsed(object sender, ElapsedEventArgs e)
        {
            frameIndex++;

            if (frameIndex < hapticAnimation.hapticFrames.Count - 1)
            {
                PlayHapticFrame(hapticAnimation.hapticFrames[frameIndex]);
                if (hapticAnimation.hapticFrames[frameIndex].delayAfterPlay + hapticAnimation.hapticFrames[frameIndex].duration > 0)
                    timer.Interval = hapticAnimation.hapticFrames[frameIndex].delayAfterPlay + hapticAnimation.hapticFrames[frameIndex].duration;
                else
                    timer.Stop();
            }
            else
            {
                timer.Stop();
                hapticPlaying = false;
                if (hapticAnimation.hapticFrames.Count > 1)
                    PlayHapticFrame(hapticAnimation.hapticFrames[frameIndex]);
            }
		}

        private int subTimerCount = 0;
        private int subTimerCountMax = 0;
        private void PlayHapticFrame(HapticFrame frame)
        {
            if (subTimer == null)
			{
                subTimer = new System.Timers.Timer();
                subTimer.Interval = 10;
                subTimer.Elapsed += SubTimerElapsed;
            }

            subTimerCount = 1;
            subTimerCountMax = frame.duration / 10;
            subTimer.Start();
        }

		private void SubTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (subTimerCount < subTimerCountMax)
            {
                subTimerCount++;
                if ((int)MainModel.LoadSetting(MainModel.Setting.WhichController) == (int)ControllerType.LEFT || (int)MainModel.LoadSetting(MainModel.Setting.WhichController) == (int)ControllerType.BOTH)
                    _ovr.TriggerHapticPulseInController(ETrackedControllerRole.LeftHand);
                if ((int)MainModel.LoadSetting(MainModel.Setting.WhichController) == (int)ControllerType.RIGHT || (int)MainModel.LoadSetting(MainModel.Setting.WhichController) == (int)ControllerType.BOTH)
                    _ovr.TriggerHapticPulseInController(ETrackedControllerRole.RightHand);
            } 
            else
			{
                subTimer.Stop();
            }
        }
    }

    public enum ControllerType
	{
        LEFT, RIGHT, BOTH
	}
}
