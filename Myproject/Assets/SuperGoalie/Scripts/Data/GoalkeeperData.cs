using UnityEngine;

namespace Assets.SuperGoalie.Scripts.Data
{
    /// <summary>
    /// 门将数据库条目 — 定义门将的物理参数和扑救概率分布。
    /// 支持从 JSON 文件加载（运行时）和 ScriptableObject（编辑器）。
    /// </summary>
    [CreateAssetMenu(fileName = "NewGoalkeeper", menuName = "Football/Goalkeeper Data")]
    public class GoalkeeperData : ScriptableObject
    {
        [Header("基本信息")]
        public string DisplayName = "门将";
        [TextArea(2, 4)]
        public string Description = "默认门将";

        [Header("身体参数")]
        [Tooltip("身高（米）")]
        public float Height = 1.9f;
        [Tooltip("扑救最大速度（米/秒）")]
        public float DiveSpeed = 4f;
        [Tooltip("水平飞扑最大距离（米）")]
        public float JumpDistance = 1f;
        [Tooltip("垂直弹跳最大额外高度（米）")]
        public float JumpHeight = 0.5f;
        [Tooltip("手臂触及范围（米）")]
        public float Reach = 0.5f;
        [Tooltip("站位移速（米/秒）")]
        public float TendGoalSpeed = 3f;
        [Tooltip("站位距离球门线（米）")]
        public float TendGoalDistance = 3f;
        [Tooltip("站位能力 0-1，越高反应越快、噪声越小")]
        [Range(0f, 1f)]
        public float GoalKeeping = 0.85f;

        [Header("扑救风格")]
        [Tooltip("偏好扑救方向：-1=左侧优先, 0=均衡, 1=右侧优先")]
        [Range(-1f, 1f)]
        public float SidePreference = 0f;
        [Tooltip("偏好扑救高度：-1=低球优先, 0=均衡, 1=高球优先")]
        [Range(-1f, 1f)]
        public float HeightPreference = 0f;

        [Header("球门覆盖热力图 (5×3 网格)")]
        [Tooltip("门框划分为 5列×3行 的网格，每个单元格表示球出现在该位置时门将扑救成功的概率 (0-1)。\n行: 低/中/高, 列: 最左/左/中/右/最右")]
        public SaveProbabilityMap ProbabilityMap = SaveProbabilityMap.CreateDefault();

        /// <summary>
        /// 根据球在门框内的归一化位置 (x: 0=左门柱, 1=右门柱; y: 0=地面, 1=横梁)
        /// 查询扑救成功率
        /// </summary>
        public float GetSaveProbability(float normalizedX, float normalizedY)
        {
            return ProbabilityMap.Sample(normalizedX, normalizedY);
        }

        /// <summary>
        /// 根据球的三维位置（球门本地坐标）查询扑救成功率
        /// </summary>
        public float GetSaveProbability(Vector3 ballPositionRelativeToGoal, float goalWidth, float goalHeight)
        {
            float nx = Mathf.InverseLerp(-goalWidth * 0.5f, goalWidth * 0.5f, ballPositionRelativeToGoal.x);
            float ny = Mathf.InverseLerp(0f, goalHeight, ballPositionRelativeToGoal.y);
            return GetSaveProbability(nx, ny);
        }

        /// <summary>
        /// 应用数据到目标 GoalKeeper 组件
        /// </summary>
        public void ApplyTo(Entities.GoalKeeper keeper)
        {
            keeper.DiveSpeed = DiveSpeed;
            keeper.JumpDistance = JumpDistance;
            keeper.JumpHeight = JumpHeight;
            keeper.Reach = Reach;
            keeper.TendGoalSpeed = TendGoalSpeed;
            keeper.TendGoalDistance = TendGoalDistance;
            keeper.GoalKeeping = GoalKeeping;
            keeper.Height = Height;
        }

        /// <summary>
        /// 从 JSON 文件加载门将数据
        /// </summary>
        public static GoalkeeperData LoadFromJson(string jsonPath)
        {
            if (!System.IO.File.Exists(jsonPath))
            {
                Debug.LogError($"[GoalkeeperData] JSON file not found: {jsonPath}");
                return null;
            }

            string json = System.IO.File.ReadAllText(jsonPath);
            var data = ScriptableObject.CreateInstance<GoalkeeperData>();
            JsonUtility.FromJsonOverwrite(json, data);
            return data;
        }

        /// <summary>
        /// 保存为 JSON 文件
        /// </summary>
        public void SaveToJson(string jsonPath)
        {
            string json = JsonUtility.ToJson(this, true);
            System.IO.File.WriteAllText(jsonPath, json);
        }

