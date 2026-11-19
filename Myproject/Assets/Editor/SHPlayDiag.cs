using System;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Play 模式诊断射手（纯反射，避免程序集引用问题）：
/// 进入 Play、调 ShooterDriver.Play()、逐帧采样手部骨骼世界位置，
/// 判断 ApplyPose 是否真的在驱动骨骼。结果写 D:/AIfootball/sh_play_diag.txt。
/// </summary>
public static class SHPlayDiag
{
    private static int _frames;
    private static float _nextSample;
    private static MonoBehaviour _driver;
    private static MonoBehaviour _retargeter;
    private static Animator _animator;
    private static readonly StringBuilder Sb = new StringBuilder();
    private static bool _played;
    private static Vector3 _firstHand;
    private static float _totalMove;
    private static int _samples;
    private static Vector3 _lastHandPos;
    private static MethodInfo _playMethod;
    private static PropertyInfo _phaseProp;
    private static FieldInfo _bonesField;

    public static void Run()
    {
        EditorSceneManager.OpenScene("Assets/SuperGoalie/Scenes/Demo.unity", OpenSceneMode.Single);
        EditorApplication.update += OnEditorUpdate;
        EditorApplication.isPlaying = true;
    }

    private static void OnEditorUpdate()
    {
        try { Step(); }
        catch (Exception e)
        {
            Sb.AppendLine("!! 异常: " + e);
            System.IO.File.WriteAllText(@"D:\AIfootball\sh_play_diag.txt", Sb.ToString());
            EditorApplication.isPlaying = false;
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.Exit(1);
        }
    }

    private static void Step()
    {
        if (!EditorApplication.isPlaying) return;
        _frames++;

        if (_frames == 5)
        {
            var all = Resources.FindObjectsOfTypeAll<MonoBehaviour>()
                .Where(x => x != null && x.gameObject.scene.IsValid()).ToList();
            _driver = all.FirstOrDefault(x => x.GetType().Name == "ShooterDriver");
            _retargeter = all.FirstOrDefault(x => x.GetType().Name == "ShooterRetargeter");

            Sb.AppendLine("=== Play 诊断 ===");
            if (_driver == null) Sb.AppendLine("!! 未找到 ShooterDriver");
            if (_retargeter != null)
            {
                var t = _retargeter.GetType();
                var ani = t.GetField("_animator", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(_retargeter) as Animator;
                _animator = ani;
                Sb.AppendLine("Retargeter._animator: " + (ani == null ? "NULL" : ani.name + " enabled=" + ani.enabled
                    + " avatar=" + (ani.avatar == null ? "NULL" : ani.avatar.name)
                    + " ctrl=" + (ani.runtimeAnimatorController == null ? "null" : ani.runtimeAnimatorController.name)));
                _bonesField = t.GetField("_bones", BindingFlags.NonPublic | BindingFlags.Instance);
                var bones = _bonesField?.GetValue(_retargeter) as Transform[];
                int missing = 0;
                if (bones != null) foreach (var b in bones) if (b == null) missing++;
                Sb.AppendLine("CaptureRestPose 骨骼缺失: " + missing + "/" + (bones == null ? 0 : bones.Length));
            }

            if (_driver != null && _driver.gameObject.activeInHierarchy)
            {
                _playMethod = _driver.GetType().GetMethod("Play", Type.EmptyTypes);
                _phaseProp = _driver.GetType().GetProperty("Phase");
                Sb.AppendLine("调用 ShooterDriver.Play() ...");
                bool ok = _playMethod != null && (bool)_playMethod.Invoke(_driver, null);
                Sb.AppendLine("Play() 返回: " + ok);
                _played = ok;
            }
            _nextSample = Time.realtimeSinceStartup + 0.1f;
            return;
        }

        if (_frames > 5 && Time.realtimeSinceStartup >= _nextSample && _animator != null)
        {
            _nextSample = Time.realtimeSinceStartup + 0.15f;
            var hand = _animator.GetBoneTransform(HumanBodyBones.LeftHand);
            var foot = _animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (hand == null) { Sb.AppendLine("t=" + Time.realtimeSinceStartup.ToString("0.0") + " LeftHand=NULL"); return; }
            if (_firstHand == Vector3.zero) _firstHand = hand.position;
            _totalMove += Vector3.Distance(_lastHandPos, hand.position);
            _samples++;
            _lastHandPos = hand.position;
            Sb.AppendLine("t=" + Time.realtimeSinceStartup.ToString("0.00")
                + " Phase=" + (_phaseProp != null && _driver != null ? _phaseProp.GetValue(_driver, null).ToString() : "?")
                + " Hand=" + hand.position.ToString("F2")
                + " Foot=" + (foot != null ? foot.position.ToString("F2") : "null"));
        }

        if (_frames > 400 || (_played && _samples >= 15))
        {
            Sb.AppendLine();
            Sb.AppendLine("手部位移总和: " + _totalMove.ToString("F3") + " m （>0.05 即骨骼在被驱动）");
            System.IO.File.WriteAllText(@"D:\AIfootball\sh_play_diag.txt", Sb.ToString());
            EditorApplication.isPlaying = false;
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.Exit(0);
        }
    }
}
