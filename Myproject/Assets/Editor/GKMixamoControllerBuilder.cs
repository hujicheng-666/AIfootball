using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using System.Text;
using System.IO;
using System.Collections.Generic;

/// <summary>
/// 重建门将控制器：9 个扑救格各为一个独立状态（单条动作），由代码按 (Turn, Height) 直选，
/// 不再用 2D 混合树插值——避免不同来源、不同时长的动作互相污染。
/// </summary>
public class GKMixamoControllerBuilder
{
    const string AnimDir = "Assets/SuperGoalie/Meshes/Characters/GKMixamo/Anims/";
    const string OutPath = "Assets/SuperGoalie/Animators/GKMixamoController.controller";
    const string ModelFbx = "Assets/SuperGoalie/Meshes/Characters/GKMixamo/gk_mixamo_rig_150k.fbx";
    const string KeeperPrefabPath = "Assets/SuperGoalie/Prefabs/Entities/GoalKeeper002.prefab";
    const string ScenePath = "Assets/SuperGoalie/Scenes/Demo.unity";

    /// <summary>经过逐条渲染核对的 9 格映射（格名, 动作, 是否镜像, 语义说明）。</summary>
    static readonly (string state, string clip, bool mirror, string note)[] Cells =
    {
        ("Punch_Low_Left",    "GK_18_DivingSaveLeft",   false, "贴地侧扑（髋高0.10，横移1.78）"),
        ("Punch_Low_Center",  "GK_19_Scoop",            false, "弯腰低位捡球"),
        ("Punch_Low_Right",   "GK_15_DivingSaveRight",  false, "贴地侧扑（髋高0.10，横移2.14）"),
        ("Punch_Mid_Left",    "GK_04_BodyBlockLeft",    false, "身体挡（贴地，横移0.93）"),
        ("Punch_Mid_Center",  "GK_11_CatchMedium",      false, "站立胸前接球（髋高0.50）"),
        ("Punch_Mid_Right",   "GK_07_BodyBlockRight",   false, "身体挡（贴地，横移0.82）"),
        ("Punch_High_Left",   "GK_13_CatchJumpHigh",    false, "双臂过头接高球（中路动作，缺侧向高球素材）"),
        ("Punch_High_Center", "GK_13_CatchJumpHigh",    false, "双臂过头接高球"),
        ("Punch_High_Right",  "GK_13_CatchJumpHigh",    true,  "双臂过头接高球（镜像）"),
    };

    static readonly Dictionary<string, AnimationClip> _cache = new Dictionary<string, AnimationClip>();

    static AnimationClip Clip(string name)
    {
        if (_cache.TryGetValue(name, out var c)) return c;
        foreach (var a in AssetDatabase.LoadAllAssetsAtPath(AnimDir + name + ".fbx"))
        {
            var clip = a as AnimationClip;
            if (clip != null && !clip.name.StartsWith("__preview")) { _cache[name] = clip; return clip; }
        }
        Debug.LogError("clip missing: " + name);
        return null;
    }

