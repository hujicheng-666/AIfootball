using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace PenaltyKickPlatform.Hunyuan.EditorTools
{
    /// <summary>
    /// 把门将 controller 里"扑救"用的混合树从挥拳动作换成真实飞身扑救动作。
    /// 原有结构：TendGoal --(trigger Dive)--> PunchBall(blend tree: 挥拳) --(trigger Exit)--> WakeupNoBall
    /// 改造后：  TendGoal --(trigger Dive)--> Dive(blend tree: High/Mid/Low Dive) --(trigger Exit)--> WakeupNoBall
    /// 参数 (Turn, Height) 与 InterceptShotMainState 的写入完全一致，不动 C# 逻辑。
    /// </summary>
    public static class KeeperDiveTree
    {
        private const string CtrlPath = "Assets/SuperGoalie/Animators/GoalKeeperAnimator001.controller";
        private const string AnimDir = "Assets/SuperGoalie/Animations/GoalKeeperAnimations";

        private static readonly Dictionary<string, string> FbxOf =
            new Dictionary<string, string>
            {
                { "High Dive Left",        "PunchHighLeft"   },
                { "High Dive Left Start",  "PunchHighLeft"   },
                { "High Dive Right Start", "PunchHighLeft"   },
                { "High Dive Left End",    "PunchHighLeft"   },
                { "Mid High Dive Left",    "PunchMiddleLeft" },
                { "Mid High Dive Left Start", "PunchMiddleLeft" },
                { "Low Dive Left",         "PunchLowLeft"    },
                { "Low Dive Left Start",   "PunchLowLeft"    },
                { "PunchHighMiddleStart",  "PunchHighCentre" },
                { "PunchHighMiddleEnd",    "PunchHighCentre" },
                { "Punch Fall Back",       "PunchMiddleCentre" },
                { "Punch Fall Back Start", "PunchMiddleCentre" },
                { "Punch Fall Back End",   "PunchMiddleCentre" },
                { "Save_Down",             "PunchDownCenter" },
                { "Save_Down_Fall",        "PunchDownCenter" },
                { "Take 001",              "PunchDownCenter" },
                { "Intercept Tackle Left", "PunchLowCenter"  },
                { "Intercept Tackle Left Start", "PunchLowCenter" },
            };

        private static readonly Dictionary<string, AnimationClip> _cache =
            new Dictionary<string, AnimationClip>();

        private static AnimationClip Clip(string clipName)
        {
            if (_cache.TryGetValue(clipName, out var c) && c != null) return c;
            if (!FbxOf.TryGetValue(clipName, out var fbx)) { Debug.LogError("[DiveTree] 未登记 " + clipName); return null; }
            string path = AnimDir + "/" + fbx + ".fbx";
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                var a = o as AnimationClip;
                if (a != null && a.name == clipName) { _cache[clipName] = a; return a; }
            }
            Debug.LogError("[DiveTree] 找不到 " + clipName + " in " + path);
            return null;
        }

        [MenuItem("AIfootball/动画/1. 接入真实扑救动作")]
        public static void Build()
        {
            _cache.Clear();
            var ac = AssetDatabase.LoadAssetAtPath<AnimatorController>(CtrlPath);
            if (ac == null) { Debug.LogError("[DiveTree] 找不到 controller"); return; }

            var sm = ac.layers[0].stateMachine;
            AnimatorState dive = null;
            foreach (var s in sm.states)
                if (s.state.name == "PunchBall" || s.state.name == "Dive") dive = s.state;
            if (dive == null) { Debug.LogError("[DiveTree] 找不到 PunchBall state"); return; }

            // 原混合树是 sub-asset，先建新的再替换引用
            var tree = new BlendTree
            {
                name = "DiveTree",
                blendType = BlendTreeType.FreeformDirectional2D,
                blendParameter = "Turn",
                blendParameterY = "Height",
                useAutomaticThresholds = false,
            };
            AssetDatabase.AddObjectToAsset(tree, ac);

            // (x=Turn, y=Height) → clip 名
            var layout = new (float x, float y, string clip)[]
            {
                (-1f, 0.85f, "High Dive Left"),
                ( 0f, 0.85f, "PunchHighMiddleEnd"),
                ( 1f, 0.85f, "High Dive Left"),
                (-1f, 0.50f, "Mid High Dive Left"),
                ( 0f, 0.50f, "Punch Fall Back"),
                ( 1f, 0.50f, "Mid High Dive Left"),
                (-1f, 0.15f, "Low Dive Left"),
                ( 0f, 0.15f, "Take 001"),
                ( 1f, 0.15f, "Low Dive Left"),
            };

            var sb = new StringBuilder("[DiveTree] 构建 2D 混合树\n");
            foreach (var (x, y, name) in layout)
            {
                var clip = Clip(name);
                if (clip == null) continue;
                tree.AddChild(clip, new Vector2(x, y));
                sb.AppendLine(string.Format("   ({0,4:F2},{1,4:F2})  {2}  {3:F2}s", x, y, name, clip.length));
            }

            // x=-1 侧统一镜像（与原 controller 的约定一致：x=1 不镜像，x=-1 镜像）
            var so = new SerializedObject(tree);
            var childs = so.FindProperty("m_Childs");
            for (int i = 0; i < childs.arraySize; i++)
            {
                var el = childs.GetArrayElementAtIndex(i);
                var xProp = el.FindPropertyRelative("m_Position").FindPropertyRelative("x");
                el.FindPropertyRelative("m_Mirror").intValue = xProp.floatValue > 0.5f ? 1 : 0;
            }
            so.ApplyModifiedPropertiesWithoutUndo();

            // 替换 state 的 motion，并改名
            dive.motion = tree;
            dive.name = "Dive";
            dive.iKOnFeet = false;
            dive.speed = 1.15f;          // 稍快一点，扑救更有爆发感
            EditorUtility.SetDirty(dive);

            // 清理同名旧 sub-asset（换过多次时避免堆叠）
            var all = AssetDatabase.LoadAllAssetsAtPath(CtrlPath);
            foreach (var o in all)
            {
                var bt = o as BlendTree;
                if (bt != null && bt != tree && bt.name == "DiveTree")
                {
                    Object.DestroyImmediate(bt, true);
                    sb.AppendLine("   清理旧 DiveTree");
                }
            }

            EditorUtility.SetDirty(ac);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            sb.AppendLine("   已挂到 state: " + dive.name + " 速度=" + dive.speed);
            Debug.Log(sb.ToString());
        }

        /// <summary>还原：把 state 的 motion 换回原来的挥拳混合树。</summary>
        [MenuItem("AIfootball/动画/1b. 回滚挥拳动作")]
        public static void Revert()
        {
            var ac = AssetDatabase.LoadAssetAtPath<AnimatorController>(CtrlPath);
            var sm = ac.layers[0].stateMachine;
            foreach (var s in sm.states)
            {
                var st = s.state;
                if (st.name != "Dive" && st.name != "PunchBall") continue;
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(CtrlPath))
                {
                    var bt = o as BlendTree;
                    if (bt != null && bt.name == "Blend Tree" && bt.blendParameter == "Turn")
                    { st.motion = bt; st.name = "PunchBall"; break; }
                }
            }
            EditorUtility.SetDirty(ac);
            AssetDatabase.SaveAssets();
            Debug.Log("[DiveTree] 已回滚");
        }

        [MenuItem("AIfootball/动画/2. 列出controller所有state与motion")]
        public static void Inspect()
        {
            var ac = AssetDatabase.LoadAssetAtPath<AnimatorController>(CtrlPath);
            var sb = new StringBuilder("[DiveTree] controller 现状\n");
            foreach (var s in ac.layers[0].stateMachine.states)
            {
                var st = s.state;
                sb.Append("  state ").Append(st.name).Append("  speed=").Append(st.speed).Append("  motion=");
                if (st.motion == null) sb.Append("null");
                else if (st.motion is BlendTree bt)
                {
                    sb.Append("BlendTree(").Append(bt.name).Append(") x=").Append(bt.blendParameter)
                      .Append(" y=").Append(bt.blendParameterY).Append(" children=").Append(bt.children.Length);
                    foreach (var cm in bt.children)
                    {
                        var c = cm.motion as AnimationClip;
                        sb.Append("\n      [").Append(cm.position.x.ToString("F2")).Append(',')
                          .Append(cm.position.y.ToString("F2")).Append("] ")
                          .Append(c != null ? c.name + " " + c.length.ToString("F2") + "s" : "null")
                          .Append(cm.mirror ? "  mirror" : "");
                    }
                }
                else sb.Append(st.motion.name);
                sb.Append('\n');
            }
            Debug.Log(sb.ToString());
        }
    }
}
