using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AIfootball.App.Models;
using AIfootball.Core.Models;

namespace AIfootball.App.Views.Dialogs;

/// <summary>
/// 门将属性编辑器。属性栏与 3×5 概率表都是按 <see cref="GoalkeeperStats"/> 现算出来的，
/// 存回去的就是 Unity 播放器读的那份 JSON，所以改完立刻影响回放。
/// </summary>
public partial class GoalkeeperEditorWindow : Window
{
    private readonly GoalkeeperStats _stats;
    private readonly List<AttributeField> _attributes = new();
    private readonly List<(string Key, TextBox Box)> _zones = new();

    /// <summary>保存时用的门将名；与打开时的名字不同即为另存。</summary>
    public string ResultName { get; private set; } = "";

    /// <summary>编辑后的副本。</summary>
    public GoalkeeperStats ResultStats => _stats;

    /// <summary>是否点了保存（取消/关窗为 false）。</summary>
    public bool Saved { get; private set; }

    public GoalkeeperEditorWindow(string name, GoalkeeperStats stats)
    {
        InitializeComponent();

        _stats = stats.Clone();
        ResultName = name;
        NameBox.Text = name;
        DisplayNameBox.Text = _stats.DisplayName;
        DescriptionBox.Text = _stats.Description;

        BuildAttributes();
        BuildZones();
    }

    private sealed record AttributeField(string Label, Action<float> Set, TextBox Box);

    /// <summary>另存时预填一个不冲突的名称。</summary>
    public void SuggestName(string suggestion)
    {
        NameBox.Text = suggestion;
        NameBox.SelectAll();
        NameBox.Focus();
    }

    private void BuildAttributes()
    {
        var specs = new (string Label, Func<float> Get, Action<float> Set)[]
        {
            ("身高 (m)", () => _stats.Height, v => _stats.Height = v),
            ("扑救速度 (m/s)", () => _stats.DiveSpeed, v => _stats.DiveSpeed = v),
            ("扑救距离 (m)", () => _stats.JumpDistance, v => _stats.JumpDistance = v),
            ("起跳高度 (m)", () => _stats.JumpHeight, v => _stats.JumpHeight = v),
            ("臂展 (m)", () => _stats.Reach, v => _stats.Reach = v),
            ("站位速度 (m/s)", () => _stats.TendGoalSpeed, v => _stats.TendGoalSpeed = v),
            ("站位距离 (m)", () => _stats.TendGoalDistance, v => _stats.TendGoalDistance = v),
            ("扑救能力 (0-1)", () => _stats.GoalKeeping, v => _stats.GoalKeeping = v),
            ("横向偏好", () => _stats.SidePreference, v => _stats.SidePreference = v),
            ("高度偏好", () => _stats.HeightPreference, v => _stats.HeightPreference = v),
        };

        // 两列：标签 / 输入框 / 间隔 / 标签 / 输入框
        AttributeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AttributeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
        AttributeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        AttributeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AttributeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });

        var rows = (specs.Length + 1) / 2;
        for (var row = 0; row < rows; row++)
            AttributeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var index = 0; index < specs.Length; index++)
        {
            var row = index / 2;
            var slot = index % 2;
            var labelColumn = slot * 3;
            var spec = specs[index];

            var label = new TextBlock
            {
                Text = spec.Label,
                FontSize = 11,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 8),
                Width = 92,
            };
            Grid.SetRow(label, row);
            Grid.SetColumn(label, labelColumn);
            AttributeGrid.Children.Add(label);

            var box = new TextBox
            {
                Style = (Style)FindResource("ModernTextBox"),
                Text = spec.Get().ToString("0.###", CultureInfo.InvariantCulture),
                Height = 30,
                Padding = new Thickness(8, 4, 8, 4),
                FontSize = 12,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8),
            };
            Grid.SetRow(box, row);
            Grid.SetColumn(box, labelColumn + 1);
            AttributeGrid.Children.Add(box);

            _attributes.Add(new AttributeField(spec.Label, spec.Set, box));
        }
    }

    private void BuildZones()
    {
        var columnHeaders = new[] { "左", "中左", "中", "中右", "右" };
        var rowHeaders = new[] { "高", "中", "低" };

        ZoneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        for (var column = 0; column < GoalGeometry.Columns.Length; column++)
            ZoneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });

        for (var row = 0; row <= GoalGeometry.Rows.Length; row++)
            ZoneGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var column = 0; column < columnHeaders.Length; column++)
        {
            var header = new TextBlock
            {
                Text = columnHeaders[column],
                FontSize = 11,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 5),
            };
            Grid.SetRow(header, 0);
            Grid.SetColumn(header, column + 1);
            ZoneGrid.Children.Add(header);
        }

        for (var displayRow = 0; displayRow < GoalGeometry.Rows.Length; displayRow++)
        {
            // 表里第 0 行是"低"（贴地），界面上要从上往下排，所以倒过来取
            var dataRow = GoalGeometry.Rows.Length - 1 - displayRow;

            var rowHeader = new TextBlock
            {
                Text = rowHeaders[displayRow],
                FontSize = 11,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(rowHeader, displayRow + 1);
            Grid.SetColumn(rowHeader, 0);
            ZoneGrid.Children.Add(rowHeader);

            for (var column = 0; column < GoalGeometry.Columns.Length; column++)
            {
                var key = GoalGeometry.Rows[dataRow] + GoalGeometry.Columns[column];
                var box = new TextBox
                {
                    Style = (Style)FindResource("ModernTextBox"),
                    Text = (_stats.ProbabilityMap.TryGetValue(key, out var value) ? value : 0f)
                        .ToString("0.##", CultureInfo.InvariantCulture),
                    Height = 30,
                    Padding = new Thickness(8, 4, 8, 4),
                    FontSize = 12,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 4, 6),
                };
                Grid.SetRow(box, displayRow + 1);
                Grid.SetColumn(box, column + 1);
                ZoneGrid.Children.Add(box);

                _zones.Add((key, box));
            }
        }
    }

    private static bool TryParseNumber(string text, out float value) =>
        float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && !float.IsNaN(value) && !float.IsInfinity(value);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "";

        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            StatusText.Text = "门将名称不能为空";
            NameBox.Focus();
            return;
        }

        // 先全部校验、再统一写回，避免半套数据被改掉
        var values = new float[_attributes.Count];
        for (var index = 0; index < _attributes.Count; index++)
        {
            var field = _attributes[index];
            if (!TryParseNumber(field.Box.Text, out var value))
            {
                StatusText.Text = $"「{field.Label}」需要一个数字";
                field.Box.Focus();
                return;
            }

            if (value < 0)
            {
                StatusText.Text = $"「{field.Label}」不能为负数";
                field.Box.Focus();
                return;
            }

            values[index] = value;
        }

        var map = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var (key, box) in _zones)
        {
            if (!TryParseNumber(box.Text, out var value))
            {
                StatusText.Text = $"区域「{key}」需要一个 0–1 的数字";
                box.Focus();
                return;
            }

            if (value is < 0 or > 1)
            {
                StatusText.Text = $"区域「{key}」必须落在 0–1 之间";
                box.Focus();
                return;
            }

            map[key] = value;
        }

        for (var index = 0; index < _attributes.Count; index++)
            _attributes[index].Set(values[index]);

        _stats.DisplayName = DisplayNameBox.Text.Trim();
        _stats.Description = DescriptionBox.Text.Trim();
        _stats.ProbabilityMap = map;

        ResultName = name;
        Saved = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }
}
