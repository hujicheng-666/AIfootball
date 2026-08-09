using UnityEngine;
using UnityEditor;
using Assets.SuperGoalie.Scripts.Data;

namespace Assets.SuperGoalie.Scripts.Editor
{
    /// <summary>
    /// 一键生成 4 种不同风格的门将 ScriptableObject
    /// 菜单：Assets → Create → Football → Generate All Goalkeepers
    /// </summary>
    public static class GoalkeeperDataGenerator
    {
        const string OutputPath = "Assets/SuperGoalie/Data/Goalkeepers";

        [MenuItem("Assets/Create/Football/Generate All Goalkeepers")]
        public static void GenerateAll()
        {
            if (!AssetDatabase.IsValidFolder(OutputPath))
            {
                string parent = System.IO.Path.GetDirectoryName(OutputPath);
                string folder = System.IO.Path.GetFileName(OutputPath);
                AssetDatabase.CreateFolder(parent, folder);
            }

            CreateEliteGoalkeeper();
            CreateAllRounder();
            CreateWeakLowRight();
            CreateReactiveSpecialist();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[GoalkeeperData] 4 个门将数据已生成到 " + OutputPath);
        }

        static void CreateEliteGoalkeeper()
        {
            var data = ScriptableObject.CreateInstance<GoalkeeperData>();
            data.DisplayName = "世界级门将";
            data.Description = "全方位无死角，扑救成功率极高。擅长预判和快速反应。";
            data.Height = 1.95f;
            data.DiveSpeed = 5f;
            data.JumpDistance = 1.2f;
            data.JumpHeight = 0.7f;
            data.Reach = 0.6f;
            data.TendGoalSpeed = 3.5f;
            data.TendGoalDistance = 2.8f;
            data.GoalKeeping = 0.93f;
            data.SidePreference = 0f;
            data.HeightPreference = 0f;
            data.ProbabilityMap = new SaveProbabilityMap
            {
                LowLeft = 0.94f, LowMidLeft = 0.95f, LowCenter = 0.97f, LowMidRight = 0.95f, LowRight = 0.94f,
                MidLeft = 0.90f, MidMidLeft = 0.92f, MidCenter = 0.95f, MidMidRight = 0.92f, MidRight = 0.90f,
                HighLeft = 0.78f, HighMidLeft = 0.82f, HighCenter = 0.88f, HighMidRight = 0.82f, HighRight = 0.78f,
            };
            Save(data, "GK_Elite.asset");
        }

        static void CreateAllRounder()
        {
            var data = ScriptableObject.CreateInstance<GoalkeeperData>();
            data.DisplayName = "均衡型门将";
            data.Description = "各项能力均衡，没有明显短板也没有特别突出的优势。";
            data.Height = 1.88f;
            data.DiveSpeed = 4f;
            data.JumpDistance = 1f;
            data.JumpHeight = 0.5f;
            data.Reach = 0.5f;
            data.TendGoalSpeed = 3f;
            data.TendGoalDistance = 3f;
            data.GoalKeeping = 0.85f;
            data.SidePreference = 0f;
            data.HeightPreference = 0f;
            data.ProbabilityMap = SaveProbabilityMap.CreateDefault();
            Save(data, "GK_AllRounder.asset");
        }

        static void CreateWeakLowRight()
        {
            var data = ScriptableObject.CreateInstance<GoalkeeperData>();
            data.DisplayName = "右下盲区型";
            data.Description = "整体能力不错，但右下角是明显弱点——重心偏左，向右倒地慢。";
            data.Height = 1.90f;
            data.DiveSpeed = 4.2f;
            data.JumpDistance = 1.05f;
            data.JumpHeight = 0.55f;
            data.Reach = 0.5f;
            data.TendGoalSpeed = 3.2f;
            data.TendGoalDistance = 3f;
            data.GoalKeeping = 0.82f;
            data.SidePreference = -0.3f; // 轻微偏好左侧
            data.HeightPreference = -0.2f; // 轻微偏好低球
            data.ProbabilityMap = SaveProbabilityMap.CreateWeakBottomRight();
            Save(data, "GK_WeakLowRight.asset");
        }

        static void CreateReactiveSpecialist()
        {
            var data = ScriptableObject.CreateInstance<GoalkeeperData>();
            data.DisplayName = "反应型门将";
            data.Description = "反应极快、擅长近距离扑救，但臂展不足、高空球较弱。";
            data.Height = 1.82f;
            data.DiveSpeed = 5.5f;
            data.JumpDistance = 1.1f;
            data.JumpHeight = 0.4f;
            data.Reach = 0.45f;
            data.TendGoalSpeed = 3.8f;
            data.TendGoalDistance = 2.5f;
            data.GoalKeeping = 0.88f;
            data.SidePreference = 0f;
            data.HeightPreference = -0.5f; // 强烈偏好低球
            data.ProbabilityMap = new SaveProbabilityMap
            {
                // 低球和中路非常强
                LowLeft = 0.90f, LowMidLeft = 0.93f, LowCenter = 0.95f, LowMidRight = 0.93f, LowRight = 0.90f,
                MidLeft = 0.85f, MidMidLeft = 0.88f, MidCenter = 0.92f, MidMidRight = 0.88f, MidRight = 0.85f,
                // 高球明显弱
                HighLeft = 0.50f, HighMidLeft = 0.55f, HighCenter = 0.60f, HighMidRight = 0.55f, HighRight = 0.50f,
            };
            Save(data, "GK_Reactive.asset");
        }

        static void Save(GoalkeeperData data, string filename)
        {
            string path = System.IO.Path.Combine(OutputPath, filename);
            AssetDatabase.CreateAsset(data, path);
        }
    }
}
