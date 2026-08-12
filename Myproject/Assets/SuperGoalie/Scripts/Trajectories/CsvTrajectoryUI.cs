using Assets.SuperGoalie.Scripts.Managers;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace Assets.SuperGoalie.Scripts.Trajectories
{
    public sealed class CsvTrajectoryUI : MonoBehaviour
    {
        GameManager _gameManager;
        Text _fileText;
        Text _statusText;
        Text _progressText;
        Text _scoreText;
        Text _gkNameText;
        Button _selectButton;
        Button _playButton;
        Button _prevGkBtn;
        Button _nextGkBtn;
        GoalkeeperRadarChart _radarChart;

        public static CsvTrajectoryUI Create(GameManager gameManager)
        {
            CsvTrajectoryUI existing = FindObjectOfType<CsvTrajectoryUI>();
            if (existing != null)
                return existing;

            GameObject oldPanel = GameObject.Find("Panel");
            if (oldPanel != null)
                oldPanel.SetActive(false);

            // 确保有 EventSystem，否则 UI 按钮不响应
            if (UnityEngine.EventSystems.EventSystem.current == null)
            {
                var esGo = new GameObject("EventSystem",
                    typeof(UnityEngine.EventSystems.EventSystem),
                    typeof(UnityEngine.EventSystems.StandaloneInputModule));
            }

            GameObject canvasObject = new GameObject("CsvTrajectoryCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1024f, 768f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            CsvTrajectoryUI ui = canvasObject.AddComponent<CsvTrajectoryUI>();
            ui.Build(gameManager, canvasObject.transform);
            return ui;
        }

        public void SetStatus(string message)
        {
            if (_statusText != null)
                _statusText.text = message;
        }

        public void SetScore(int score)
        {
            if (_scoreText != null)
                _scoreText.text = "进球数：" + score;
        }

        /// <summary>显示/隐藏整个控制界面（嵌入模式由 WPF 接管）</summary>
        public void SetVisible(bool visible)
        {
            gameObject.SetActive(visible);
        }

        public void SetPlaying(bool playing)
        {
            if (_selectButton != null) _selectButton.interactable = !playing;
            if (_playButton != null) _playButton.interactable = !playing && _gameManager.HasLoadedTrajectory;
        }

        void Build(GameManager gameManager, Transform canvasTransform)
        {
            _gameManager = gameManager;
            Font font = CreateChineseFont();

            GameObject panel = CreateUiObject("CsvPanel", canvasTransform);
            Image panelImage = panel.AddComponent<Image>();
            panelImage.color = new Color(0.035f, 0.055f, 0.085f, 0.90f);
            panelImage.raycastTarget = false;  // 背景不拦截点击
            RectTransform panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 0f);
            panelRect.anchorMax = new Vector2(0f, 0f);
            panelRect.pivot = new Vector2(0f, 0f);
            panelRect.anchoredPosition = new Vector2(16f, 16f);
            panelRect.sizeDelta = new Vector2(440f, 260f);

            CreateText(panel.transform, "Title", "点球 CSV 轨迹回放", font, 18, new Vector2(14f, -10f), new Vector2(412f, 24f), TextAnchor.MiddleLeft, FontStyle.Bold);
            CreateText(panel.transform, "Coordinates",
                "坐标：球门线地面中心为原点；X 向球场内，Y 向门将右侧（射手左侧），Z 为球心高度（米）",
                font, 11, new Vector2(14f, -36f), new Vector2(412f, 28f), TextAnchor.UpperLeft, FontStyle.Normal);

            _fileText = CreateText(panel.transform, "File", "尚未选择 CSV 文件", font, 12,
                new Vector2(14f, -67f), new Vector2(412f, 20f), TextAnchor.MiddleLeft, FontStyle.Normal);

            // 门将选择行
            CreateText(panel.transform, "GkLabel", "门将:", font, 13,
                new Vector2(14f, -93f), new Vector2(42f, 24f), TextAnchor.MiddleLeft, FontStyle.Bold);

            _prevGkBtn = CreateButton(panel.transform, "PrevGkBtn", "◀", font,
                new Vector2(58f, -93f), new Vector2(28f, 24f), PrevGoalkeeper);
            _prevGkBtn.GetComponent<Image>().color = new Color(0.3f, 0.3f, 0.35f);

            _gkNameText = CreateText(panel.transform, "GkName", "无门将", font, 13,
                new Vector2(90f, -93f), new Vector2(130f, 24f), TextAnchor.MiddleCenter, FontStyle.Bold);

            _nextGkBtn = CreateButton(panel.transform, "NextGkBtn", "▶", font,
                new Vector2(224f, -93f), new Vector2(28f, 24f), NextGoalkeeper);
            _nextGkBtn.GetComponent<Image>().color = new Color(0.3f, 0.3f, 0.35f);

            _selectButton = CreateButton(panel.transform, "SelectButton", "选择 CSV", font,
                new Vector2(14f, -125f), new Vector2(96f, 30f), OnSelectCsv);
            _playButton = CreateButton(panel.transform, "PlayButton", "开始播放", font,
                new Vector2(118f, -125f), new Vector2(96f, 30f), OnPlay);
            var exitBtn = CreateButton(panel.transform, "ExitButton", "退出程序", font,
                new Vector2(234f, -125f), new Vector2(96f, 30f), () =>
                {
#if UNITY_EDITOR
                    UnityEditor.EditorApplication.isPlaying = false;
#else
                    Application.Quit();
#endif
                });

            // 禁用所有按钮之间的导航，防止焦点跳转
            Navigation noneNav = new Navigation { mode = Navigation.Mode.None };
            _selectButton.navigation = noneNav;
            _playButton.navigation = noneNav;
            exitBtn.navigation = noneNav;

            _statusText = CreateText(panel.transform, "Status", "请选择点球轨迹 CSV；首点应约为 (11, 0, 0.145)。", font, 12,
                new Vector2(14f, -162f), new Vector2(412f, 28f), TextAnchor.UpperLeft, FontStyle.Normal);
            _progressText = CreateText(panel.transform, "Progress", "时间：0.000 / 0.000 秒", font, 12,
                new Vector2(14f, -194f), new Vector2(215f, 20f), TextAnchor.MiddleLeft, FontStyle.Normal);
            _scoreText = CreateText(panel.transform, "Score", "进球数：0", font, 13,
                new Vector2(285f, -194f), new Vector2(141f, 20f), TextAnchor.MiddleRight, FontStyle.Bold);

            CreateText(panel.transform, "Format", "允许表头；时间必须严格递增。首点必须在点球点附近，坐标记录的是球心。", font, 10,
                new Vector2(14f, -220f), new Vector2(412f, 24f), TextAnchor.MiddleLeft, FontStyle.Normal);

            _playButton.interactable = false;

            // 五维雷达图（右上角）
            _radarChart = GoalkeeperRadarChart.Create(canvasTransform, font);
        }

        void Update()
        {
            if (_gameManager == null || _progressText == null)
                return;

            _progressText.text = string.Format("时间：{0:0.000} / {1:0.000} 秒",
                _gameManager.TrajectoryTime, _gameManager.TrajectoryDuration);
        }

        void OnSelectCsv()
        {
            try
            {
                string path = WindowsCsvFileDialog.Open();
                if (string.IsNullOrEmpty(path))
                    return;

                string resultMessage;
                if (_gameManager.TryLoadTrajectory(path, out resultMessage))
                {
                    _fileText.text = "文件：" + Path.GetFileName(path);
                    SetStatus(resultMessage);
                    _playButton.interactable = true;
                }
                else
                {
                    SetStatus(resultMessage);
                    _playButton.interactable = false;
                }
            }
            catch (Exception exception)
            {
                SetStatus("打开文件失败：" + exception.Message);
                _playButton.interactable = false;
            }
        }

        void OnPlay()
        {
            string message;
            if (_gameManager.TryPlayTrajectory(out message))
            {
                SetStatus(message);
                SetPlaying(true);
            }
            else
            {
                SetStatus(message);
            }
        }

        /// <summary>
        /// 刷新门将显示
        /// </summary>
        public void RefreshGoalkeeperList()
        {
            UpdateGoalkeeperDisplay();
        }

        void PrevGoalkeeper()
        {
            if (_gameManager == null) return;
            var list = _gameManager.AvailableGoalkeepers;
            if (list.Count == 0) return;
            int idx = list.IndexOf(_gameManager.CurrentGoalkeeperName ?? "");
            if (idx < 0) idx = 0;
            idx = (idx - 1 + list.Count) % list.Count;
            _gameManager.SwitchGoalkeeper(list[idx]);
            UpdateGoalkeeperDisplay();
        }

        void NextGoalkeeper()
        {
            if (_gameManager == null) return;
            var list = _gameManager.AvailableGoalkeepers;
            if (list.Count == 0) return;
            int idx = list.IndexOf(_gameManager.CurrentGoalkeeperName ?? "");
            if (idx < 0) idx = -1;
            idx = (idx + 1) % list.Count;
            _gameManager.SwitchGoalkeeper(list[idx]);
            UpdateGoalkeeperDisplay();
        }

        void UpdateGoalkeeperDisplay()
        {
            if (_gameManager == null) return;
            string name = _gameManager.CurrentGoalkeeperName ?? "无";
            if (_gkNameText != null) _gkNameText.text = name;
            if (_radarChart != null) _radarChart.Refresh(_gameManager.CurrentGoalkeeperData);
        }

        static GameObject CreateUiObject(string name, Transform parent)
        {
            GameObject result = new GameObject(name, typeof(RectTransform));
            result.layer = 5;
            result.transform.SetParent(parent, false);
            return result;
        }

        static Text CreateText(Transform parent, string name, string value, Font font, int fontSize,
            Vector2 anchoredPosition, Vector2 size, TextAnchor alignment, FontStyle fontStyle)
        {
            GameObject textObject = CreateUiObject(name, parent);
            Text text = textObject.AddComponent<Text>();
            text.font = font;
            text.fontSize = fontSize;
            text.fontStyle = fontStyle;
            text.color = Color.white;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.text = value;
            text.raycastTarget = false;  // 文字不拦截点击，让按钮自己处理

            RectTransform rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;
            return text;
        }

        static Button CreateButton(Transform parent, string name, string label, Font font,
            Vector2 anchoredPosition, Vector2 size, UnityEngine.Events.UnityAction action)
        {
            GameObject buttonObject = CreateUiObject(name, parent);
            Image image = buttonObject.AddComponent<Image>();
            image.color = new Color(0.12f, 0.52f, 0.88f, 1f);
            Button button = buttonObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(action);

            ColorBlock colors = button.colors;
            colors.highlightedColor = new Color(0.2f, 0.65f, 1f, 1f);
            colors.pressedColor = new Color(0.08f, 0.36f, 0.68f, 1f);
            colors.disabledColor = new Color(0.25f, 0.28f, 0.32f, 0.8f);
            button.colors = colors;

            RectTransform buttonRect = buttonObject.GetComponent<RectTransform>();
            buttonRect.anchorMin = new Vector2(0f, 1f);
            buttonRect.anchorMax = new Vector2(0f, 1f);
            buttonRect.pivot = new Vector2(0f, 1f);
            buttonRect.anchoredPosition = anchoredPosition;
            buttonRect.sizeDelta = size;

            Text text = CreateText(buttonObject.transform, "Label", label, font, 13,
                Vector2.zero, Vector2.zero, TextAnchor.MiddleCenter, FontStyle.Bold);
            RectTransform textRect = text.rectTransform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.pivot = new Vector2(0.5f, 0.5f);
            textRect.anchoredPosition = Vector2.zero;
            textRect.sizeDelta = Vector2.zero;
            return button;
        }

        static Font CreateChineseFont()
        {
            try
            {
                return Font.CreateDynamicFontFromOSFont(
                    new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Arial" }, 18);
            }
            catch
            {
                return Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
        }
    }
}
