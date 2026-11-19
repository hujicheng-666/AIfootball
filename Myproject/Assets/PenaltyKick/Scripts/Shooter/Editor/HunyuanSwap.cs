using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PenaltyKickPlatform.Hunyuan.EditorTools
{
    /// <summary>
    /// 把混元角色替换进 Demo 场景。
    /// 只替换"模型子物体"，保留原有 GameObject、脚本组件与场景引用，
    /// 避免破坏 GoalKeeper / ShooterRetargeter 的既有绑定。
    /// </summary>
    public static class HunyuanSwap
    {
        private const string ScenePath = "Assets/SuperGoalie/Scenes/Demo.unity";
        private const string ShooterPrefab = "Assets/SuperGoalie/Meshes/Characters/Hunyuan/ShooterRig.prefab";
        private const string KeeperPrefab = "Assets/SuperGoalie/Meshes/Characters/Hunyuan/GoalkeeperRig.prefab";

        [MenuItem("AIfootball/混元/4. 检查场景结构")]
        public static void Inspect()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath);
            var sb = new StringBuilder("[HunyuanSwap] 场景层级：\n");

            foreach (var root in scene.GetRootGameObjects())
            {
                Dump(root.transform, 0, sb);
            }
            Debug.Log(sb.ToString());
        }

        private static void Dump(Transform t, int depth, StringBuilder sb)
        {
            if (depth > 4) return;
            sb.Append(' ', depth * 2).Append("- ").Append(t.name);
            var comps = t.GetComponents<Component>();
            sb.Append("  [");
            foreach (var c in comps)
            {
                if (c == null) continue;
                sb.Append(c.GetType().Name).Append(' ');
            }
            sb.Append("]");
            var animator = t.GetComponent<Animator>();
            if (animator != null)
                sb.Append(" avatar=").Append(animator.avatar != null ? animator.avatar.name : "null")
                  .Append(" ctrl=").Append(animator.runtimeAnimatorController != null ? animator.runtimeAnimatorController.name : "null");
            var smr = t.GetComponent<SkinnedMeshRenderer>();
            if (smr != null) sb.Append(" SMR=").Append(smr.sharedMesh != null ? smr.sharedMesh.name : "null");
            var mf = t.GetComponent<MeshFilter>();
            if (mf != null) sb.Append(" Mesh=").Append(mf.sharedMesh != null ? mf.sharedMesh.name : "null");
            sb.AppendLine();

            foreach (Transform child in t)
                Dump(child, depth + 1, sb);
        }
        [MenuItem("AIfootball/混元/5. 替换角色模型")]
        public static void Swap()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath);
            SwapKeeper();
            SwapShooter();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[HunyuanSwap] 场景已保存");
        }

        private static void SwapKeeper()
        {
            var keeper = GameObject.Find("GoalKeeper002");
            if (keeper == null) { Debug.LogError("[HunyuanSwap] 找不到 GoalKeeper002"); return; }

            var oldChar = keeper.transform.Find("Character");
            if (oldChar == null) { Debug.LogError("[HunyuanSwap] 找不到 Character 子物体"); return; }

            var oldPos = oldChar.localPosition;
            var oldRot = oldChar.localRotation;
            var oldScale = oldChar.localScale;
            var oldAnim = oldChar.GetComponent<Animator>();
            var events = oldChar.GetComponent("GoalKeeperAnimationEvents") as Component;
            var oldCtrl = oldAnim != null ? oldAnim.runtimeAnimatorController : null;
            Debug.Log(string.Format("[HunyuanSwap] 旧 Character pos={0} rot={1} scale={2} ctrl={3} events={4}",
                oldPos, oldRot.eulerAngles, oldScale, oldCtrl != null ? oldCtrl.name : "null", events != null));

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(KeeperPrefab);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, keeper.transform);
            inst.name = "Character";
            inst.transform.localPosition = oldPos;
            inst.transform.localRotation = oldRot;

            var newAnim = inst.GetComponent<Animator>();
            if (newAnim != null && oldAnim != null)
            {
                newAnim.runtimeAnimatorController = oldCtrl;
                newAnim.applyRootMotion = oldAnim.applyRootMotion;
                newAnim.cullingMode = oldAnim.cullingMode;
            }

            if (events != null)
            {
                UnityEditorInternal.ComponentUtility.CopyComponent(events);
                UnityEditorInternal.ComponentUtility.PasteComponentAsNew(inst);
                Debug.Log("[HunyuanSwap] 已迁移 GoalKeeperAnimationEvents");
            }

            // 重绑 GoalKeeper._animator
            var gk = keeper.GetComponent("GoalKeeper") as Component;
            if (gk != null && newAnim != null)
            {
                var so = new SerializedObject(gk);
                var prop = so.FindProperty("_animator");
                if (prop != null) { prop.objectReferenceValue = newAnim; so.ApplyModifiedProperties(); }
                Debug.Log("[HunyuanSwap] 门将 _animator 已重绑");
            }

            oldChar.gameObject.SetActive(false);   // 禁用而非删除：prefab 实例内部不允许 Destroy
        }

        private static void SwapShooter()
        {
            var shooter = GameObject.Find("Shooter");
            if (shooter == null) { Debug.LogError("[HunyuanSwap] 找不到 Shooter"); return; }

            var oldModel = shooter.transform.Find("Model");
            if (oldModel == null) { Debug.LogError("[HunyuanSwap] 找不到 Model 子物体"); return; }

            var oldPos = oldModel.localPosition;
            var oldRot = oldModel.localRotation;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ShooterPrefab);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, shooter.transform);
            inst.name = "Model";
            inst.transform.localPosition = oldPos;
            inst.transform.localRotation = oldRot;

            var newAnim = inst.GetComponent<Animator>();

            // 重绑 ShooterRetargeter._animator
            var retargeter = shooter.GetComponent("ShooterRetargeter") as Component;
            if (retargeter != null && newAnim != null)
            {
                var so = new SerializedObject(retargeter);
                var prop = so.FindProperty("_animator");
                if (prop != null) { prop.objectReferenceValue = newAnim; so.ApplyModifiedProperties(); }
                Debug.Log("[HunyuanSwap] 射手 _animator 已重绑");
            }
            else
            {
                Debug.LogWarning("[HunyuanSwap] ShooterRetargeter 或 Animator 缺失");
            }

            oldModel.gameObject.SetActive(false);
        }
        [MenuItem("AIfootball/混元/6. 验证场景")]
        public static void VerifyScene()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath);

            foreach (var name in new[] { "GoalKeeper002", "Shooter" })
            {
                var go = GameObject.Find(name);
                if (go == null) { Debug.LogWarning("[HunyuanSwap] 找不到 " + name); continue; }

                var sb = new StringBuilder("[HunyuanSwap] " + name + " 世界位置=" + go.transform.position);
                foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (!smr.gameObject.activeInHierarchy) continue;
                    sb.Append("\n    ").Append(smr.gameObject.name)
                      .Append(" 顶点=").Append(smr.sharedMesh != null ? smr.sharedMesh.vertexCount.ToString("N0") : "null")
                      .Append(" 世界bounds min.y=").Append(smr.bounds.min.y.ToString("F3"))
                      .Append(" max.y=").Append(smr.bounds.max.y.ToString("F3"))
                      .Append(" 材质=").Append(smr.sharedMaterial != null ? smr.sharedMaterial.name : "null");
                }
                var anim = go.GetComponentInChildren<Animator>(true);
                if (anim != null)
                    sb.Append("\n    Animator avatar=").Append(anim.avatar != null ? anim.avatar.name : "null")
                      .Append(" isValid=").Append(anim.avatar != null && anim.avatar.isValid)
                      .Append(" ctrl=").Append(anim.runtimeAnimatorController != null ? anim.runtimeAnimatorController.name : "null")
                      .Append(" 启用=").Append(anim.enabled);
                Debug.Log(sb.ToString());
            }

            // 渲染主相机
            var cam = Camera.main;
            if (cam == null) { Debug.LogWarning("[HunyuanSwap] 无 MainCamera"); return; }
            int W = 1280, H = 720;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            var outPath = System.IO.Path.Combine(".tmp_hunyuan_verify", "scene.png");
            System.IO.Directory.CreateDirectory(".tmp_hunyuan_verify");
            System.IO.File.WriteAllBytes(outPath, tex.EncodeToPNG());
            cam.targetTexture = null;
            Debug.Log("[HunyuanSwap] 场景渲染已写出 " + System.IO.Path.GetFullPath(outPath));
        }
    }
}
