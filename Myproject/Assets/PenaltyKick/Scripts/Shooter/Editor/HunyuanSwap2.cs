using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PenaltyKickPlatform.Hunyuan.EditorTools
{
    /// <summary>
    /// 第二代（精细）绑骨模型的场景替换。
    /// 与 HunyuanSwap 的区别：会先把已有的 Character / Model 子物体全部禁用，
    /// 再挂 Rig2 prefab，避免场景里出现三层叠加模型。
    /// </summary>
    public static class HunyuanSwap2
    {
        private const string ScenePath = "Assets/SuperGoalie/Scenes/Demo.unity";
        private const string ShooterPrefab = "Assets/SuperGoalie/Meshes/Characters/Hunyuan/ShooterRig2.prefab";
        private const string KeeperPrefab = "Assets/SuperGoalie/Meshes/Characters/Hunyuan/GoalkeeperRig2.prefab";

        [MenuItem("AIfootball/混元/7. 替换为Rig2模型")]
        public static void Swap()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            SwapKeeper();
            SwapShooter();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[Swap2] 场景已保存");
        }

        private static void SwapKeeper()
        {
            var keeper = GameObject.Find("GoalKeeper002");
            if (keeper == null) { Debug.LogError("[Swap2] 找不到 GoalKeeper002"); return; }

            // 禁用所有现有的 Character 子物体（含旧模型与第一代 Rig）
            Transform template = null;
            var oldPos = Vector3.zero; var oldRot = Quaternion.identity;
            Animator oldAnim = null; RuntimeAnimatorController ctrl = null;
            foreach (Transform ch in keeper.transform)
            {
                if (ch.name != "Character") continue;
                if (template == null && ch.gameObject.activeSelf)
                {
                    template = ch; oldPos = ch.localPosition; oldRot = ch.localRotation;
                    oldAnim = ch.GetComponent<Animator>();
                    if (oldAnim != null) ctrl = oldAnim.runtimeAnimatorController;
                }
                ch.gameObject.SetActive(false);
                Debug.Log("[Swap2] 禁用旧门将子物体 " + ch.name);
            }
            if (template == null)
            {
                var any = keeper.transform.Find("Character");
                if (any != null) { oldPos = any.localPosition; oldRot = any.localRotation; }
            }
            if (ctrl == null) ctrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                "Assets/SuperGoalie/Animators/GoalKeeperAnimator001.controller");
            Debug.Log("[Swap2] 门将模板 pos=" + oldPos + " rot=" + oldRot.eulerAngles + " ctrl=" + (ctrl != null ? ctrl.name : "null"));

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(KeeperPrefab);
            if (prefab == null) { Debug.LogError("[Swap2] 找不到 " + KeeperPrefab); return; }
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, keeper.transform);
            inst.name = "Character";
            inst.transform.localPosition = oldPos;
            inst.transform.localRotation = oldRot;

            var anim = inst.GetComponent<Animator>();
            if (anim == null) anim = inst.GetComponentInChildren<Animator>();
            if (anim != null)
            {
                anim.runtimeAnimatorController = ctrl;
                if (oldAnim != null) { anim.applyRootMotion = oldAnim.applyRootMotion; anim.cullingMode = oldAnim.cullingMode; }
                Debug.Log("[Swap2] 门将 Animator avatar=" + (anim.avatar != null ? anim.avatar.name : "null")
                          + " valid=" + (anim.avatar != null && anim.avatar.isValid) + " ctrl=" + (ctrl != null ? ctrl.name : "null"));
            }

            // 动画事件组件迁移（第一代 Rig 上那份）
            if (template != null)
            {
                var ev = template.GetComponent("GoalKeeperAnimationEvents") as Component;
                if (ev != null)
                {
                    UnityEditorInternal.ComponentUtility.CopyComponent(ev);
                    UnityEditorInternal.ComponentUtility.PasteComponentAsNew(inst);
                    Debug.Log("[Swap2] 已迁移 GoalKeeperAnimationEvents");
                }
            }

            var gk = keeper.GetComponent("GoalKeeper") as Component;
            if (gk != null && anim != null)
            {
                var so = new SerializedObject(gk);
                var p = so.FindProperty("_animator");
                if (p != null) { p.objectReferenceValue = anim; so.ApplyModifiedProperties(); }
                Debug.Log("[Swap2] 门将 _animator 已重绑");
            }
        }

        private static void SwapShooter()
        {
            var shooter = GameObject.Find("Shooter");
            if (shooter == null) { Debug.LogError("[Swap2] 找不到 Shooter"); return; }

            Transform template = null;
            var oldPos = Vector3.zero; var oldRot = Quaternion.identity;
            foreach (Transform ch in shooter.transform)
            {
                if (ch.name != "Model") continue;
                if (template == null && ch.gameObject.activeSelf) { template = ch; oldPos = ch.localPosition; oldRot = ch.localRotation; }
                ch.gameObject.SetActive(false);
                Debug.Log("[Swap2] 禁用旧射手子物体 " + ch.name);
            }
            if (template == null)
            {
                var any = shooter.transform.Find("Model");
                if (any != null) { oldPos = any.localPosition; oldRot = any.localRotation; }
            }
            Debug.Log("[Swap2] 射手模板 pos=" + oldPos + " rot=" + oldRot.eulerAngles);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ShooterPrefab);
            if (prefab == null) { Debug.LogError("[Swap2] 找不到 " + ShooterPrefab); return; }
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, shooter.transform);
            inst.name = "Model";
            inst.transform.localPosition = oldPos;
            inst.transform.localRotation = oldRot;

            var anim = inst.GetComponent<Animator>();
            if (anim == null) anim = inst.GetComponentInChildren<Animator>();
            if (anim != null)
                Debug.Log("[Swap2] 射手 Animator avatar=" + (anim.avatar != null ? anim.avatar.name : "null")
                          + " valid=" + (anim.avatar != null && anim.avatar.isValid));

            // 三个组件都可能持有 Animator 引用
            foreach (var typeName in new[] { "ShooterRetargeter", "ShooterDriver", "ShooterPlaybackBinder" })
            {
                var comp = shooter.GetComponent(typeName) as Component;
                if (comp == null) { Debug.LogWarning("[Swap2] 缺组件 " + typeName); continue; }
                var so = new SerializedObject(comp);
                bool any = false;
                foreach (var pn in new[] { "_animator", "animator", "_target" })
                {
                    var p = so.FindProperty(pn);
                    if (p != null && p.propertyType == SerializedPropertyType.ObjectReference)
                    { p.objectReferenceValue = anim; any = true; }
                }
                if (any) { so.ApplyModifiedProperties(); Debug.Log("[Swap2] " + typeName + " Animator 已重绑"); }
            }
        }

        [MenuItem("AIfootball/混元/8. 验证Rig2场景")]
        public static void VerifyScene()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var sb = new StringBuilder("[Swap2] ===== 场景验证 =====\n");

            foreach (var name in new[] { "GoalKeeper002", "Shooter" })
            {
                var go = GameObject.Find(name);
                if (go == null) { sb.AppendLine("找不到 " + name); continue; }
                sb.AppendLine(name + " 世界位置=" + go.transform.position);

                int active = 0, disabled = 0;
                foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr.gameObject.activeInHierarchy)
                    {
                        active++;
                        sb.AppendLine("    [ON ] " + smr.gameObject.name
                            + " 顶点=" + (smr.sharedMesh != null ? smr.sharedMesh.vertexCount.ToString("N0") : "null")
                            + " bounds y=" + smr.bounds.min.y.ToString("F3") + "~" + smr.bounds.max.y.ToString("F3")
                            + " 材质=" + (smr.sharedMaterial != null ? smr.sharedMaterial.name : "null")
                            + " 贴图=" + (smr.sharedMaterial != null && smr.sharedMaterial.mainTexture != null ? smr.sharedMaterial.mainTexture.name : "null"));
                    }
                    else disabled++;
                }
                sb.AppendLine("    启用网格=" + active + " 禁用网格=" + disabled);

                var anim = go.GetComponentInChildren<Animator>(false);
                if (anim != null)
                    sb.AppendLine("    Animator avatar=" + (anim.avatar != null ? anim.avatar.name : "null")
                        + " isValid=" + (anim.avatar != null && anim.avatar.isValid)
                        + " human=" + (anim.avatar != null && anim.avatar.isHuman)
                        + " ctrl=" + (anim.runtimeAnimatorController != null ? anim.runtimeAnimatorController.name : "null"));
            }
            Debug.Log(sb.ToString());

            // 渲染
            var cam = Camera.main;
            if (cam == null) { foreach (var c in Object.FindObjectsOfType<Camera>()) { if (c.CompareTag("MainCamera")) { cam = c; break; } } }
            if (cam == null) { var cs = Object.FindObjectsOfType<Camera>(); if (cs.Length > 0) cam = cs[0]; }
            if (cam == null) { Debug.LogWarning("[Swap2] 无相机"); return; }

            int W = 1280, H = 720;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            var outPath = System.IO.Path.Combine(".tmp_swap2", "scene.png");
            System.IO.Directory.CreateDirectory(".tmp_swap2");
            System.IO.File.WriteAllBytes(outPath, tex.EncodeToPNG());
            cam.targetTexture = null;
            Debug.Log("[Swap2] 渲染写出 " + System.IO.Path.GetFullPath(outPath));
        }
    }
}
