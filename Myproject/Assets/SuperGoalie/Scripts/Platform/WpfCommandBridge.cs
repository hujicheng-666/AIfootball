using Assets.SuperGoalie.Scripts.Entities;
using Assets.SuperGoalie.Scripts.Managers;
using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace Assets.SuperGoalie.Scripts.Platform
{
    /// <summary>
    /// 当 Unity 以 --wpf-host 嵌入 WPF 时，桥接来自宿主应用的命令。
    /// 命令通过 --wpf-command 指定的文本文件传递（AIfootball.App 侧由
    /// UnityCommandClient 写入），本组件轮询文件内容变化并执行。
    /// 支持命令：csv:&lt;path&gt; | replay | reset | view |
    /// goalkeeper:previous | goalkeeper:next | speed:&lt;float&gt;
    /// </summary>
    public sealed class WpfCommandBridge : MonoBehaviour
    {
        const float PollInterval = 0.15f;

        // 广角 FOV：场景主相机默认长焦视野过窄，看不到球门全貌
        // （参考 PenaltyKick/Scripts/Camera/MultiViewReplayCamera.cs 的 DefaultFieldOfView）
        const float DefaultFieldOfView = 55f;

        struct CameraView
        {
            public Vector3 Position;
            public Vector3 Target;
            public CameraView(Vector3 position, Vector3 target)
            {
                Position = position;
                Target = target;
            }
        }

        GameManager _gameManager;
        Camera _mainCamera;
        Goal _goal;
        Ball _ball;
        string _commandFile;
        string _lastContent = "";
        CameraView[] _views;
        int _activeView;

        /// <summary>解析命令行并创建桥；非嵌入模式返回 null。</summary>
        public static WpfCommandBridge Create(GameManager gameManager)
        {
            bool wpfHost = false;
            string commandFile = null;
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--wpf-host", StringComparison.OrdinalIgnoreCase))
                    wpfHost = true;
                else if (string.Equals(args[i], "--wpf-command", StringComparison.OrdinalIgnoreCase)
                         && i + 1 < args.Length)
                    commandFile = args[i + 1].Trim().Trim('"');
            }

            if (!wpfHost || string.IsNullOrEmpty(commandFile))
            {
                WriteDiagnostic("Create: wpfHost=" + wpfHost
                    + " commandFile=" + (commandFile ?? "<null>") + " -> early return");
                return null;
            }

            WriteDiagnostic("Create: wpfHost=" + wpfHost
                + " commandFile=" + commandFile + " -> bridge created");
            GameObject hostObject = new GameObject("WpfCommandBridge");
            WpfCommandBridge bridge = hostObject.AddComponent<WpfCommandBridge>();
            bridge.Init(gameManager, commandFile);
            return bridge;
        }

        /// <summary>把命令行与解析结果写入 runtime/data/wpf-bridge-debug.txt 供排查</summary>
        static void WriteDiagnostic(string message)
        {
            try
            {
                string dir = Path.Combine(Application.dataPath, "..", "data");
                Directory.CreateDirectory(dir);
                File.WriteAllText(
                    Path.Combine(dir, "wpf-bridge-debug.txt"),
                    message + "\n---\n" + string.Join("\n", Environment.GetCommandLineArgs()));
            }
            catch { }
        }

        void Init(GameManager gameManager, string commandFile)
        {
            _gameManager = gameManager;
            _commandFile = commandFile;
            _mainCamera = Camera.main;

            _goal = gameManager != null ? gameManager.Goal : null;
            if (_goal == null)
                _goal = FindObjectOfType<Goal>(true);
            _ball = gameManager != null ? gameManager.Ball : null;
            if (_ball == null)
                _ball = FindObjectOfType<Ball>(true);

            // 视角基于球门坐标系（参考 MultiViewReplayCamera 的 4 个预设）
            _views = BuildViews();
            // 初始视角：罚球点视角（罚球点上方看球门）
            _activeView = 0;
        }

        CameraView[] BuildViews()
        {
            if (_goal != null)
            {
                Vector3 origin = _goal.CsvCoordinateOrigin;
                Vector3 forward = _goal.PitchForward;   // 从球门指向罚球点
                Vector3 right = _goal.PitchRight;       // 门将右侧（CSV Y 正方向）
                return new[]
                {
                    // 罚球点视角（初始）：相机在罚球点后上方，看向球门内
                    new CameraView(origin + forward * 18f + Vector3.up * 4.2f, origin + forward * 2.5f + Vector3.up * 1.2f),
                    // 球门后方
                    new CameraView(origin - forward * 7f + Vector3.up * 3.2f, origin + forward * 6f + Vector3.up * 1.2f),
                    // 侧面
                    new CameraView(origin + forward * 5.5f + right * 14f + Vector3.up * 5f, origin + forward * 5.5f + Vector3.up * 1.2f),
                    // 高空俯瞰
                    new CameraView(origin + forward * 8f + Vector3.up * 16f, origin + forward * 3f + Vector3.up * 0.5f),
                };
            }

            // 回退：场景默认位姿
            Vector3 defaultPos = _mainCamera != null ? _mainCamera.transform.position : new Vector3(0f, 6f, 25f);
            Vector3 defaultTarget = defaultPos + (_mainCamera != null ? _mainCamera.transform.forward : Vector3.forward) * 20f;
            return new[] { new CameraView(defaultPos, defaultTarget) };
        }

        void Start()
        {
            // 嵌入模式隐藏 Unity 全部 UI（场景预置 Canvas/Panel + 运行时 UI），
            // 由 WPF 控制条接管
            foreach (Canvas c in FindObjectsOfType<Canvas>(true))
            {
                if (c != null && c.gameObject != null)
                    c.gameObject.SetActive(false);
            }
            if (_gameManager != null)
                _gameManager.SetUiVisible(false);
            ApplyView(_activeView);
            StartCoroutine(PollLoop());
        }

        IEnumerator PollLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(PollInterval);
                if (string.IsNullOrEmpty(_commandFile) || !File.Exists(_commandFile))
                    continue;

                string content;
                try { content = File.ReadAllText(_commandFile).Trim(); }
                catch { continue; }

                if (content.Length == 0 || content == _lastContent)
                    continue;

                _lastContent = content;
                ExecuteCommand(content);
            }
        }

        void ExecuteCommand(string command)
        {
            if (command.StartsWith("csv:", StringComparison.Ordinal))
            {
                string path = command.Substring(4).Trim().Trim('"');
                string message = null;
                if (_gameManager != null && _gameManager.TryLoadTrajectory(path, out message))
                    Debug.Log("[WpfCommandBridge] csv 已载入: " + message);
                else
                    Debug.LogWarning("[WpfCommandBridge] csv 载入失败: " + message);
                return;
            }

            switch (command)
            {
                case "replay":
                    string message = null;
                    if (_gameManager != null && _gameManager.TryPlayTrajectory(out message))
                        Debug.Log("[WpfCommandBridge] replay: " + message);
                    else
                        Debug.LogWarning("[WpfCommandBridge] replay 失败: " + message);
                    break;
                case "reset":
                    if (_gameManager != null)
                        _gameManager.StopAndResetTrajectory();
                    break;
                case "view":
                    CycleView();
                    break;
                case "goalkeeper:previous":
                    if (_gameManager != null) _gameManager.PreviousGoalkeeper();
                    break;
                case "goalkeeper:next":
                    if (_gameManager != null) _gameManager.NextGoalkeeper();
                    break;
                default:
                    if (command.StartsWith("speed:", StringComparison.Ordinal))
                    {
                        float speed;
                        if (float.TryParse(command.Substring(6), out speed) && _gameManager != null)
                            _gameManager.SetPlaybackSpeed(speed);
                        else
                            Debug.LogWarning("[WpfCommandBridge] 无效速度命令: " + command);
                    }
                    else
                    {
                        Debug.LogWarning("[WpfCommandBridge] 未知命令: " + command);
                    }
                    break;
            }
        }

        void CycleView()
        {
            if (_views == null || _views.Length == 0) return;
            _activeView = (_activeView + 1) % _views.Length;
            ApplyView(_activeView);
        }

        void ApplyView(int index)
        {
            if (_views == null || _views.Length == 0) return;
            if (_mainCamera == null)
            {
                _mainCamera = Camera.main;
                if (_mainCamera == null) return;
            }
            // 广角 FOV，避免长焦视野过窄看不到球场全貌
            _mainCamera.fieldOfView = DefaultFieldOfView;
            Vector3 position = _views[index].Position;
            Vector3 target = _views[index].Target;
            _mainCamera.transform.SetPositionAndRotation(
                position,
                Quaternion.LookRotation((target - position).normalized, Vector3.up));
            Debug.Log("[WpfCommandBridge] 视角: " + index);
        }
    }
}
