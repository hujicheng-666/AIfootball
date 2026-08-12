using Assets.SuperGoalie.Scripts.Data;
using Assets.SuperGoalie.Scripts.Entities;
using Assets.SuperGoalie.Scripts.Platform;
using Assets.SuperGoalie.Scripts.States.GoalKeeperStates.Idle.MainState;
using Assets.SuperGoalie.Scripts.Trajectories;
using Patterns.Singleton;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Assets.SuperGoalie.Scripts.Managers
{
    public class GameManager : Singleton<GameManager>
    {
        [SerializeField] Ball _ball;
        [SerializeField] Goal _goal;
        [SerializeField] GoalKeeper _goalKeeper;
        [SerializeField] Text _scoreText;
        [SerializeField] float _penaltySpotTolerance = 0.35f;

        BallTrajectory _loadedTrajectory;
        CsvTrajectoryUI _trajectoryUi;
        Coroutine _resetCoroutine;
        SoundManager _soundManager;
        int _score;
        bool _shotInProgress;
        Vector3 _originalBallCenter;
        Quaternion _originalBallRotation;

        // 门将数据
        string _goalkeepersDir;
        List<string> _availableGoalkeepers = new List<string>();
        string _currentGoalkeeperName;

        public List<string> AvailableGoalkeepers { get { return _availableGoalkeepers; } }
        public string CurrentGoalkeeperName { get { return _currentGoalkeeperName; } }
        public GoalkeeperData CurrentGoalkeeperData
        {
            get { return _goalKeeper != null ? _goalKeeper.GoalkeeperData : null; }
        }

        public Goal Goal { get { return _goal; } }
        public Ball Ball { get { return _ball; } }
        public bool HasLoadedTrajectory { get { return _loadedTrajectory != null; } }
        public float TrajectoryTime { get { return _ball != null ? _ball.TrajectoryTime : 0f; } }
        public float TrajectoryDuration
        {
            get { return _loadedTrajectory != null ? _loadedTrajectory.Duration : 0f; }
        }

        public override void Awake()
        {
            base.Awake();

            ResolveSceneReferences();
            List<string> missingReferences = GetMissingSceneReferences();
            if (missingReferences.Count > 0)
            {
                Debug.LogError(
                    "GameManager cannot start because the active scene is missing: "
                    + string.Join(", ", missingReferences.ToArray())
                    + ". Open Assets/SuperGoalie/Scenes/Demo.unity instead of a Temp/__Backupscenes scene.",
                    this);
                enabled = false;
                return;
            }

            _soundManager = SoundManager.Instance;
            if (_soundManager != null)
            {
                _ball.OnBallLaunched += _soundManager.PlayBallKickedSound;
                _goalKeeper.OnPunchBall += _soundManager.PlayBallKickedSound;
                _goal.GoalTrigger.OnCollidedWithBall += _soundManager.PlayGoalScoredSound;
            }
            _ball.OnBallLaunched += _goalKeeper.Instance_OnBallLaunched;
            _goal.GoalTrigger.OnCollidedWithBall += Instance_OnBallCollidedWithGoal;
            _ball.OnTrajectoryCompleted += Instance_OnTrajectoryCompleted;
            _ball.OnTrajectoryReleased += Instance_OnTrajectoryReleased;

            _originalBallCenter = _ball.CenterPosition;
            _originalBallRotation = _ball.Rotation;

            _trajectoryUi = CsvTrajectoryUI.Create(this);
            _trajectoryUi.SetScore(_score);
        }

        void Start()
        {
            // 确保窗口获取焦点，避免按钮需双击
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;

            // 确定数据目录
            _goalkeepersDir = FindDataSubDir("goalkeepers");

            // 扫描可用门将
            _availableGoalkeepers = GoalkeeperData.ListAvailableGoalkeepers(_goalkeepersDir);
            if (_availableGoalkeepers.Count == 0)
            {
                // 回退：检查 runtime/data/goalkeepers
                string altDir = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(Application.dataPath, "..", "..", "runtime", "data", "goalkeepers"));
                if (System.IO.Directory.Exists(altDir))
                {
                    _goalkeepersDir = altDir;
                    _availableGoalkeepers = GoalkeeperData.ListAvailableGoalkeepers(_goalkeepersDir);
                }
            }

            // 命令行指定门将: --goalkeeper GK_Elite
            string gkArg = null;
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--goalkeeper" && i + 1 < args.Length)
                    gkArg = args[i + 1];
            }

            // 选择门将：命令行优先 > 默认第一个
            string selectedGk = gkArg;
            if (string.IsNullOrEmpty(selectedGk) && _availableGoalkeepers.Count > 0)
                selectedGk = _availableGoalkeepers[0];

            if (!string.IsNullOrEmpty(selectedGk))
            {
                string gkPath = System.IO.Path.Combine(_goalkeepersDir, selectedGk + ".json");
                if (!System.IO.File.Exists(gkPath) && !selectedGk.EndsWith(".json"))
                    gkPath = System.IO.Path.Combine(_goalkeepersDir, selectedGk);

                if (System.IO.File.Exists(gkPath))
                {
                    _goalKeeper.LoadGoalkeeperFromJson(gkPath);
                    _currentGoalkeeperName = System.IO.Path.GetFileNameWithoutExtension(gkPath);
                    Debug.Log($"[GameManager] 门将: {_currentGoalkeeperName}");
                }
            }

            // 通知 UI 刷新门将列表
            if (_trajectoryUi != null)
                _trajectoryUi.RefreshGoalkeeperList();

            string csvToLoad = null;

            // 命令行自动加载: FootballViewer.exe --csv "data/sample1_trajectory.csv"
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--csv" && i + 1 < args.Length)
                {
                    csvToLoad = args[i + 1];
                    if (!System.IO.Path.IsPathRooted(csvToLoad))
                        csvToLoad = System.IO.Path.GetFullPath(
                            System.IO.Path.Combine(Application.dataPath, "..", csvToLoad));
                    break;
                }
            }

            // 2) 无命令行参数时，自动查找 data/ 下最新的 CSV
            if (string.IsNullOrEmpty(csvToLoad))
            {
                var dataDirs = new System.Collections.Generic.List<string>
                {
                    System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", "data")),
                    // Unity Editor 模式下 runtime/data/
                    System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", "..", "runtime", "data")),
                };
                foreach (string dataDir in dataDirs)
                {
                    if (System.IO.Directory.Exists(dataDir))
                    {
                        var csvFiles = System.IO.Directory.GetFiles(dataDir, "*_trajectory.csv");
                        if (csvFiles.Length > 0)
                        {
                            System.Array.Sort(csvFiles);
                            csvToLoad = csvFiles[csvFiles.Length - 1];
                            Debug.Log($"[GameManager] 自动检测到 CSV: {csvToLoad}");
                            break;
                        }
                    }
                }
            }

            // 执行加载（不自动播放，等待用户点击）
            if (!string.IsNullOrEmpty(csvToLoad) && System.IO.File.Exists(csvToLoad))
            {
                Debug.Log($"[GameManager] 自动加载: {csvToLoad}");
                if (TryLoadTrajectory(csvToLoad, out string loadMsg))
                {
                    Debug.Log($"[GameManager] {loadMsg}");
                    if (_trajectoryUi != null)
                    {
                        _trajectoryUi.SetStatus(loadMsg);
                        _trajectoryUi.SetPlaying(false);
                    }
                }
                else
                {
                    Debug.LogError($"[GameManager] 自动加载失败: {loadMsg}");
                    if (_trajectoryUi != null)
                        _trajectoryUi.SetStatus(loadMsg);
                }
            }

            // 嵌入模式（WPF --wpf-host）：接管 WPF 端控制命令
            WpfCommandBridge.Create(this);
        }

        void ResolveSceneReferences()
        {
            if (_ball == null)
                _ball = FindObjectOfType<Ball>(true);
            if (_goal == null)
                _goal = FindObjectOfType<Goal>(true);
            if (_goalKeeper == null)
                _goalKeeper = FindObjectOfType<GoalKeeper>(true);

            if (_goal != null)
                _goal.EnsureSceneReferences();
        }

        List<string> GetMissingSceneReferences()
        {
            List<string> missing = new List<string>();
            if (_ball == null)
                missing.Add("Ball");
            if (_goal == null)
                missing.Add("Goal");
            else
            {
                if (_goal.GoalTrigger == null)
                    missing.Add("GoalTrigger");
                if (!_goal.HasCompleteGoalMouth)
                    missing.Add("GoalMouth points");
            }
            if (_goalKeeper == null)
                missing.Add("GoalKeeper");
            return missing;
        }

        public bool TryLoadTrajectory(string path, out string message)
        {
            try
            {
                StopResetCoroutine();
                _loadedTrajectory = CsvTrajectoryLoader.Load(path, _goal);
                ValidatePenaltyStart(_loadedTrajectory);
                _shotInProgress = false;

                _ball.Rotation = _originalBallRotation;
                _ball.HoldAtCenter(_loadedTrajectory.InitialCenter);
                ResetGoalKeeper();
                _goal.GoalTrigger.gameObject.SetActive(true);

                message = string.Format("已载入 {0} 个轨迹点，总时长 {1:0.000} 秒。",
                    _loadedTrajectory.SampleCount, _loadedTrajectory.Duration);
                return true;
            }
            catch (Exception exception)
            {
                _loadedTrajectory = null;
                message = "载入失败：" + exception.Message;
                return false;
            }
        }

        public bool TryPlayTrajectory(out string message)
        {
            if (_loadedTrajectory == null)
            {
                message = "请先选择一个有效的 CSV 文件。";
                return false;
            }

            try
            {
                StopResetCoroutine();
                ResetGoalKeeper();
                _goalKeeper.PrepareForShot();
                _goal.GoalTrigger.ResetForNewShot();
                _goal.GoalTrigger.gameObject.SetActive(true);

                Vector3 goalkeeperTarget = _loadedTrajectory.FindClosestCenter(_goal.CsvCoordinateOrigin);
                _ball.PlayTrajectory(_loadedTrajectory, goalkeeperTarget);
                _shotInProgress = true;
                message = "正在按 CSV 轨迹播放；守门员触球后将切换为物理运动。";
                return true;
            }
            catch (Exception exception)
            {
                _shotInProgress = false;
                message = "播放失败：" + exception.Message;
                return false;
            }
        }

        public void StopAndResetTrajectory()
        {
            StopResetCoroutine();
            ResetNow();
            if (_trajectoryUi != null)
                _trajectoryUi.SetStatus(_loadedTrajectory == null
                    ? "请选择列顺序为 time,x,y,z 的 CSV 文件。"
                    : "已停止并回到轨迹起点，可以重新播放。 ");
        }

        /// <summary>
        /// 运行时切换门将
        /// </summary>
        public bool SwitchGoalkeeper(string goalkeeperName)
        {
            string gkPath = System.IO.Path.Combine(_goalkeepersDir, goalkeeperName + ".json");
            if (!System.IO.File.Exists(gkPath))
                gkPath = System.IO.Path.Combine(_goalkeepersDir, goalkeeperName);

            if (!System.IO.File.Exists(gkPath))
            {
                Debug.LogWarning($"[GameManager] 门将文件不存在: {gkPath}");
                return false;
            }

            bool ok = _goalKeeper.LoadGoalkeeperFromJson(gkPath);
            if (ok)
            {
                _currentGoalkeeperName = System.IO.Path.GetFileNameWithoutExtension(gkPath);
                ResetGoalKeeper();
                if (_trajectoryUi != null)
                    _trajectoryUi.SetStatus($"已切换门将: {_currentGoalkeeperName}");
                Debug.Log($"[GameManager] 切换门将: {_currentGoalkeeperName}");
            }
            return ok;
        }

        /// <summary>切换到上一个可用门将</summary>
        public bool PreviousGoalkeeper()
        {
            if (_availableGoalkeepers.Count == 0) return false;
            int index = _availableGoalkeepers.IndexOf(_currentGoalkeeperName ?? "");
            if (index < 0) index = 0;
            index = (index - 1 + _availableGoalkeepers.Count) % _availableGoalkeepers.Count;
            return SwitchGoalkeeper(_availableGoalkeepers[index]);
        }

        /// <summary>切换到下一个可用门将</summary>
        public bool NextGoalkeeper()
        {
            if (_availableGoalkeepers.Count == 0) return false;
            int index = _availableGoalkeepers.IndexOf(_currentGoalkeeperName ?? "");
            if (index < 0) index = -1;
            index = (index + 1) % _availableGoalkeepers.Count;
            return SwitchGoalkeeper(_availableGoalkeepers[index]);
        }

        /// <summary>设置轨迹回放速度倍率</summary>
        public void SetPlaybackSpeed(float speed)
        {
            if (_ball != null)
                _ball.PlaybackSpeed = speed;
            Debug.Log("[GameManager] 回放速度: " + speed);
        }

        /// <summary>嵌入模式隐藏/显示 Unity 自带 UI</summary>
        public void SetUiVisible(bool visible)
        {
            if (_trajectoryUi != null)
                _trajectoryUi.SetVisible(visible);
        }

        string FindDataSubDir(string subDir)
        {
            var candidates = new List<string>
            {
                System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", "data", subDir)),
                System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", "..", "runtime", "data", subDir)),
            };
            foreach (string dir in candidates)
                if (System.IO.Directory.Exists(dir))
                    return dir;
            return candidates[0];
        }

        void Instance_OnBallCollidedWithGoal()
        {
            // 如果球已被扑出，不算进球
            if (_goalKeeper != null && _goalKeeper.SaveAttemptSuccess) return;

            ++_score;
            if (_scoreText != null)
                _scoreText.text = string.Format("Score:{0}", _score);
            if (_trajectoryUi != null)
            {
                _trajectoryUi.SetScore(_score);
                _trajectoryUi.SetStatus("进球！即将回到轨迹起点。 ");
            }

            ScheduleReset(2f);
        }

        void Instance_OnTrajectoryCompleted()
        {
            if (!_shotInProgress) return;
            _shotInProgress = false;
            ScheduleReset(2f);
        }

        void Instance_OnTrajectoryReleased()
        {
            if (!_shotInProgress) return;
            ScheduleReset(4f);
        }

        /// <summary>直接显示状态文字</summary>
        public void ShowStatus(string msg)
        {
            if (_trajectoryUi != null)
                _trajectoryUi.SetStatus(msg);
        }

        void ScheduleReset(float delay)
        {
            StopResetCoroutine();
            _resetCoroutine = StartCoroutine(ResetAfterDelay(delay));
        }

        IEnumerator ResetAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            _resetCoroutine = null;
            ResetNow();
            if (_trajectoryUi != null)
                _trajectoryUi.SetStatus("已回到轨迹起点，可以再次播放。 ");
        }

        void ResetNow()
        {
            _shotInProgress = false;
            _ball.Stop();
            _ball.Rotation = _originalBallRotation;

            if (_loadedTrajectory != null)
                _ball.HoldAtCenter(_loadedTrajectory.InitialCenter);
            else
            {
                _ball.CancelTrajectory();
                _ball.Position = _ball.RootPositionForCenter(_originalBallCenter);
            }

            ResetGoalKeeper();
            _goal.GoalTrigger.gameObject.SetActive(true);

            if (_trajectoryUi != null)
                _trajectoryUi.SetPlaying(false);
        }

        void ResetGoalKeeper()
        {
            if (_goalKeeper != null)
            {
                _goalKeeper.ResetPosition();
                if (_goalKeeper.FSM != null && _goalKeeper.FSM.ContainsState<IdleMainState>())
                    _goalKeeper.FSM.ChangeState<IdleMainState>();
            }
        }

        void ValidatePenaltyStart(BallTrajectory trajectory)
        {
            float worldBallRadius = _ball.SphereCollider.radius
                * Mathf.Max(_ball.transform.lossyScale.x, Mathf.Max(_ball.transform.lossyScale.y, _ball.transform.lossyScale.z));
            Vector3 expectedCenter = _goal.PenaltySpotBallCenter(worldBallRadius);
            float distance = Vector3.Distance(trajectory.InitialCenter, expectedCenter);

            if (distance > Mathf.Max(_penaltySpotTolerance, 0.01f))
            {
                throw new FormatException(string.Format(
                    "首个球心坐标必须位于点球点附近。期望约为 ({0:0.###}, 0, {1:0.###})，当前误差 {2:0.###} 米。",
                    Goal.PenaltySpotDistance, worldBallRadius, distance));
            }
        }

        void StopResetCoroutine()
        {
            if (_resetCoroutine == null)
                return;

            StopCoroutine(_resetCoroutine);
            _resetCoroutine = null;
        }

        void OnDestroy()
        {
            if (_ball != null)
            {
                if (_soundManager != null)
                    _ball.OnBallLaunched -= _soundManager.PlayBallKickedSound;
                if (_goalKeeper != null)
                    _ball.OnBallLaunched -= _goalKeeper.Instance_OnBallLaunched;
                _ball.OnTrajectoryCompleted -= Instance_OnTrajectoryCompleted;
                _ball.OnTrajectoryReleased -= Instance_OnTrajectoryReleased;
            }

            if (_goalKeeper != null && _soundManager != null)
                _goalKeeper.OnPunchBall -= _soundManager.PlayBallKickedSound;

            if (_goal != null && _goal.GoalTrigger != null)
            {
                if (_soundManager != null)
                    _goal.GoalTrigger.OnCollidedWithBall -= _soundManager.PlayGoalScoredSound;
                _goal.GoalTrigger.OnCollidedWithBall -= Instance_OnBallCollidedWithGoal;
            }
        }
    }
}
