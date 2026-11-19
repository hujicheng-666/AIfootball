using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace PenaltyKickPlatform.Hunyuan.EditorTools
{
    /// <summary>单独一趟：把 DiveTree 中 x=+1 的子项标为 mirror，并落盘。</summary>
    public static class DiveMirrorFix
    {
        [MenuItem("AIfootball/动画/4. 修正DiveTree镜像")]
        public static void Run()
        {
            var ac = AssetDatabase.LoadAssetAtPath<AnimatorController>(
                "Assets/SuperGoalie/Animators/GoalKeeperAnimator001.controller");
            if (ac == null) { Debug.LogError("[MirrorFix] 无 controller"); return; }

            BlendTree target = null;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(
                "Assets/SuperGoalie/Animators/GoalKeeperAnimator001.controller"))
            {
                var bt = o as BlendTree;
                if (bt != null && bt.name == "DiveTree") target = bt;
            }
            if (target == null) { Debug.LogError("[MirrorFix] 找不到 DiveTree"); return; }

            var sb = new StringBuilder("[MirrorFix] DiveTree 子项\n");
            var so = new SerializedObject(target);
            var childs = so.FindProperty("m_Childs");
            for (int i = 0; i < childs.arraySize; i++)
            {
                var el = childs.GetArrayElementAtIndex(i);
                var pos = el.FindPropertyRelative("m_Position");
                float x = pos.FindPropertyRelative("x").floatValue;
                float y = pos.FindPropertyRelative("y").floatValue;
                int mir = x > 0.5f ? 1 : 0;
                el.FindPropertyRelative("m_Mirror").intValue = mir;

                var mo = el.FindPropertyRelative("m_Motion").objectReferenceValue as AnimationClip;
                sb.AppendLine(string.Format("   ({0:F2},{1:F2}) mirror={2}  {3}",
                    x, y, mir, mo != null ? mo.name : "null"));
            }
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
            EditorUtility.SetDirty(ac);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // 复核
            var so2 = new SerializedObject(target);
            var c2 = so2.FindProperty("m_Childs");
            sb.AppendLine("   复核：");
            for (int i = 0; i < c2.arraySize; i++)
                sb.AppendLine("     [" + i + "] mirror=" + c2.GetArrayElementAtIndex(i).FindPropertyRelative("m_Mirror").intValue);

            Debug.Log(sb.ToString());
        }
    }
}