        /// <summary>
        /// 扫描目录下所有门将 JSON 文件，返回名称列表
        /// </summary>
        public static System.Collections.Generic.List<string> ListAvailableGoalkeepers(string directoryPath)
        {
            var result = new System.Collections.Generic.List<string>();
            if (!System.IO.Directory.Exists(directoryPath)) return result;

            foreach (string file in System.IO.Directory.GetFiles(directoryPath, "*.json"))
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(file);
                if (!result.Contains(name))
                    result.Add(name);
            }
            result.Sort();
            return result;
        }
    }

    /// <summary>
    /// 球门覆盖概率热力图 (5列 × 3行)
    /// </summary>
    [System.Serializable]
    public struct SaveProbabilityMap
    {
        [Tooltip("低球 (地面附近)  — 左到右 5列")]
        public float LowLeft, LowMidLeft, LowCenter, LowMidRight, LowRight;
        [Tooltip("中球 (半高)      — 左到右 5列")]
        public float MidLeft, MidMidLeft, MidCenter, MidMidRight, MidRight;
        [Tooltip("高球 (横梁附近)  — 左到右 5列")]
        public float HighLeft, HighMidLeft, HighCenter, HighMidRight, HighRight;

        /// <summary>
        /// 采样：输入归一化坐标 (0-1)，输出概率 (0-1)
        /// </summary>
        public float Sample(float normalizedX, float normalizedY)
        {
            // 钳制到 [0,1]
            normalizedX = Mathf.Clamp01(normalizedX);
            normalizedY = Mathf.Clamp01(normalizedY);

            // 5 列: 0.0-0.2, 0.2-0.4, 0.4-0.6, 0.6-0.8, 0.8-1.0
            // 3 行: 0.0-0.33, 0.33-0.67, 0.67-1.0
            int col = Mathf.Clamp(Mathf.FloorToInt(normalizedX * 5f), 0, 4);
            int row = Mathf.Clamp(Mathf.FloorToInt(normalizedY * 3f), 0, 2);

            return GetCell(row, col);
        }

        float GetCell(int row, int col)
        {
            return (row, col) switch
            {
                (0, 0) => LowLeft,    (0, 1) => LowMidLeft,  (0, 2) => LowCenter,  (0, 3) => LowMidRight,  (0, 4) => LowRight,
                (1, 0) => MidLeft,    (1, 1) => MidMidLeft,  (1, 2) => MidCenter,  (1, 3) => MidMidRight,  (1, 4) => MidRight,
                (2, 0) => HighLeft,   (2, 1) => HighMidLeft, (2, 2) => HighCenter, (2, 3) => HighMidRight, (2, 4) => HighRight,
                _ => 0f
            };
        }

        /// <summary>
        /// 创建一个默认的"全能型"门将（全区域 0.85 概率）
        /// </summary>
        public static SaveProbabilityMap CreateDefault()
        {
            return new SaveProbabilityMap
            {
                LowLeft = 0.85f, LowMidLeft = 0.88f, LowCenter = 0.92f, LowMidRight = 0.88f, LowRight = 0.85f,
                MidLeft = 0.80f, MidMidLeft = 0.83f, MidCenter = 0.88f, MidMidRight = 0.83f, MidRight = 0.80f,
                HighLeft = 0.60f, HighMidLeft = 0.65f, HighCenter = 0.72f, HighMidRight = 0.65f, HighRight = 0.60f,
            };
        }

        /// <summary>
        /// 创建一个"扑救高手"（擅长扑救，死角较弱）
        /// </summary>
        public static SaveProbabilityMap CreateElite()
        {
            return new SaveProbabilityMap
            {
                LowLeft = 0.92f, LowMidLeft = 0.94f, LowCenter = 0.96f, LowMidRight = 0.94f, LowRight = 0.92f,
                MidLeft = 0.88f, MidMidLeft = 0.90f, MidCenter = 0.94f, MidMidRight = 0.90f, MidRight = 0.88f,
                HighLeft = 0.72f, HighMidLeft = 0.78f, HighCenter = 0.85f, HighMidRight = 0.78f, HighRight = 0.72f,
            };
        }

        /// <summary>
        /// 创建一个"有明显弱点的门将"（右下角是盲区）
        /// </summary>
        public static SaveProbabilityMap CreateWeakBottomRight()
        {
            return new SaveProbabilityMap
            {
                LowLeft = 0.82f, LowMidLeft = 0.78f, LowCenter = 0.85f, LowMidRight = 0.50f, LowRight = 0.30f,
                MidLeft = 0.75f, MidMidLeft = 0.80f, MidCenter = 0.83f, MidMidRight = 0.70f, MidRight = 0.55f,
                HighLeft = 0.55f, HighMidLeft = 0.60f, HighCenter = 0.65f, HighMidRight = 0.50f, HighRight = 0.40f,
            };
        }
    }
}
