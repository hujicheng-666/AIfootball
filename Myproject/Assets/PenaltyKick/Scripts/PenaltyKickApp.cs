using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Assets.SuperGoalie.Scripts.Data;
using Assets.SuperGoalie.Scripts.Entities;
using Assets.SuperGoalie.Scripts.Trajectories;
using PenaltyKickPlatform.CameraControl;
using PenaltyKickPlatform.Analysis;
using PenaltyKickPlatform.Coordinate;
using PenaltyKickPlatform.History;
using PenaltyKickPlatform.Platform;
using PenaltyKickPlatform.UI;
using UnityEngine;

namespace PenaltyKickPlatform
{
    [DefaultExecutionOrder(1000)]
    public sealed class PenaltyKickApp : MonoBehaviour
    {
        private const float KeeperHomeForwardDistance = 0f;
        private const float KeeperSavePlaneForwardDistance = 1.35f;
        private Ball _ball;
        private Goal _goal;
        private GoalKeeper _keeper;
        private PenaltyCoordinateSystem _coordinates;
        private CsvHistoryStore _history;
        private MultiViewReplayCamera _cameraController;
        private PenaltyKickRuntimeUI _ui;
        private BallTrajectory _trajectory;
        private string _trajectoryName;
        private float _playbackSpeed = 1f;
        private Quaternion _ballInitialRotation;
        private bool _trajectoryPlaying;
        private bool _goalScored;
        private bool _resultRecorded;
        private string _currentHistoryId;
        private Coroutine _autoResetCoroutine;
        private string _goalkeepersDir;
        private readonly List<string> _availableGoalkeepers = new List<string>();
        private string _currentGoalkeeperName;

        public CsvHistoryStore History { get { return _history; } }
        public MultiViewReplayCamera CameraController { get { return _cameraController; } }
        public PenaltyCoordinateSystem Coordinates { get { return _coordinates; } }
        public float PlaybackSpeed { get { return _playbackSpeed; } }
        public bool HasTrajectory { get { return _trajectory != null; } }
        public IList<string> AvailableGoalkeepers { get { return _availableGoalkeepers.AsReadOnly(); } }
        public string CurrentGoalkeeperName { get { return _currentGoalkeeperName; } }
        public GoalkeeperData CurrentGoalkeeperData { get { return _keeper != null ? _keeper.GoalkeeperData : null; } }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindObjectOfType<PenaltyKickApp>() != null) return;
            if (FindObjectOfType<Ball>() == null || FindObjectOfType<Goal>() == null
                || FindObjectOfType<GoalKeeper>() == null || Camera.main == null) return;

