using System;
using Assets.SuperGoalie.Scripts.Data;
using PenaltyKickPlatform.History;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PenaltyKickPlatform.UI
{
    public sealed class PenaltyKickRuntimeUI : MonoBehaviour
    {
        private static readonly Color Panel = new Color(0.022f, 0.030f, 0.040f, 0.88f);
        private static readonly Color PanelDeep = new Color(0.012f, 0.018f, 0.026f, 0.92f);
        private static readonly Color Tile = new Color(0.060f, 0.074f, 0.092f, 0.92f);
        private static readonly Color TileHot = new Color(0.090f, 0.112f, 0.136f, 0.96f);
        private static readonly Color Accent = new Color(0.035f, 0.490f, 0.690f, 1f);
        private static readonly Color AccentBright = new Color(0.120f, 0.740f, 0.900f, 1f);
        private static readonly Color Gold = new Color(0.980f, 0.720f, 0.240f, 1f);
        private static readonly Color Danger = new Color(0.720f, 0.180f, 0.180f, 1f);
        private static readonly Color MainText = new Color(0.940f, 0.970f, 0.990f, 1f);
        private static readonly Color SoftText = new Color(0.700f, 0.780f, 0.850f, 1f);
        private static readonly Color DimText = new Color(0.450f, 0.540f, 0.620f, 1f);

        private static Font _uiFont;

        private PenaltyKickApp _app;
        private Canvas _canvas;
        private RectTransform _historyContent;
        private RectTransform _controlPanel;
        private RectTransform _historyPanel;
        private RectTransform _replayPanel;
        private Button _controlCollapseButton;
        private Button _historyCollapseButton;
        private Button _replayCollapseButton;
        private bool _controlCollapsed;
        private bool _historyCollapsed = true;
        private bool _replayCollapsed;
        private Text _statusText;
        private Text _timeText;
        private Text _speedText;
        private Text _statisticsText;
        private Text _viewText;
        private Text _goalkeeperNameText;
        private Text _goalkeeperStatsText;
        private Slider _speedSlider;
        private RawImage _goalkeeperRadarImage;
        private Texture2D _goalkeeperRadarTexture;

        private static Font UiFont
        {
            get
            {
                if (_uiFont == null)
                    _uiFont = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimHei", "Arial" }, 14);
                if (_uiFont == null)
                    _uiFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
                return _uiFont;
            }
        }

        public void Initialize(PenaltyKickApp app)
        {
            _app = app;
            BuildCanvas();
            if (_app.CameraController != null)
                _app.CameraController.ViewChanged += OnViewChanged;
            SetPlaybackSpeed(_app.PlaybackSpeed);
            RebuildHistory();
        }

        public void SetStatus(string message)
        {
            if (_statusText != null)
                _statusText.text = string.IsNullOrEmpty(message) ? "" : message;
        }

        public void SetLiveTime(float seconds)
        {
            if (_timeText != null)
                _timeText.text = FormatTime(seconds);
        }

        public void SetLiveTime(float seconds, float duration)
        {
            if (_timeText != null)
                _timeText.text = FormatTime(seconds) + " / " + FormatTime(duration);
        }

        public void SetPlaybackSpeed(float speed)
        {
            if (_speedText != null)
                _speedText.text = "\u901f\u5ea6 " + speed.ToString("0.##") + "x";
            if (_speedSlider != null)
            {
#if UNITY_2019_1_OR_NEWER
                _speedSlider.SetValueWithoutNotify(Mathf.Clamp(speed, 0.25f, 2f));
#else
                _speedSlider.value = Mathf.Clamp(speed, 0.25f, 2f);
#endif
            }
        }

        public void SetGoalkeeper(GoalkeeperData data)
        {
            SetGoalkeeper(data == null ? "\u95e8\u5c06" : data.DisplayName, data, -1, -1);
        }

        public void SetGoalkeeper(string displayName, GoalkeeperData data, int index, int total)
        {
            if (_goalkeeperNameText != null)
            {
                string name = string.IsNullOrWhiteSpace(displayName) ? "\u95e8\u5c06" : displayName;
                if (total > 0 && index >= 0)
                    name += "  " + (index + 1) + "/" + total;
                _goalkeeperNameText.text = name;
            }
            if (_goalkeeperStatsText != null)
            {
                if (data == null)
                {
                    _goalkeeperStatsText.text = "\u6682\u65e0\u95e8\u5c06\u6570\u636e";
                }
                else
                {
                    _goalkeeperStatsText.text = "\u8eab\u9ad8 " + data.Height.ToString("0.00") + "m   \u624b\u957f " + data.Reach.ToString("0.00") + "m\n"
                        + "\u624b\u611f " + Mathf.RoundToInt(data.GoalKeeping * 100f) + "%   \u98de\u6251 " + data.DiveSpeed.ToString("0.0") + "m/s   \u5f39\u8df3 " + data.JumpHeight.ToString("0.00") + "m";
                }
            }
            DrawGoalkeeperRadar(data);
        }
        public void RebuildHistory()
        {
            if (_historyContent == null)
                return;

            for (int index = _historyContent.childCount - 1; index >= 0; index--)
                Destroy(_historyContent.GetChild(index).gameObject);

            if (_app == null || _app.History == null || _app.History.Entries.Count == 0)
            {
                RectTransform empty = CreatePanel("HistoryEmpty", _historyContent, Tile);
                LayoutElement emptyLayout = empty.gameObject.AddComponent<LayoutElement>();
                emptyLayout.preferredHeight = 116f;
                emptyLayout.minHeight = 116f;

                Text title = CreateText("EmptyTitle", empty, "\u8fd8\u6ca1\u6709\u4e0a\u4f20\u8bb0\u5f55", 16, TextAnchor.MiddleCenter);
                SetRect(title.rectTransform, Vector2.zero, Vector2.one, new Vector2(12f, 54f), new Vector2(-12f, -20f));
                title.fontStyle = FontStyle.Bold;

                Text tip = CreateText("EmptyTip", empty, "\u5bfc\u5165\u4e3b\u9879\u76ee\u8f93\u51fa\u7684\u8f68\u8ff9 CSV \u540e\u4f1a\u51fa\u73b0\u5728\u8fd9\u91cc", 12, TextAnchor.MiddleCenter);
                SetRect(tip.rectTransform, Vector2.zero, Vector2.one, new Vector2(16f, 20f), new Vector2(-16f, -62f));
                tip.color = DimText;
            }
            else
            {
                for (int index = 0; index < _app.History.Entries.Count; index++)
                    CreateHistoryRow(_app.History.Entries[index]);
            }

            UpdateStatistics();
            ForceHistoryLayout();
        }

        private void BuildCanvas()
        {
            EnsureEventSystem();
            if (_canvas != null)
                Destroy(_canvas.gameObject);

            GameObject canvasObject = new GameObject("PenaltyKickRuntimeCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            SetLayerRecursively(canvasObject, 5);
            _canvas = canvasObject.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.overrideSorting = true;
            _canvas.sortingOrder = 10000;

            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.45f;

            GraphicRaycaster raycaster = canvasObject.GetComponent<GraphicRaycaster>();
            raycaster.ignoreReversedGraphics = true;

            BuildControlPanel(canvasObject.transform);
            BuildHistoryPanel(canvasObject.transform);
            BuildReplayPanel(canvasObject.transform);
            DisableCompetingCanvases();
        }

        private void BuildControlPanel(Transform canvas)
        {
            _controlPanel = CreatePanel("ControlPanel", canvas, Panel);
            RectTransform panel = _controlPanel;
            panel.anchorMin = new Vector2(0f, 1f);
            panel.anchorMax = new Vector2(0f, 1f);
            panel.pivot = new Vector2(0f, 1f);
            panel.anchoredPosition = new Vector2(18f, -18f);
            panel.sizeDelta = new Vector2(388f, 290f);
            AddChrome(panel.gameObject, 0.10f);

            Text title = CreateText("Title", panel, "\u70b9\u7403\u8f68\u8ff9\u5206\u6790\u53f0", 21, TextAnchor.MiddleLeft);
            SetRect(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(16f, -42f), new Vector2(-54f, -8f));
            title.fontStyle = FontStyle.Bold;

            Text subtitle = CreateText("Subtitle", panel, "\u4e3b\u9879\u76ee\u8f68\u8ff9\u5bfc\u5165  \u4e2a\u6027\u5316\u95e8\u5c06", 11, TextAnchor.MiddleRight);
            SetRect(subtitle.rectTransform, Vector2.zero, Vector2.zero, new Vector2(154f, 246f), new Vector2(372f, 276f));
            subtitle.color = DimText;
            subtitle.gameObject.SetActive(false);

            BuildInputPanel(panel);
            BuildGoalkeeperPanel(panel);

            _controlCollapseButton = CreateButton("CollapseControl", panel, "\u2014", TileHot, ToggleControlPanel);
            SetRect(_controlCollapseButton.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-44f, -39f), new Vector2(-10f, -7f));
            AddTopAccent(panel);
            ApplyControlPanelState();
        }

        private void BuildInputPanel(Transform parent)
        {
            RectTransform panel = CreatePanel("InputPanel", parent, new Color(0.050f, 0.065f, 0.086f, 0.78f));
            SetRect(panel, Vector2.zero, Vector2.zero, new Vector2(12f, 144f), new Vector2(376f, 238f));
            AddOutline(panel.gameObject, 0.06f);

            Text label = CreateText("InputLabel", panel, "\u6570\u636e\u8f93\u5165", 13, TextAnchor.MiddleLeft);
            SetRect(label.rectTransform, Vector2.zero, Vector2.zero, new Vector2(12f, 54f), new Vector2(180f, 78f));
            label.fontStyle = FontStyle.Bold;

            Button csv = CreateButton("PickCsv", panel, "\u5bfc\u5165\u4e3b\u9879\u76ee CSV", Accent, () => _app.PickCsv());
            SetRect(csv.GetComponent<RectTransform>(), Vector2.zero, Vector2.zero, new Vector2(12f, 16f), new Vector2(252f, 46f));

            Text tip = CreateText("PipelineTip", panel, "\u76f8\u673a\u6807\u5b9a\u4e0e 3D \u91cd\u5efa\u7531\u4e3b\u9879\u76ee\u5b8c\u6210", 11, TextAnchor.MiddleLeft);
            SetRect(tip.rectTransform, Vector2.zero, Vector2.zero, new Vector2(12f, 2f), new Vector2(348f, 18f));
            tip.color = DimText;

        }
        private void BuildGoalkeeperPanel(Transform parent)
        {
            RectTransform panel = CreatePanel("GoalkeeperPanel", parent, new Color(0.050f, 0.065f, 0.086f, 0.78f));
            SetRect(panel, Vector2.zero, Vector2.zero, new Vector2(12f, 12f), new Vector2(376f, 134f));
            AddOutline(panel.gameObject, 0.06f);

            Text title = CreateText("GoalkeeperTitle", panel, "\u95e8\u5c06\u80fd\u529b\u6863\u6848", 13, TextAnchor.MiddleLeft);
            SetRect(title.rectTransform, Vector2.zero, Vector2.zero, new Vector2(12f, 92f), new Vector2(140f, 114f));
            title.fontStyle = FontStyle.Bold;

            _goalkeeperRadarTexture = new Texture2D(96, 96, TextureFormat.RGBA32, false);
            _goalkeeperRadarTexture.filterMode = FilterMode.Bilinear;
            _goalkeeperRadarImage = CreateRect("GoalkeeperRadar", panel).gameObject.AddComponent<RawImage>();
            _goalkeeperRadarImage.texture = _goalkeeperRadarTexture;
            _goalkeeperRadarImage.raycastTarget = false;
            SetRect(_goalkeeperRadarImage.rectTransform, Vector2.zero, Vector2.zero, new Vector2(12f, 12f), new Vector2(98f, 98f));
            ClearRadarTexture();
            _goalkeeperRadarTexture.Apply();

            _goalkeeperNameText = CreateText("GoalkeeperName", panel, "\u95e8\u5c06", 16, TextAnchor.MiddleLeft);
            SetRect(_goalkeeperNameText.rectTransform, Vector2.zero, Vector2.zero, new Vector2(110f, 62f), new Vector2(254f, 92f));
            _goalkeeperNameText.fontStyle = FontStyle.Bold;
            _goalkeeperNameText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _goalkeeperNameText.verticalOverflow = VerticalWrapMode.Truncate;

            _goalkeeperStatsText = CreateText("GoalkeeperStats", panel, "", 11, TextAnchor.UpperLeft);
            SetRect(_goalkeeperStatsText.rectTransform, Vector2.zero, Vector2.zero, new Vector2(110f, 16f), new Vector2(258f, 60f));
            _goalkeeperStatsText.color = SoftText;
            _goalkeeperStatsText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _goalkeeperStatsText.verticalOverflow = VerticalWrapMode.Truncate;

            Button previous = CreateButton("PreviousGoalkeeper", panel, "\u4e0a\u4e00\u4f4d", TileHot, () => _app.PreviousGoalkeeper());
            SetRect(previous.GetComponent<RectTransform>(), Vector2.zero, Vector2.zero, new Vector2(268f, 64f), new Vector2(348f, 96f));

            Button next = CreateButton("NextGoalkeeper", panel, "\u4e0b\u4e00\u4f4d", Accent, () => _app.NextGoalkeeper());
            SetRect(next.GetComponent<RectTransform>(), Vector2.zero, Vector2.zero, new Vector2(268f, 24f), new Vector2(348f, 56f));
        }

        private void BuildHistoryPanel(Transform canvas)
        {
            _historyPanel = CreatePanel("HistoryPanel", canvas, PanelDeep);
            RectTransform panel = _historyPanel;
            panel.anchorMin = new Vector2(1f, 1f);
            panel.anchorMax = new Vector2(1f, 1f);
            panel.pivot = new Vector2(1f, 1f);
            panel.anchoredPosition = new Vector2(-18f, -18f);
            panel.sizeDelta = new Vector2(334f, 520f);
            AddChrome(panel.gameObject, 0.10f);

            Text title = CreateText("HistoryTitle", panel, "\u4e0a\u4f20\u5386\u53f2", 18, TextAnchor.MiddleLeft);
            SetRect(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(16f, -42f), new Vector2(-54f, -8f));
            title.fontStyle = FontStyle.Bold;

            Text hint = CreateText("HistoryHint", panel, "\u70b9\u51fb\u8bb0\u5f55\u8f7d\u5165\u9884\u89c8", 11, TextAnchor.MiddleRight);
            SetRect(hint.rectTransform, Vector2.zero, Vector2.zero, new Vector2(148f, 482f), new Vector2(318f, 506f));
            hint.color = DimText;
            hint.gameObject.SetActive(false);

            _statisticsText = CreateText("Statistics", panel, "", 12, TextAnchor.UpperLeft);
            SetRect(_statisticsText.rectTransform, Vector2.zero, Vector2.zero, new Vector2(16f, 424f), new Vector2(318f, 470f));
            _statisticsText.color = SoftText;
            _statisticsText.horizontalOverflow = HorizontalWrapMode.Wrap;

            RectTransform scrollRoot = CreatePanel("HistoryScroll", panel, new Color(0.010f, 0.016f, 0.024f, 0.46f));
            SetRect(scrollRoot, Vector2.zero, Vector2.zero, new Vector2(10f, 12f), new Vector2(324f, 414f));
            scrollRoot.GetComponent<Image>().raycastTarget = true;

            ScrollRect scrollRect = scrollRoot.gameObject.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 24f;

            RectTransform viewport = CreateRect("Viewport", scrollRoot);
            Stretch(viewport, 6f);
            // RectMask2D does not depend on a transparent Graphic writing stencil.
            // The former clear Image + Mask combination could clip all rendering
            // while raycasts still worked, producing invisible clickable rows.
            viewport.gameObject.AddComponent<RectMask2D>();

            _historyContent = CreateRect("Content", viewport);
            _historyContent.anchorMin = new Vector2(0f, 1f);
            _historyContent.anchorMax = new Vector2(1f, 1f);
            _historyContent.pivot = new Vector2(0.5f, 1f);
            _historyContent.anchoredPosition = Vector2.zero;
            _historyContent.sizeDelta = Vector2.zero;

            VerticalLayoutGroup layout = _historyContent.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 8f;
            layout.padding = new RectOffset(6, 6, 6, 6);
            layout.childForceExpandHeight = false;
            layout.childControlHeight = true;
            layout.childControlWidth = true;

            ContentSizeFitter fitter = _historyContent.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scrollRect.viewport = viewport;
            scrollRect.content = _historyContent;

            _historyCollapseButton = CreateButton("CollapseHistory", panel, "+", TileHot, ToggleHistoryPanel);
            SetRect(_historyCollapseButton.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-44f, -39f), new Vector2(-10f, -7f));
            AddTopAccent(panel);
            ApplyHistoryPanelState();
        }

        private void BuildReplayPanel(Transform canvas)
        {
            _replayPanel = CreatePanel("ReplayPanel", canvas, Panel);
            RectTransform panel = _replayPanel;
            panel.anchorMin = new Vector2(0.5f, 0f);
            panel.anchorMax = new Vector2(0.5f, 0f);
            panel.pivot = new Vector2(0.5f, 0f);
            panel.anchoredPosition = new Vector2(0f, 14f);
            panel.sizeDelta = new Vector2(680f, 104f);
            AddChrome(panel.gameObject, 0.08f);

            Button reset = CreateButton("Reset", panel, "\u590d\u4f4d", TileHot, () => _app.ResetAll());
            SetRect(reset.GetComponent<RectTransform>(), Vector2.zero, Vector2.zero, new Vector2(14f, 58f), new Vector2(86f, 88f));

            Button replay = CreateButton("Replay", panel, "\u4ece\u5934\u64ad\u653e", Accent, () => _app.RestartPlayback());
            SetRect(replay.GetComponent<RectTransform>(), Vector2.zero, Vector2.zero, new Vector2(96f, 58f), new Vector2(204f, 88f));

            Button view = CreateButton("View", panel, "\u89c6\u89d2", TileHot, () => _app.CameraController.CycleView());
            SetRect(view.GetComponent<RectTransform>(), Vector2.zero, Vector2.zero, new Vector2(214f, 58f), new Vector2(286f, 88f));

            _viewText = CreateText("ViewText", panel, "\u5c04\u624b\u89c6\u89d2", 12, TextAnchor.MiddleLeft);
            SetRect(_viewText.rectTransform, Vector2.zero, Vector2.zero, new Vector2(302f, 60f), new Vector2(424f, 86f));
            _viewText.color = SoftText;

            _timeText = CreateText("Time", panel, "00:00.000", 14, TextAnchor.MiddleRight);
            SetRect(_timeText.rectTransform, Vector2.zero, Vector2.zero, new Vector2(462f, 60f), new Vector2(620f, 86f));
            _timeText.fontStyle = FontStyle.Bold;

            CreateSpeedSlider(panel);

            _speedText = CreateText("SpeedText", panel, "\u901f\u5ea6 1x", 12, TextAnchor.MiddleRight);
            SetRect(_speedText.rectTransform, Vector2.zero, Vector2.zero, new Vector2(192f, 30f), new Vector2(288f, 50f));
            _speedText.color = SoftText;

            _statusText = CreateText("Status", panel, "\u5c31\u7eea", 12, TextAnchor.UpperLeft);
            SetRect(_statusText.rectTransform, Vector2.zero, Vector2.zero, new Vector2(306f, 14f), new Vector2(660f, 50f));
            _statusText.color = SoftText;
            _statusText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _statusText.verticalOverflow = VerticalWrapMode.Truncate;

            Text replayTitle = CreateText("ReplayTitle", panel, "\u56de\u653e\u63a7\u5236", 15, TextAnchor.MiddleLeft);
            SetRect(replayTitle.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(16f, -42f), new Vector2(-54f, -8f));
            replayTitle.fontStyle = FontStyle.Bold;

            _replayCollapseButton = CreateButton("CollapseReplay", panel, "\u2014", TileHot, ToggleReplayPanel);
            SetRect(_replayCollapseButton.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-44f, -39f), new Vector2(-10f, -7f));
            AddTopAccent(panel);
            ApplyReplayPanelState();
        }

        private void ToggleControlPanel()
        {
            _controlCollapsed = !_controlCollapsed;
            ApplyControlPanelState();
        }

        private void ToggleHistoryPanel()
        {
            _historyCollapsed = !_historyCollapsed;
            ApplyHistoryPanelState();
        }

        private void ToggleReplayPanel()
        {
            _replayCollapsed = !_replayCollapsed;
            ApplyReplayPanelState();
        }

        private void ApplyControlPanelState()
        {
            SetPanelCollapsed(_controlPanel, _controlCollapsed, 290f, _controlCollapseButton, "Title", false);
        }

        private void ApplyHistoryPanelState()
        {
            SetPanelCollapsed(_historyPanel, _historyCollapsed, 520f, _historyCollapseButton, "HistoryTitle", false);
        }

        private void ApplyReplayPanelState()
        {
            SetPanelCollapsed(_replayPanel, _replayCollapsed, 104f, _replayCollapseButton, "ReplayTitle", true);
        }

        private static void SetPanelCollapsed(RectTransform panel, bool collapsed, float expandedHeight,
            Button collapseButton, string titleName, bool titleOnlyWhenCollapsed)
        {
            if (panel == null || collapseButton == null)
                return;

            Vector2 size = panel.sizeDelta;
            size.y = collapsed ? 48f : expandedHeight;
            panel.sizeDelta = size;

            for (int index = 0; index < panel.childCount; index++)
            {
                Transform child = panel.GetChild(index);
                bool isTitle = child.name == titleName;
                bool persistent = isTitle || child.name == "TopAccent" || child == collapseButton.transform;
                child.gameObject.SetActive(collapsed ? persistent : !(titleOnlyWhenCollapsed && isTitle));
            }

            Text label = collapseButton.GetComponentInChildren<Text>(true);
            if (label != null)
                label.text = collapsed ? "+" : "\u2014";
            collapseButton.transform.SetAsLastSibling();
        }

        private static void AddTopAccent(RectTransform panel)
        {
            RectTransform accent = CreatePanel("TopAccent", panel, new Color(0.18f, 0.78f, 0.66f, 0.90f));
            SetRect(accent, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -3f), Vector2.zero);
            accent.SetAsFirstSibling();
        }

        private void CreateHistoryRow(CsvHistoryEntry entry)
        {
            RectTransform row = CreatePanel("History_" + entry.Id, _historyContent, Tile);
            LayoutElement rowLayout = row.gameObject.AddComponent<LayoutElement>();
            rowLayout.preferredHeight = 76f;
            rowLayout.minHeight = 76f;
            AddOutline(row.gameObject, 0.055f);

            string resultText = string.IsNullOrEmpty(entry.LastResult) ? "\u672a\u64ad\u653e" : entry.LastResult;
            Color resultColor = string.IsNullOrEmpty(entry.LastResult) ? DimText
                : entry.LastResult == CsvHistoryStore.ResultSaved ? new Color(0.38f, 0.92f, 0.62f, 1f)
                : entry.LastResult == CsvHistoryStore.ResultGoal ? Gold
                : entry.LastResult == CsvHistoryStore.ResultMiss ? new Color(0.92f, 0.62f, 0.38f, 1f)
                : SoftText;

            Text name = CreateText("Name", row, entry.DisplayName, 13, TextAnchor.MiddleLeft);
            SetRect(name.rectTransform, Vector2.zero, Vector2.one, new Vector2(12f, 38f), new Vector2(-154f, -8f));
            name.fontStyle = FontStyle.Bold;
            name.horizontalOverflow = HorizontalWrapMode.Wrap;
            name.verticalOverflow = VerticalWrapMode.Truncate;

            Text result = CreateText("Result", row, resultText, 11, TextAnchor.MiddleRight);
            SetRect(result.rectTransform, Vector2.zero, Vector2.one, new Vector2(166f, 40f), new Vector2(-50f, -10f));
            result.color = resultColor;
            result.fontStyle = FontStyle.Bold;

            string meta = string.IsNullOrEmpty(entry.ImportedAt) ? "\u672a\u77e5\u65f6\u95f4" : entry.ImportedAt;
            if (!string.IsNullOrEmpty(entry.LastPlayedAt))
                meta += " | " + entry.LastEventTime.ToString("0.000") + "s";
            Text date = CreateText("Date", row, meta, 11, TextAnchor.MiddleLeft);
            SetRect(date.rectTransform, Vector2.zero, Vector2.one, new Vector2(12f, 12f), new Vector2(-50f, -42f));
            date.color = DimText;
            date.horizontalOverflow = HorizontalWrapMode.Wrap;
            date.verticalOverflow = VerticalWrapMode.Truncate;

            string id = entry.Id;
            Button load = CreateTransparentButton("Load", row, () => _app.PlayHistory(id));
            Stretch(load.GetComponent<RectTransform>(), 0f);
            load.transform.SetAsFirstSibling();

            Button delete = CreateButton("Delete", row, "\u5220", Danger, () => _app.DeleteHistory(id));
            SetRect(delete.GetComponent<RectTransform>(), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-38f, -15f), new Vector2(-8f, 15f));
        }

        private void UpdateStatistics()
        {
            if (_statisticsText == null || _app == null || _app.History == null)
                return;
            CsvHistoryStore history = _app.History;
            _statisticsText.text = "\u4e0a\u4f20 " + history.UploadedCount + "   \u5df2\u5b8c\u6210 " + history.CompletedCount
                + "\n\u8fdb\u7403 " + history.GoalCount + "   \u6251\u51fa " + history.SavedCount + "   \u5c04\u504f "
                + history.MissCount + "   \u547d\u4e2d\u76ee\u6807 " + history.OnTargetRate.ToString("0.0") + "%";
        }

        private void ForceHistoryLayout()
        {
            if (_historyContent == null)
                return;
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(_historyContent);
            Canvas.ForceUpdateCanvases();
        }

        private void DrawGoalkeeperRadar(GoalkeeperData data)
        {
            if (_goalkeeperRadarTexture == null)
                return;
            ClearRadarTexture();
            if (data == null)
            {
                _goalkeeperRadarTexture.Apply();
                return;
            }

            float[] values =
            {
                Mathf.InverseLerp(2f, 6f, data.DiveSpeed),
                Mathf.InverseLerp(0.3f, 0.7f, data.Reach),
                Mathf.InverseLerp(0.3f, 0.8f, data.JumpHeight),
                Mathf.InverseLerp(0.5f, 0.95f, data.GoalKeeping),
                Mathf.InverseLerp(1.7f, 2.1f, data.Height)
            };

            int size = _goalkeeperRadarTexture.width;
            int center = size / 2;
            float radius = size * 0.36f;
            Color grid = new Color(0.42f, 0.52f, 0.62f, 0.55f);
            Color line = new Color(0.30f, 0.90f, 0.70f, 1f);
            Color fill = new Color(0.10f, 0.58f, 0.86f, 0.38f);

            for (int ring = 1; ring <= 3; ring++)
                DrawRadarCircle(center, center, radius * ring / 3f, grid);

            Vector2[] polygon = new Vector2[5];
            for (int index = 0; index < 5; index++)
            {
                float angle = -Mathf.PI * 0.5f + index * Mathf.PI * 2f / 5f;
                DrawRadarLine(center, center,
                    center + Mathf.RoundToInt(radius * Mathf.Cos(angle)),
                    center + Mathf.RoundToInt(radius * Mathf.Sin(angle)), grid);
                float valueRadius = radius * Mathf.Clamp01(values[index]);
                polygon[index] = new Vector2(center + valueRadius * Mathf.Cos(angle), center + valueRadius * Mathf.Sin(angle));
            }

            FillRadarPolygon(polygon, fill);
            for (int index = 0; index < polygon.Length; index++)
            {
                Vector2 a = polygon[index];
                Vector2 b = polygon[(index + 1) % polygon.Length];
                DrawRadarLine(Mathf.RoundToInt(a.x), Mathf.RoundToInt(a.y), Mathf.RoundToInt(b.x), Mathf.RoundToInt(b.y), line);
            }
            _goalkeeperRadarTexture.Apply();
        }

        private void ClearRadarTexture()
        {
            if (_goalkeeperRadarTexture == null)
                return;
            Color[] pixels = _goalkeeperRadarTexture.GetPixels();
            for (int index = 0; index < pixels.Length; index++)
                pixels[index] = Color.clear;
            _goalkeeperRadarTexture.SetPixels(pixels);
        }

        private void DrawRadarLine(int x0, int y0, int x1, int y1, Color color)
        {
            int dx = Mathf.Abs(x1 - x0);
            int dy = Mathf.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;

            while (true)
            {
                SetRadarPixel(x0, y0, color);
                if (x0 == x1 && y0 == y1)
                    break;
                int e2 = err * 2;
                if (e2 > -dy)
                {
                    err -= dy;
                    x0 += sx;
                }
                if (e2 < dx)
                {
                    err += dx;
                    y0 += sy;
                }
            }
        }

        private void DrawRadarCircle(int cx, int cy, float radius, Color color)
        {
            const int steps = 48;
            for (int index = 0; index < steps; index++)
            {
                float a0 = index * Mathf.PI * 2f / steps;
                float a1 = (index + 1) * Mathf.PI * 2f / steps;
                DrawRadarLine(
                    cx + Mathf.RoundToInt(radius * Mathf.Cos(a0)),
                    cy + Mathf.RoundToInt(radius * Mathf.Sin(a0)),
                    cx + Mathf.RoundToInt(radius * Mathf.Cos(a1)),
                    cy + Mathf.RoundToInt(radius * Mathf.Sin(a1)),
                    color);
            }
        }
        private void FillRadarPolygon(Vector2[] polygon, Color color)
        {
            int minX = _goalkeeperRadarTexture.width;
            int minY = _goalkeeperRadarTexture.height;
            int maxX = 0;
            int maxY = 0;
            for (int index = 0; index < polygon.Length; index++)
            {
                minX = Mathf.Min(minX, Mathf.FloorToInt(polygon[index].x));
                minY = Mathf.Min(minY, Mathf.FloorToInt(polygon[index].y));
                maxX = Mathf.Max(maxX, Mathf.CeilToInt(polygon[index].x));
                maxY = Mathf.Max(maxY, Mathf.CeilToInt(polygon[index].y));
            }

            minX = Mathf.Clamp(minX, 0, _goalkeeperRadarTexture.width - 1);
            minY = Mathf.Clamp(minY, 0, _goalkeeperRadarTexture.height - 1);
            maxX = Mathf.Clamp(maxX, 0, _goalkeeperRadarTexture.width - 1);
            maxY = Mathf.Clamp(maxY, 0, _goalkeeperRadarTexture.height - 1);

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (PointInPolygon(new Vector2(x + 0.5f, y + 0.5f), polygon))
                        SetRadarPixel(x, y, color);
                }
            }
        }

        private static bool PointInPolygon(Vector2 point, Vector2[] polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                bool crosses = (polygon[i].y > point.y) != (polygon[j].y > point.y);
                if (crosses)
                {
                    float x = (polygon[j].x - polygon[i].x) * (point.y - polygon[i].y) / (polygon[j].y - polygon[i].y) + polygon[i].x;
                    if (point.x < x)
                        inside = !inside;
                }
            }
            return inside;
        }

        private void SetRadarPixel(int x, int y, Color color)
        {
            if (_goalkeeperRadarTexture == null)
                return;
            if (x < 0 || y < 0 || x >= _goalkeeperRadarTexture.width || y >= _goalkeeperRadarTexture.height)
                return;
            _goalkeeperRadarTexture.SetPixel(x, y, color);
        }

        private void CreateSpeedSlider(Transform parent)
        {
            RectTransform root = CreateRect("SpeedSlider", parent);
            SetRect(root, Vector2.zero, Vector2.zero, new Vector2(18f, 22f), new Vector2(182f, 46f));

            Image background = CreateRect("Background", root).gameObject.AddComponent<Image>();
            Stretch(background.rectTransform, 0f);
            background.color = new Color(0.010f, 0.014f, 0.020f, 0.90f);
            background.raycastTarget = true;

            RectTransform fillArea = CreateRect("FillArea", root);
            Stretch(fillArea, 4f);

            Image fill = CreateRect("Fill", fillArea).gameObject.AddComponent<Image>();
            Stretch(fill.rectTransform, 0f);
            fill.color = AccentBright;
            fill.raycastTarget = false;

            Image handle = CreateRect("Handle", root).gameObject.AddComponent<Image>();
            SetRect(handle.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(-7f, -8f), new Vector2(7f, 8f));
            handle.color = MainText;
            handle.raycastTarget = true;

            _speedSlider = root.gameObject.AddComponent<Slider>();
            _speedSlider.minValue = 0.25f;
            _speedSlider.maxValue = 2f;
            _speedSlider.wholeNumbers = false;
            _speedSlider.direction = Slider.Direction.LeftToRight;
            _speedSlider.fillRect = fill.rectTransform;
            _speedSlider.handleRect = handle.rectTransform;
            _speedSlider.targetGraphic = handle;
            _speedSlider.value = Mathf.Clamp(_app != null ? _app.PlaybackSpeed : 1f, 0.25f, 2f);
            _speedSlider.onValueChanged.AddListener(value =>
            {
                float rounded = Mathf.Round(value * 4f) / 4f;
                if (_app != null)
                    _app.SetPlaybackSpeed(rounded);
            });
        }

        private void OnViewChanged(string viewName)
        {
            if (_viewText == null)
                return;
            switch (viewName)
            {
                case "Shooter view":
                    _viewText.text = "\u5c04\u624b\u89c6\u89d2";
                    break;
                case "Behind goal":
                    _viewText.text = "\u7403\u95e8\u540e\u65b9";
                    break;
                case "Side view":
                    _viewText.text = "\u4fa7\u9762\u89c6\u89d2";
                    break;
                case "Ball follow":
                    _viewText.text = "\u8ddf\u968f\u7403";
                    break;
                default:
                    _viewText.text = viewName;
                    break;
            }
        }

        private Button CreateButton(string name, Transform parent, string label, Color color, UnityEngine.Events.UnityAction action)
        {
            RectTransform rect = CreateRect(name, parent);
            Image image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = true;
            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(action);
            ColorBlock colors = button.colors;
            colors.normalColor = color;
            colors.highlightedColor = Color.Lerp(color, Color.white, 0.10f);
            colors.pressedColor = Color.Lerp(color, Color.black, 0.18f);
            colors.disabledColor = new Color(color.r, color.g, color.b, 0.35f);
            colors.colorMultiplier = 1f;
            button.colors = colors;
            Navigation navigation = button.navigation;
            navigation.mode = Navigation.Mode.None;
            button.navigation = navigation;
            AddOutline(rect.gameObject, 0.04f);

            Text text = CreateText("Label", rect, label, 12, TextAnchor.MiddleCenter);
            Stretch(text.rectTransform, 3f);
            text.fontStyle = FontStyle.Bold;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return button;
        }

        private Button CreateTransparentButton(string name, Transform parent, UnityEngine.Events.UnityAction action)
        {
            RectTransform rect = CreateRect(name, parent);
            Image image = rect.gameObject.AddComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0.001f);
            image.raycastTarget = true;
            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(action);
            Navigation navigation = button.navigation;
            navigation.mode = Navigation.Mode.None;
            button.navigation = navigation;
            return button;
        }

        private Text CreateText(string name, Transform parent, string text, int size, TextAnchor alignment)
        {
            RectTransform rect = CreateRect(name, parent);
            Text label = rect.gameObject.AddComponent<Text>();
            label.font = UiFont;
            label.text = text;
            label.fontSize = size;
            label.alignment = alignment;
            label.color = MainText;
            label.raycastTarget = false;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            return label;
        }

        private static RectTransform CreatePanel(string name, Transform parent, Color color)
        {
            RectTransform rect = CreateRect(name, parent);
            Image image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return rect;
        }

        private static RectTransform CreateRect(string name, Transform parent)
        {
            GameObject gameObject = new GameObject(name, typeof(RectTransform));
            gameObject.layer = parent == null ? 5 : parent.gameObject.layer;
            gameObject.transform.SetParent(parent, false);
            return gameObject.GetComponent<RectTransform>();
        }

        private static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        private static void Stretch(RectTransform rect, float padding)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(padding, padding);
            rect.offsetMax = new Vector2(-padding, -padding);
        }

        private static void AddChrome(GameObject target, float outlineAlpha)
        {
            Shadow shadow = target.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.38f);
            shadow.effectDistance = new Vector2(0f, -3f);
            AddOutline(target, outlineAlpha);
        }

        private static void AddOutline(GameObject target, float alpha)
        {
            Outline outline = target.AddComponent<Outline>();
            outline.effectColor = new Color(1f, 1f, 1f, alpha);
            outline.effectDistance = new Vector2(1f, -1f);
        }

        private static void EnsureEventSystem()
        {
            EventSystem[] systems = FindObjectsOfType<EventSystem>(true);
            EventSystem eventSystem = null;
            for (int index = 0; index < systems.Length; index++)
            {
                EventSystem candidate = systems[index];
                if (candidate == null)
                    continue;
                if (eventSystem == null)
                {
                    eventSystem = candidate;
                    candidate.gameObject.SetActive(true);
                    candidate.enabled = true;
                }
                else
                {
                    candidate.enabled = false;
                }
            }
            if (eventSystem == null)
            {
                GameObject eventObject = new GameObject("EventSystem", typeof(EventSystem));
                eventSystem = eventObject.GetComponent<EventSystem>();
            }

            BaseInputModule inputModule = eventSystem.GetComponent<BaseInputModule>();
            if (inputModule == null)
                inputModule = eventSystem.gameObject.AddComponent<StandaloneInputModule>();
            inputModule.enabled = true;
            EventSystem.current = eventSystem;
        }

        public void DisableCompetingCanvases()
        {
            Canvas[] canvases = FindObjectsOfType<Canvas>();
            for (int index = 0; index < canvases.Length; index++)
            {
                Canvas canvas = canvases[index];
                if (canvas == null || canvas == _canvas)
                    continue;
                if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                    canvas.enabled = false;
            }
        }

        private static void SetLayerRecursively(GameObject gameObject, int layer)
        {
            gameObject.layer = layer;
            for (int index = 0; index < gameObject.transform.childCount; index++)
                SetLayerRecursively(gameObject.transform.GetChild(index).gameObject, layer);
        }

        private static string FormatTime(float seconds)
        {
            if (seconds < 0f)
                seconds = 0f;
            int minutes = Mathf.FloorToInt(seconds / 60f);
            float rest = seconds - minutes * 60f;
            return minutes.ToString("00") + ":" + rest.ToString("00.000");
        }
    }
}
