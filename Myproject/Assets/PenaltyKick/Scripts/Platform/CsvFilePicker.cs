using System;
using System.IO;
using Assets.SuperGoalie.Scripts.Trajectories;
using UnityEngine;

namespace PenaltyKickPlatform.Platform
{
    public sealed class CsvFilePicker
    {
        public void Pick(Action<string, string> selected, Action<string> failed)
        {
            try
            {
                string path = null;
#if UNITY_EDITOR
                path = UnityEditor.EditorUtility.OpenFilePanel("Select football trajectory CSV", string.Empty, "csv");
#elif UNITY_STANDALONE_WIN
                path = WindowsCsvFileDialog.Open();
#else
                failed("Local CSV picker is not supported on this platform. Use paste instead.");
                return;
#endif
                if (!string.IsNullOrEmpty(path))
                    ReadPath(path, selected, failed);
                else
                    failed("CSV selection cancelled.");
            }
            catch (Exception exception)
            {
                failed("Failed to open CSV picker: " + exception.Message);
            }
        }

        private static void ReadPath(string path, Action<string, string> selected, Action<string> failed)
        {
            try
            {
                selected(Path.GetFileName(path), File.ReadAllText(path));
            }
            catch (Exception exception)
            {
                failed("Failed to read CSV: " + exception.Message);
            }
        }
    }
}