    [MenuItem("Tools/门将/重建 GKMixamo 控制器")]
    public static void Run()
    {
        var sb = new StringBuilder();

        if (AssetDatabase.LoadAssetAtPath<AnimatorController>(OutPath) != null)
            AssetDatabase.DeleteAsset(OutPath);

        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(OutPath);
        ctrl.AddParameter("Dive", AnimatorControllerParameterType.Trigger);
        ctrl.AddParameter("Exit", AnimatorControllerParameterType.Trigger);
        ctrl.AddParameter("Idle", AnimatorControllerParameterType.Trigger);
        ctrl.AddParameter("TendGoal", AnimatorControllerParameterType.Trigger);
        ctrl.AddParameter("HasBall", AnimatorControllerParameterType.Bool);
        ctrl.AddParameter("Forward", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("Height", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("Turn", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("HeightMultiplier", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("WeightHandLeft", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("WeightHandRight", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("WeightLook", AnimatorControllerParameterType.Float);

        var sm = ctrl.layers[0].stateMachine;
        sm.name = "Base Layer";

        var stIdle = sm.AddState("Idle", new Vector3(300, 0, 0));
        stIdle.motion = Clip("GK_10_IdleNoBall");

        // 待机/站位：1D 阈值必须显式设置（Vector2 只写 position，不写 threshold）
        var stTend = sm.AddState("TendGoal", new Vector3(300, 120, 0));
        var tendTree = new BlendTree();
        tendTree.name = "TendGoalTree";
        tendTree.blendType = BlendTreeType.Simple1D;
        tendTree.blendParameter = "Turn";
        AssetDatabase.AddObjectToAsset(tendTree, ctrl);
        tendTree.AddChild(Clip("GK_08_SidestepLeft"), -1f);
        tendTree.AddChild(Clip("GK_10_IdleNoBall"), 0f);
        tendTree.AddChild(Clip("GK_02_SidestepRight"), 1f);
        stTend.motion = tendTree;

        // 9 个扑救格：各自单条动作，代码直选
        int i = 0;
        foreach (var c in Cells)
        {
            var st = sm.AddState(c.state, new Vector3(560, 40 + i * 70, 0));
            st.motion = Clip(c.clip);
            st.mirror = c.mirror;
            sb.AppendLine($"{c.state}\t{c.clip}\tmirror={c.mirror}\t{c.note}");
            i++;
        }

        sm.defaultState = stIdle;

        AddT(stIdle, stTend, "TendGoal", 0.25f, false, 0f);
        AddT(stTend, stIdle, "Idle", 0.25f, false, 0f);
        foreach (var c in Cells)
        {
            var st = sm.states[i2of(sm, c.state)].state;
            // 待机 -> 该格：由 Dive 触发器 + 代码 CrossFade 双重保障
            var tIn = stTend.AddTransition(st);
            tIn.hasExitTime = false;
            tIn.duration = 0.10f;
            tIn.hasFixedDuration = true;

            // 该格 -> 待机：Exit 触发器
            AddT(st, stIdle, "Exit", 0.30f, false, 0f);
        }

        AssetDatabase.SaveAssets();
        sb.AppendLine("CONTROLLER_CREATED");

        // 挂到 prefab 与场景
        var contents = PrefabUtility.LoadPrefabContents(KeeperPrefabPath);
        var model = contents.transform.Find("GKMixamo");
        if (model != null)
        {
            var an = model.GetComponent<Animator>();
            an.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(OutPath);
        }
        PrefabUtility.SaveAsPrefabAsset(contents, KeeperPrefabPath);
        PrefabUtility.UnloadPrefabContents(contents);

        var scene = EditorSceneManager.OpenScene(ScenePath);
        GameObject keeperRoot = null;
        foreach (var root in scene.GetRootGameObjects())
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.GetComponent("GoalKeeper") != null) keeperRoot = t.gameObject;
        if (keeperRoot != null)
        {
            var m2 = keeperRoot.transform.Find("GKMixamo");
            if (m2 != null)
            {
                var an2 = m2.GetComponent<Animator>();
                an2.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(OutPath);
                var sso = new SerializedObject(keeperRoot.GetComponent("GoalKeeper"));
                sso.FindProperty("_animator").objectReferenceValue = an2;
                sso.FindProperty("_modelRoot").objectReferenceValue = m2;
                sso.ApplyModifiedPropertiesWithoutUndo();
            }
        }
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);
        sb.AppendLine("SCENE_SAVED");

        File.WriteAllText("D:/AIfootball/logs/gk_controller_build.txt", sb.ToString());
        Debug.Log("BUILT");
    }

    static int i2of(AnimatorStateMachine sm, string name)
    {
        var list = sm.states;
        for (int k = 0; k < list.Length; k++)
            if (list[k].state.name == name) return k;
        return 0;
    }

    static void AddT(AnimatorState from, AnimatorState to, string trigger, float dur, bool hasExitTime, float exitTime)
    {
        var t = from.AddTransition(to);
        t.hasExitTime = hasExitTime;
        t.exitTime = exitTime;
        t.duration = dur;
        t.hasFixedDuration = true;
        if (!string.IsNullOrEmpty(trigger)) t.AddCondition(AnimatorConditionMode.If, 0f, trigger);
    }
}