            new GameObject("PenaltyKickPlatform").AddComponent<PenaltyKickApp>();
        }

        private void Start()
        {
            gameObject.name = "PenaltyKickPlatform";
            _ball = FindObjectOfType<Ball>();
            _goal = FindObjectOfType<Goal>();
            _keeper = FindObjectOfType<GoalKeeper>();
            if (_goal != null)
                _goal.EnsureSceneReferences();
            _coordinates = PenaltyCoordinateSystem.FromScene(_goal, _ball);
            _history = new CsvHistoryStore(
                Path.GetFullPath(Path.Combine(Application.dataPath, "..", "data")),
                Application.persistentDataPath);
            _ballInitialRotation = _ball.transform.rotation;

            // Keep the trigger active so goal detection works after scene initialization.
            _goal.GoalTrigger.OnCollidedWithBall += HandleGoalScored;
            _ball.OnBallLaunched += _keeper.Instance_OnBallLaunched;
            _ball.OnTrajectoryCompleted += HandleTrajectoryCompleted;
            _ball.OnTrajectoryReleased += HandleTrajectoryReleased;

            Vector3 keeperHome = _coordinates.Origin + _coordinates.XAxis * KeeperHomeForwardDistance;
            keeperHome.y = _keeper.transform.position.y;
            Quaternion keeperHomeRotation = Quaternion.LookRotation(_coordinates.XAxis, Vector3.up);
            _keeper.SetHomePose(keeperHome, keeperHomeRotation);
            InitializeGoalkeepers();

            _cameraController = gameObject.AddComponent<MultiViewReplayCamera>();
            _cameraController.Initialize(Camera.main, _ball, _coordinates);

            _ui = gameObject.AddComponent<PenaltyKickRuntimeUI>();
            _ui.Initialize(this);
            RefreshGoalkeeperUI();
            SetStatus("\u5c31\u7eea\u3002\u8bf7\u4ece\u4e3b\u9879\u76ee\u5bfc\u5165\u5df2\u91cd\u5efa\u7684\u8f68\u8ff9 CSV\u3002");
            ImportFromCommandLine();

            // Hide the legacy UI after the new runtime UI has been created.
            HideLegacyUI();
            _ui.DisableCompetingCanvases();
        }

        private void Update()
        {
            if (!_trajectoryPlaying || _trajectory == null) return;
            _ui.SetLiveTime(_ball.TrajectoryTime, _trajectory.Duration);
        }

        private void OnDestroy()
        {
            if (_goal != null && _goal.GoalTrigger != null)
                _goal.GoalTrigger.OnCollidedWithBall -= HandleGoalScored;
            if (_ball != null)
            {
                if (_keeper != null)
                    _ball.OnBallLaunched -= _keeper.Instance_OnBallLaunched;
                _ball.OnTrajectoryCompleted -= HandleTrajectoryCompleted;
                _ball.OnTrajectoryReleased -= HandleTrajectoryReleased;
            }
        }

        // CSV import
        public void PickCsv()
        {
            Debug.Log("[PenaltyKick] PickCsv called");
            SetStatus("\u6b63\u5728\u6253\u5f00 CSV \u9009\u62e9\u5668...");
            try
            {
                new CsvFilePicker().Pick(
                    (name, text) => ImportCsvText(name, text, true), 
                    err => SetStatus(err));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PenaltyKick] CsvFilePicker failed: " + ex.Message + " - falling back");
                string path = Assets.SuperGoalie.Scripts.Trajectories.WindowsCsvFileDialog.Open();
                if (!string.IsNullOrEmpty(path))
                    ImportCsvText(Path.GetFileName(path), File.ReadAllText(path), true);
            }
        }

        public void ImportCsvText(string displayName, string csvText, bool saveToHistory)
        {
            try
            {
                BallTrajectory parsed = CsvTrajectoryLoader.Parse(csvText, _coordinates.ToWorld);
                CsvHistoryEntry entry = saveToHistory ? _history.Add(displayName, csvText) : null;
                _ui.RebuildHistory();
                LoadTrajectoryPreview(parsed, displayName, entry?.Id);
            }
            catch (Exception e) { SetStatus("CSV \u8bfb\u53d6\u5931\u8d25\uff1a" + e.Message); }
        }

        // Goalkeeper database
        public void PreviousGoalkeeper()
        {
            SwitchGoalkeeperByOffset(-1);
        }

        public void NextGoalkeeper()
        {
            SwitchGoalkeeperByOffset(1);
        }

        public bool SwitchGoalkeeper(string goalkeeperName)
        {
            if (string.IsNullOrEmpty(goalkeeperName) || string.IsNullOrEmpty(_goalkeepersDir))
                return false;

            string path = Path.Combine(_goalkeepersDir, goalkeeperName + ".json");
            if (!File.Exists(path))
                path = Path.Combine(_goalkeepersDir, goalkeeperName);
            if (!File.Exists(path))
            {
                SetStatus("\u627e\u4e0d\u5230\u95e8\u5c06\u914d\u7f6e: " + goalkeeperName);
                return false;
            }

            bool ok = _keeper.LoadGoalkeeperFromJson(path);
            if (!ok)
            {
                SetStatus("\u95e8\u5c06\u52a0\u8f7d\u5931\u8d25: " + goalkeeperName);
                return false;
            }

            _currentGoalkeeperName = Path.GetFileNameWithoutExtension(path);
            _keeper.ResetToHome();
            ResetKeeperShotState();
            RefreshGoalkeeperUI();
            SetStatus("\u5f53\u524d\u95e8\u5c06: " + GetGoalkeeperDisplayName());
            return true;
        }

        private void InitializeGoalkeepers()
        {
            _goalkeepersDir = FindDataSubDir("goalkeepers");
            _availableGoalkeepers.Clear();
            _availableGoalkeepers.AddRange(GoalkeeperData.ListAvailableGoalkeepers(_goalkeepersDir));

            string selected = ReadCommandLineOption("--goalkeeper");
            if (string.IsNullOrEmpty(selected) && _availableGoalkeepers.Count > 0)
                selected = _availableGoalkeepers[0];
            if (!string.IsNullOrEmpty(selected))
                SwitchGoalkeeper(selected);
        }

        private void SwitchGoalkeeperByOffset(int offset)
        {
            if (_availableGoalkeepers.Count == 0)
            {
                SetStatus("\u672a\u627e\u5230\u95e8\u5c06\u914d\u7f6e\u3002");
                return;
            }

            int index = _availableGoalkeepers.IndexOf(_currentGoalkeeperName ?? string.Empty);
            if (index < 0)
                index = 0;
            index = (index + offset + _availableGoalkeepers.Count) % _availableGoalkeepers.Count;
            SwitchGoalkeeper(_availableGoalkeepers[index]);
        }

        private string FindDataSubDir(string subDir)
        {
            string[] candidates =
            {
                Path.GetFullPath(Path.Combine(Application.dataPath, "..", "data", subDir)),
                Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "runtime", "data", subDir)),
                Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "data", subDir))
            };

            for (int index = 0; index < candidates.Length; index++)
            {
                if (Directory.Exists(candidates[index]))
                    return candidates[index];
            }
            return candidates[0];
        }

        private string ReadCommandLineOption(string option)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int index = 0; index + 1 < args.Length; index++)
            {
                if (args[index] == option)
                    return args[index + 1];
            }
            return null;
        }

        private void RefreshGoalkeeperUI()
        {
            if (_ui == null)
                return;

            int index = _availableGoalkeepers.IndexOf(_currentGoalkeeperName ?? string.Empty);
            _ui.SetGoalkeeper(GetGoalkeeperDisplayName(), CurrentGoalkeeperData, index, _availableGoalkeepers.Count);
        }

        private string GetGoalkeeperDisplayName()
        {
            GoalkeeperData data = CurrentGoalkeeperData;
            if (data != null && !string.IsNullOrWhiteSpace(data.DisplayName))
                return data.DisplayName;
            return string.IsNullOrEmpty(_currentGoalkeeperName) ? "None" : _currentGoalkeeperName;
        }
        // Replay controls
        public void RestartPlayback()
        {
            if (_trajectory == null) { SetStatus("\u8bf7\u5148\u5bfc\u5165 CSV\u3002"); return; }
            PlayTrajectory(_trajectory, _trajectoryName, _currentHistoryId);
        }

        public void PlayHistory(string id)
        {
            try
            {
                var entries = _history.Entries;
                CsvHistoryEntry entry = null;
                for (int i = 0; i < entries.Count; i++)
                {
                    if (entries[i].Id == id) { entry = entries[i]; break; }
                }
                if (entry == null) { SetStatus("\u5386\u53f2\u8bb0\u5f55\u4e0d\u5b58\u5728\u3002"); return; }
                BallTrajectory parsed = CsvTrajectoryLoader.Parse(_history.Read(id), _coordinates.ToWorld);
                LoadTrajectoryPreview(parsed, entry.DisplayName, entry.Id);
            }
            catch (Exception e) { SetStatus("\u8bfb\u53d6\u5931\u8d25\uff1a" + e.Message); }
        }

        public void DeleteHistory(string id)
        {
            if (_history.Delete(id)) { _ui.RebuildHistory(); SetStatus("\u5df2\u5220\u9664\u3002"); }
        }

        public void SetPlaybackSpeed(float speed)
        {
            _playbackSpeed = speed;
            Time.timeScale = speed;
            _ui.SetPlaybackSpeed(speed);
            if (_trajectoryPlaying && _trajectory != null) RestartPlayback();
            else SetStatus("\u500d\u901f\uff1a" + speed.ToString("0.##") + "x");
        }

        public ShooterProfile GenerateShooterProfile()
        {
            if (_history == null || _history.Entries.Count == 0)
            {
                SetStatus("请先导入至少一条训练轨迹 CSV。");
                return null;
            }

            ShooterProfile profile = ShooterProfileAnalyzer.Build(_history, _coordinates);
            if (profile.ValidTrajectoryCount == 0)
            {
                SetStatus("没有可用于生成画像的有效训练轨迹。");
                return null;
            }

            SetStatus("已根据 " + profile.ValidTrajectoryCount + " 条训练轨迹生成人物特点。");
            return profile;
        }

        public void ResetAll()
        {
            StopAutoReset();
            _trajectoryPlaying = false;
            _goalScored = false;
            _resultRecorded = false;
            Time.timeScale = 1f;
            _ball.CancelTrajectory();
            _ball.Stop();
            // 使用实际球半径而非硬编码 0.145f，保证复位高度与轨迹/校验逻辑一致
            _ball.transform.SetPositionAndRotation(_coordinates.ToWorld(new Vector3(11f, 0f, _ball.WorldRadius)), _ballInitialRotation);
            _ball.gameObject.SetActive(true);
            _keeper.ResetToHome();
            ResetKeeperShotState();
            _goal.GoalTrigger.ResetForNewShot();
            _goal.GoalTrigger.gameObject.SetActive(true);
            _ui.SetLiveTime(0f, _trajectory?.Duration ?? 0f);
            SetStatus("\u5df2\u91cd\u7f6e\u3002");
        }

        public void SetStatus(string msg)
        {
            _ui?.SetStatus(msg);
            Debug.Log("[PenaltyKick] " + msg);
        }

        // Internal helpers
        private void LoadTrajectoryPreview(BallTrajectory trajectory, string displayName, string historyId)
        {
            StopAutoReset();
            _trajectoryPlaying = false;
            _goalScored = false;
            _resultRecorded = false;
            _currentHistoryId = historyId;
            Time.timeScale = 1f;

            _trajectory = trajectory;
            _trajectoryName = displayName;

            _ball.gameObject.SetActive(true);
            _ball.HoldAtCenter(trajectory.InitialCenter);
            _goal.GoalTrigger.ResetForNewShot();
            _goal.GoalTrigger.gameObject.SetActive(true);
            _keeper.ResetToHome();
            ResetKeeperShotState();
            _ui.SetLiveTime(0f, trajectory.Duration);

            float maxHeight = trajectory.MaxCenterY - _coordinates.Origin.y;
            string heightWarning = maxHeight > 3f ? "\uff0c\u6ce8\u610f\uff1a\u6700\u9ad8\u70b9 " + maxHeight.ToString("0.00") + "m\uff0c\u9ad8\u4e8e\u7403\u95e8" : string.Empty;
            SetStatus(string.Format("\u5df2\u52a0\u8f7d {0}\uff08{1}\u91c7\u6837\u70b9\uff09{2}\uff0c\u70b9\u51fb\u201c\u4ece\u5934\u64ad\u653e\u201d\u5f00\u59cb\u3002",
                displayName, trajectory.SampleCount, heightWarning));
        }
        private void PlayTrajectory(BallTrajectory trajectory, string displayName, string historyId)
        {
            StopAutoReset();
            _trajectoryPlaying = false;
            _goalScored = false;
            _resultRecorded = false;
            _currentHistoryId = historyId;
            Time.timeScale = _playbackSpeed;

            _ball.Stop();
            _ball.gameObject.SetActive(true);
            _goal.GoalTrigger.ResetForNewShot();
            _goal.GoalTrigger.gameObject.SetActive(true);
            _keeper.ResetToHome();
            ResetKeeperShotState();
            _keeper.PrepareForShot();

            _trajectory = trajectory;
            _trajectoryName = displayName;

            Vector3 keeperSavePlane = _coordinates.Origin + _coordinates.XAxis * KeeperSavePlaneForwardDistance;
            Vector3 keeperTarget = trajectory.FindCenterClosestToPlane(keeperSavePlane, _coordinates.XAxis);
            _ball.PlayTrajectory(trajectory, keeperTarget);

            _trajectoryPlaying = true;
            float maxHeight = trajectory.MaxCenterY - _coordinates.Origin.y;
            string heightWarning = maxHeight > 3f ? "\uff0c\u6ce8\u610f\uff1a\u6700\u9ad8\u70b9 " + maxHeight.ToString("0.00") + "m\uff0c\u9ad8\u4e8e\u7403\u95e8" : string.Empty;
            SetStatus(string.Format("\u64ad\u653e {0}\uff08{1}\u91c7\u6837\u70b9, {2}x\uff09{3}",
                displayName, trajectory.SampleCount, _playbackSpeed.ToString("0.##"), heightWarning));
        }


        private void ResetKeeperShotState()
        {
            if (_keeper == null)
                return;

            _keeper.SaveAttemptSuccess = false;
            _keeper.WasHitByBall = false;
            _keeper.SaveProbability = 0f;
        }
        private void HandleGoalScored()
        {
            if (_resultRecorded || (!_trajectoryPlaying && _autoResetCoroutine == null)) return;
            _goalScored = true;
            _trajectoryPlaying = false;
            Vector3 releaseVelocity = _ball != null ? _ball.Velocity : Vector3.zero;
            if (_ball != null && releaseVelocity.sqrMagnitude > 0.01f)
                _ball.ReleaseTrajectoryToPhysics(releaseVelocity);

            bool keeperTouched = _keeper != null && (_keeper.SaveAttemptSuccess || _keeper.WasHitByBall);
            _history.RecordResult(_currentHistoryId, CsvHistoryStore.ResultGoal, keeperTouched, _ball != null ? _ball.TrajectoryTime : (_trajectory?.Duration ?? 0f));
            _ui.RebuildHistory();
            _resultRecorded = true;
            SetStatus("\u8fdb\u7403\uff0c\u7403\u7ee7\u7eed\u6309\u7269\u7406\u8fd0\u52a8\u3002");
            ScheduleAutoResetWhenBallSettles(8f, "\u5df2\u81ea\u52a8\u590d\u4f4d\u3002");
        }


        private void HandleTrajectoryCompleted()
        {
            if (!_trajectoryPlaying || _resultRecorded)
                return;

            _trajectoryPlaying = false;
            ScheduleAutoResetWhenBallSettles(8f, "\u5df2\u81ea\u52a8\u590d\u4f4d\u3002");
        }

        private void HandleTrajectoryReleased()
        {
            if (!_trajectoryPlaying || _resultRecorded)
                return;

            _trajectoryPlaying = false;
            ScheduleAutoResetWhenBallSettles(8f, "\u5df2\u81ea\u52a8\u590d\u4f4d\u3002");
        }

        private void RecordNonGoalResult()
        {
            bool keeperTouched = _keeper != null && (_keeper.SaveAttemptSuccess || _keeper.WasHitByBall);
            string result = keeperTouched ? CsvHistoryStore.ResultSaved : CsvHistoryStore.ResultMiss;
            _history.RecordResult(_currentHistoryId, result, keeperTouched, _ball != null ? _ball.TrajectoryTime : 0f);
            _ui.RebuildHistory();
            _resultRecorded = true;
            SetStatus(result + "\uff0c\u5373\u5c06\u81ea\u52a8\u590d\u4f4d\u3002");
        }

        private void ScheduleAutoReset(float delaySeconds, string statusAfterReset)
        {
            StopAutoReset();
            _autoResetCoroutine = StartCoroutine(AutoResetAfterDelay(delaySeconds, statusAfterReset));
        }

        private void ScheduleAutoResetWhenBallSettles(float maxDelaySeconds, string statusAfterReset)
        {
            StopAutoReset();
            _autoResetCoroutine = StartCoroutine(AutoResetWhenBallSettles(maxDelaySeconds, statusAfterReset));
        }

        private IEnumerator AutoResetWhenBallSettles(float maxDelaySeconds, string statusAfterReset)
        {
            float elapsed = 0f;
            float settled = 0f;
            while (elapsed < maxDelaySeconds)
            {
                yield return new WaitForSecondsRealtime(0.25f);
                elapsed += 0.25f;
                bool physicsBall = _ball != null && (_ball.TrajectoryPlayer == null || !_ball.TrajectoryPlayer.IsPlaying);
                bool slow = _ball == null || _ball.Rigidbody == null || _ball.Rigidbody.velocity.sqrMagnitude < 0.04f;
                settled = physicsBall && slow ? settled + 0.25f : 0f;
                if (elapsed >= 1.5f && settled >= 0.75f)
                    break;
            }

            _autoResetCoroutine = null;
            if (!_resultRecorded && !_goalScored)
                RecordNonGoalResult();
            ResetAll();
            SetStatus(statusAfterReset);
        }

        private IEnumerator AutoResetAfterDelay(float delaySeconds, string statusAfterReset)
        {
            yield return new WaitForSecondsRealtime(delaySeconds);
            _autoResetCoroutine = null;
            if (!_resultRecorded && !_goalScored)
                RecordNonGoalResult();
            ResetAll();
            SetStatus(statusAfterReset);
        }

        private void StopAutoReset()
        {
            if (_autoResetCoroutine == null)
                return;

            StopCoroutine(_autoResetCoroutine);
            _autoResetCoroutine = null;
        }
        private void ImportFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "-trajectory" && i + 1 < args.Length)
                {
                    string path = args[i + 1];
                    if (File.Exists(path))
                    {
                        string text = File.ReadAllText(path);
                        ImportCsvText(Path.GetFileName(path), text, false);
                    }
                }
            }
        }

        private void HideLegacyUI()
        {
            foreach (Canvas canvas in FindObjectsOfType<Canvas>(true))
            {
                if (canvas == null)
                    continue;
                if (canvas.name == "PenaltyKickRuntimeCanvas")
                    continue;
                if (canvas.GetComponentInParent<PenaltyKickRuntimeUI>() != null)
                    continue;

                canvas.gameObject.SetActive(false);
                Debug.Log("[PenaltyKick] Hid legacy canvas: " + canvas.name);
            }
        }
    }
}
