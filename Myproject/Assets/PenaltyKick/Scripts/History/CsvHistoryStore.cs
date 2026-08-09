using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PenaltyKickPlatform.History
{
    [Serializable]
    public sealed class CsvHistoryEntry
    {
        public string Id;
        public string DisplayName;
        public string ImportedAt;
        public string FileName;
        public string LastResult;
        public string LastPlayedAt;
        public bool LastTouchedByKeeper;
        public float LastEventTime;
    }

    [Serializable]
    internal sealed class CsvHistoryIndex
    {
        public List<CsvHistoryEntry> Entries = new List<CsvHistoryEntry>();
        public int TotalUploads;
    }

    public sealed class CsvHistoryStore
    {
        public const string ResultGoal = "\u8FDB\u7403";
        public const string ResultMiss = "\u5C04\u504F";
        public const string ResultSaved = "\u88AB\u6251\u51FA";

        private readonly string _directory;
        private readonly string _indexPath;
        private readonly string _resultsPath;
        private CsvHistoryIndex _index;

        public CsvHistoryStore(string dataRoot, string legacyPersistentDataPath)
        {
            _directory = Path.Combine(dataRoot, "PenaltyKickHistory");
            _indexPath = Path.Combine(_directory, "index.json");
            _resultsPath = Path.Combine(_directory, "results.csv");
            MigrateLegacyHistory(legacyPersistentDataPath);
            Directory.CreateDirectory(_directory);
            LoadIndex();
        }

        public IList<CsvHistoryEntry> Entries { get { return _index.Entries.AsReadOnly(); } }
        public string ResultsPath { get { return _resultsPath; } }
        public string DirectoryPath { get { return _directory; } }
        public int UploadedCount { get { return _index.TotalUploads; } }
        public int CompletedCount { get { return GoalCount + MissCount + SavedCount; } }
        public int GoalCount { get { return CountResult(ResultGoal); } }
        public int MissCount { get { return CountResult(ResultMiss); } }
        public int SavedCount { get { return CountResult(ResultSaved); } }
        public float OnTargetRate
        {
            get { return CompletedCount == 0 ? 0f : (GoalCount + SavedCount) * 100f / CompletedCount; }
        }

        public CsvHistoryEntry Add(string displayName, string csvText)
        {
            Directory.CreateDirectory(_directory);
            string id = Guid.NewGuid().ToString("N");
            CsvHistoryEntry entry = new CsvHistoryEntry
            {
                Id = id,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? "trajectory.csv" : Path.GetFileName(displayName),
                ImportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                FileName = id + ".csv"
            };

            File.WriteAllText(Path.Combine(_directory, entry.FileName), csvText);
            _index.Entries.Insert(0, entry);
            _index.TotalUploads++;
            SaveIndex();
            return entry;
        }

        public string Read(string id)
        {
            CsvHistoryEntry entry = Find(id);
            if (entry == null)
                throw new FileNotFoundException("CSV history entry was not found.", id);

            string path = Path.Combine(_directory, entry.FileName);
            if (!File.Exists(path))
                throw new FileNotFoundException("CSV history file was not found.", path);
            return File.ReadAllText(path);
        }

        public bool Delete(string id)
        {
            CsvHistoryEntry entry = Find(id);
            if (entry == null)
                return false;
            DeleteFile(entry);
            _index.Entries.Remove(entry);
            SaveIndex();
            return true;
        }

        public void RecordResult(string id, string result, bool touchedByKeeper, float eventTime)
        {
            CsvHistoryEntry entry = Find(id);
            if (entry == null)
                return;

            entry.LastResult = result;
            entry.LastPlayedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            entry.LastTouchedByKeeper = touchedByKeeper;
            entry.LastEventTime = eventTime;
            SaveIndex();

            if (!File.Exists(_resultsPath))
                File.WriteAllText(_resultsPath,
                    "played_at,csv_id,csv_name,result,keeper_touched,event_time_seconds\n");
            File.AppendAllText(_resultsPath,
                Escape(entry.LastPlayedAt) + "," + Escape(entry.Id) + "," + Escape(entry.DisplayName) + ","
                + Escape(result) + "," + (touchedByKeeper ? "true" : "false") + ","
                + eventTime.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "\n");
        }

        private CsvHistoryEntry Find(string id)
        {
            return _index.Entries.Find(item => item.Id == id);
        }

        private void LoadIndex()
        {
            _index = new CsvHistoryIndex();
            if (!File.Exists(_indexPath))
                return;
            try
            {
                CsvHistoryIndex loaded = JsonUtility.FromJson<CsvHistoryIndex>(File.ReadAllText(_indexPath));
                if (loaded != null && loaded.Entries != null)
                    _index = loaded;
                _index.Entries.RemoveAll(entry => entry == null || string.IsNullOrEmpty(entry.Id)
                    || string.IsNullOrEmpty(entry.FileName)
                    || !File.Exists(Path.Combine(_directory, entry.FileName)));
                if (_index.TotalUploads < _index.Entries.Count)
                    _index.TotalUploads = _index.Entries.Count;
                SaveIndex();
            }
            catch (Exception exception)
            {
                Debug.LogWarning("CSV history index could not be read: " + exception.Message);
                _index = new CsvHistoryIndex();
            }
        }

        private void SaveIndex()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(_indexPath, JsonUtility.ToJson(_index, true));
        }

        private void DeleteFile(CsvHistoryEntry entry)
        {
            string path = Path.Combine(_directory, entry.FileName);
            if (File.Exists(path))
                File.Delete(path);
        }

        private static string Escape(string value)
        {
            string safe = value ?? string.Empty;
            return "\"" + safe.Replace("\"", "\"\"") + "\"";
        }

        private int CountResult(string result)
        {
            int count = 0;
            for (int index = 0; index < _index.Entries.Count; index++)
            {
                if (string.Equals(_index.Entries[index].LastResult, result, StringComparison.Ordinal))
                    count++;
            }
            return count;
        }

        private void MigrateLegacyHistory(string legacyPersistentDataPath)
        {
            if (File.Exists(_indexPath) || string.IsNullOrEmpty(legacyPersistentDataPath))
                return;
            string legacyDirectory = Path.Combine(legacyPersistentDataPath, "PenaltyKickHistory");
            if (!Directory.Exists(legacyDirectory)
                || string.Equals(Path.GetFullPath(legacyDirectory), Path.GetFullPath(_directory),
                    StringComparison.OrdinalIgnoreCase))
                return;

            Directory.CreateDirectory(_directory);
            string[] files = Directory.GetFiles(legacyDirectory);
            for (int index = 0; index < files.Length; index++)
            {
                string destination = Path.Combine(_directory, Path.GetFileName(files[index]));
                if (!File.Exists(destination))
                    File.Copy(files[index], destination);
            }
            Debug.Log("Penalty kick history migrated to project folder: " + _directory);
        }
    }
}