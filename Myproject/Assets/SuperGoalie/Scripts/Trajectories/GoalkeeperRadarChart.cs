using Assets.SuperGoalie.Scripts.Data;
using UnityEngine;
using UnityEngine.UI;

namespace Assets.SuperGoalie.Scripts.Trajectories
{
    /// <summary>
    /// 五维雷达图 — 显示门将的速度/臂展/弹跳/反应/身高五项属性
    /// 位置：屏幕右上角
    /// </summary>
    public sealed class GoalkeeperRadarChart : MonoBehaviour
    {
        const int AXIS_COUNT = 5;

        string[] _axisNames = { "速度", "臂展", "弹跳", "反应", "身高" };
        float[] _values = new float[AXIS_COUNT];

        RawImage _image;
        Texture2D _tex;
        int _size = 160;
        Text[] _axisLabels = new Text[AXIS_COUNT];
        Text _nameLabel;

        public static GoalkeeperRadarChart Create(Transform canvasParent, Font font)
        {
            GameObject go = new GameObject("GoalkeeperRadarChart", typeof(RectTransform));
            go.transform.SetParent(canvasParent, false);

            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(-16f, -16f);
            rt.sizeDelta = new Vector2(200f, 200f);

            // 背景
            GameObject bg = new GameObject("Bg", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(go.transform, false);
            bg.GetComponent<Image>().color = new Color(0.035f, 0.055f, 0.085f, 0.85f);
            RectTransform bgRt = bg.GetComponent<RectTransform>();
            bgRt.anchorMin = Vector2.zero; bgRt.anchorMax = Vector2.one;
            bgRt.sizeDelta = Vector2.zero;

            // 标题
            GameObject titleGo = new GameObject("Title", typeof(RectTransform), typeof(Text));
            titleGo.transform.SetParent(go.transform, false);
            Text title = titleGo.GetComponent<Text>();
            title.font = font ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            title.fontSize = 14;
            title.fontStyle = FontStyle.Bold;
            title.color = Color.white;
            title.alignment = TextAnchor.MiddleCenter;
            title.text = "门将属性";
            RectTransform titleRt = titleGo.GetComponent<RectTransform>();
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.anchoredPosition = new Vector2(0f, -6f);
            titleRt.sizeDelta = new Vector2(0f, 22f);

            // 雷达图贴图
            GameObject imgGo = new GameObject("RadarTex", typeof(RectTransform), typeof(RawImage));
            imgGo.transform.SetParent(go.transform, false);
            RawImage rawImage = imgGo.GetComponent<RawImage>();
            RectTransform imgRt = imgGo.GetComponent<RectTransform>();
            imgRt.anchorMin = Vector2.zero;
            imgRt.anchorMax = Vector2.one;
            imgRt.offsetMin = new Vector2(4f, 4f);
            imgRt.offsetMax = new Vector2(-4f, -28f);

            var chart = go.AddComponent<GoalkeeperRadarChart>();
            chart._image = rawImage;
            chart._tex = new Texture2D(chart._size, chart._size, TextureFormat.RGBA32, false);
            chart._tex.filterMode = FilterMode.Point;
            rawImage.texture = chart._tex;

            // 五个轴标签
            for (int i = 0; i < AXIS_COUNT; i++)
            {
                // 标签文本
                GameObject lblGo = new GameObject($"Axis{i}", typeof(RectTransform), typeof(Text));
                lblGo.transform.SetParent(go.transform, false);
                Text lbl = lblGo.GetComponent<Text>();
                lbl.font = font ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
                lbl.fontSize = 11;
                lbl.color = new Color(0.7f, 0.7f, 0.8f);
                lbl.alignment = TextAnchor.MiddleCenter;
                lbl.text = chart._axisNames[i];
                chart._axisLabels[i] = lbl;
            }

            // 门将名字（底部）
            GameObject nameGo = new GameObject("GkLabel", typeof(RectTransform), typeof(Text));
            nameGo.transform.SetParent(go.transform, false);
            chart._nameLabel = nameGo.GetComponent<Text>();
            chart._nameLabel.font = font ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            chart._nameLabel.fontSize = 12;
            chart._nameLabel.fontStyle = FontStyle.Bold;
            chart._nameLabel.color = new Color(0.2f, 0.85f, 0.5f);
            chart._nameLabel.alignment = TextAnchor.MiddleCenter;
            chart._nameLabel.text = "";
            RectTransform nameRt = nameGo.GetComponent<RectTransform>();
            nameRt.anchorMin = Vector2.zero; nameRt.anchorMax = Vector2.one;
            nameRt.offsetMin = new Vector2(0f, -22f);
            nameRt.offsetMax = new Vector2(0f, -4f);

            // 默认灰色
            chart.ClearTexture();
            chart.UpdateLabelPositions();
            return chart;
        }

        public void Refresh(GoalkeeperData data)
        {
            if (data == null)
            {
                ClearTexture();
                if (_nameLabel != null) _nameLabel.text = "";
                return;
            }

            if (_nameLabel != null) _nameLabel.text = data.DisplayName;

            // 五维：DiveSpeed(0-6), Reach(0.3-0.7), JumpHeight(0.3-0.8), GoalKeeping(0.5-1), Height(1.7-2.1)
            _values[0] = Mathf.InverseLerp(2f, 6f, data.DiveSpeed);
            _values[1] = Mathf.InverseLerp(0.3f, 0.7f, data.Reach);
            _values[2] = Mathf.InverseLerp(0.3f, 0.8f, data.JumpHeight);
            _values[3] = Mathf.InverseLerp(0.5f, 0.95f, data.GoalKeeping);
            _values[4] = Mathf.InverseLerp(1.7f, 2.1f, data.Height);

            DrawRadar();
            UpdateLabelPositions();
        }

        void UpdateLabelPositions()
        {
            float maxR = _size * 0.38f;
            float cx = _size * 0.5f, cy = _size * 0.5f;

            // 贴图在 RawImage 里的偏移（因为 RawImage 区域可能和 _size 不同）
            Rect imgRect = _image.rectTransform.rect;
            float scaleX = imgRect.width / _size;
            float scaleY = imgRect.height / _size;
            float ox = _image.rectTransform.offsetMin.x;
            float oy = _image.rectTransform.offsetMin.y;

            for (int i = 0; i < AXIS_COUNT; i++)
            {
                if (_axisLabels[i] == null) continue;

                float angle = -Mathf.PI / 2f + i * 2f * Mathf.PI / AXIS_COUNT;
                // 标签放在顶点外侧 15 像素
                float r = maxR + 18f;
                float px = ox + (cx + r * Mathf.Cos(angle)) * scaleX;
                float py = oy + (cy + r * Mathf.Sin(angle)) * scaleY;

                RectTransform rt = _axisLabels[i].rectTransform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.zero;
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = new Vector2(px, py);
                rt.sizeDelta = new Vector2(40f, 18f);
            }
        }

        void ClearTexture()
        {
            Color[] pixels = _tex.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = Color.clear;
            _tex.SetPixels(pixels);
            _tex.Apply();
        }

        void DrawRadar()
        {
            ClearTexture();

            int cx = _size / 2, cy = _size / 2;
            float maxR = _size * 0.38f;

            // 画网格（三层参考圆 + 轴线）
            for (int ring = 1; ring <= 3; ring++)
                DrawCircle(cx, cy, maxR * ring / 3f, new Color(0.3f, 0.3f, 0.4f, 0.6f));

            for (int i = 0; i < AXIS_COUNT; i++)
            {
                float angle = -Mathf.PI / 2f + i * 2f * Mathf.PI / AXIS_COUNT;
                int ex = cx + (int)(maxR * Mathf.Cos(angle));
                int ey = cy + (int)(maxR * Mathf.Sin(angle));
                DrawLine(cx, cy, ex, ey, new Color(0.3f, 0.3f, 0.4f, 0.5f));
            }

            // 画属性多边形
            Vector2[] poly = new Vector2[AXIS_COUNT];
            for (int i = 0; i < AXIS_COUNT; i++)
            {
                float angle = -Mathf.PI / 2f + i * 2f * Mathf.PI / AXIS_COUNT;
                float r = maxR * Mathf.Clamp01(_values[i]);
                poly[i] = new Vector2(cx + r * Mathf.Cos(angle), cy + r * Mathf.Sin(angle));
            }
            Color fillColor = new Color(0.18f, 0.72f, 0.44f, 0.5f);
            FillPolygon(poly, fillColor);

            // 描边
            for (int i = 0; i < AXIS_COUNT; i++)
            {
                int j = (i + 1) % AXIS_COUNT;
                DrawLine((int)poly[i].x, (int)poly[i].y, (int)poly[j].x, (int)poly[j].y, new Color(0.2f, 0.85f, 0.5f, 1f));
            }

            _tex.Apply();
        }

        void DrawLine(int x0, int y0, int x1, int y1, Color color)
        {
            int dx = Mathf.Abs(x1 - x0), dy = Mathf.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;
            while (true)
            {
                if (x0 >= 0 && x0 < _size && y0 >= 0 && y0 < _size)
                    _tex.SetPixel(x0, y0, color);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
        }

        void DrawCircle(int cx, int cy, float r, Color color)
        {
            int steps = 60;
            for (int i = 0; i < steps; i++)
            {
                float a0 = i * 2f * Mathf.PI / steps;
                float a1 = (i + 1) * 2f * Mathf.PI / steps;
                int x0 = cx + (int)(r * Mathf.Cos(a0));
                int y0 = cy + (int)(r * Mathf.Sin(a0));
                int x1 = cx + (int)(r * Mathf.Cos(a1));
                int y1 = cy + (int)(r * Mathf.Sin(a1));
                DrawLine(x0, y0, x1, y1, color);
            }
        }

        void FillPolygon(Vector2[] poly, Color color)
        {
            // 简单扫描线填充
            float minY = _size, maxY = 0;
            foreach (var p in poly) { minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y); }
            for (int y = (int)minY; y <= (int)maxY; y++)
            {
                float minX = _size, maxX = 0;
                for (int i = 0; i < poly.Length; i++)
                {
                    int j = (i + 1) % poly.Length;
                    Vector2 a = poly[i], b = poly[j];
                    if ((a.y <= y && b.y > y) || (b.y <= y && a.y > y))
                    {
                        float t = (y - a.y) / (b.y - a.y);
                        float x = a.x + t * (b.x - a.x);
                        minX = Mathf.Min(minX, x);
                        maxX = Mathf.Max(maxX, x);
                    }
                }
                for (int x = (int)minX; x <= (int)maxX; x++)
                    if (x >= 0 && x < _size && y >= 0 && y < _size)
                        _tex.SetPixel(x, y, color);
            }
        }
    }
}
