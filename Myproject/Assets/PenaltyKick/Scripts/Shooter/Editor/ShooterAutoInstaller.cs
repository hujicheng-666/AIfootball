#if UNITY_EDITOR
using Assets.SuperGoalie.Scripts.Managers;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PenaltyKickPlatform.Shooter.EditorTools
{
    /// <summary>
    /// 编译完成后自动检查：若当前打开的是比赛场景（存在 GameManager）而场景中还没有射手，
    /// 就自动创建**并保存场景**。
    ///
    /// 为什么必须自动保存
    /// ----------------
    /// 射手是场景对象。只在编辑器内存里创建而不落盘，构建出的 exe 里就不会有它——
    /// 这正是「射手没出现」的原因。所以这里创建后立即 SaveScene。
    ///
    /// 每个编辑器会话只检查一次；SessionKey 带版本号，改动本文件即可重新触发一次。
    /// </summary>
    [InitializeOnLoad]
    public static class ShooterAutoInstaller
    {
        private const string SessionKey = "AIfootball.ShooterAutoInstallChecked.v2";
        private const string ShooterName = "Shooter";

        /// <summary>设为 false 可关闭自动安装（例如你想手动控制）。</summary>
        public static bool Enabled = true;

        /// <summary>自动创建后是否立即保存场景。关闭则只创建、需手动 Ctrl+S。</summary>
        public static bool AutoSaveScene = true;

        static ShooterAutoInstaller()
        {
            EditorApplication.delayCall += TryInstall;
        }

        private static void TryInstall()
        {
            if (!Enabled) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (SessionState.GetBool(SessionKey, false)) return;

            // 只在真正的比赛场景里动手：该场景必然有 GameManager
            GameManager manager = FindManager();
            if (manager == null) return;

            SessionState.SetBool(SessionKey, true);

            if (GameObject.Find(ShooterName) != null) return;

            Debug.Log("[ShooterAutoInstaller] 比赛场景中没有射手，自动创建。");
            ShooterSetup.CreateShooter(silentIfExists: true);

            if (GameObject.Find(ShooterName) == null)
            {
                Debug.LogError("[ShooterAutoInstaller] 射手创建失败，请查看上面的错误日志" +
                               "（通常是模型路径或 Avatar 配置问题）。");
                return;
            }

            if (!AutoSaveScene) return;

            Scene scene = SceneManager.GetActiveScene();
            try
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                Debug.Log("[ShooterAutoInstaller] 场景已保存：" + scene.path);
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning("[ShooterAutoInstaller] 自动保存场景失败，请手动按 Ctrl+S，" +
                                 "否则构建出的播放器里不会有射手：" + exception.Message);
            }
        }

        private static GameManager FindManager()
        {
#if UNITY_2023_1_OR_NEWER
            return Object.FindFirstObjectByType<GameManager>(FindObjectsInactive.Include);
#else
            return Object.FindObjectOfType<GameManager>(true);
#endif
        }

        [MenuItem("AIfootball/射手/重新自动安装（下次编译生效）", priority = 22)]
        private static void ReArmAutoInstall()
        {
            SessionState.SetBool(SessionKey, false);
            Debug.Log("[ShooterAutoInstaller] 已重新武装：下次编译或重新打开工程时会再检查一次。");
        }
    }
}
#endif
