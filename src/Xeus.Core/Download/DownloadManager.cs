using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using Amoeba.Messages;
using Omnix.Base;
using Omnix.Collections;
using Omnix.Configuration;
using Omnix.Cryptography;
using Omnix.Io;
using Xeus.Core.Contents.Cache;
using Xeus.Core.Contents.Download.Internal;
using Xeus.Core.Internal;
using Xeus.Messages.Options;
using Xeus.Messages.Reports;

namespace Xeus.Core.Contents.Download
{
    sealed partial class DownloadManager : ServiceBase, ISettings
    {
        private ExchangeManager _networkManager;
        private CacheManager _cacheManager;
        private BufferPool _bufferPool;

        private Settings _settings;

        private string _basePath;

        private TaskManager _downloadTaskManager;
        private List<TaskManager> _decodeTaskManagers = new List<TaskManager>();

        private ExistManager _existManager = new ExistManager();

        private VolatileDownloadItemInfoManager _volatileDownloadItemInfoManager;
        private DownloadItemInfoManager _downloadItemInfoManager;
        
        private EventScheduler _watchTimer;

        private volatile ServiceStateType _state = ServiceStateType.Stopped;

        private readonly object _lockObject = new object();
        private volatile bool _disposed;

        private readonly int _threadCount = 2;

        public DownloadManager(string configPath, ExchangeManager networkManager, CacheManager cacheManager, BufferPool bufferPool)
        {
            _networkManager = networkManager;
            _cacheManager = cacheManager;
            _bufferPool = bufferPool;

            _settings = new Settings(configPath);

            _downloadTaskManager = new TaskManager(this.DownloadingThread);

            for (int i = 0; i < _threadCount; i++)
            {
                _decodeTaskManagers.Add(new TaskManager(this.DecodingThread));
            }

            _volatileDownloadItemInfoManager = new VolatileDownloadItemInfoManager();
            _volatileDownloadItemInfoManager.AddEvent += (info) => this.Event_AddInfo(info);
            _volatileDownloadItemInfoManager.RemoveEvent += (info) => this.Event_RemoveInfo(info);

            _downloadItemInfoManager = new DownloadItemInfoManager();
            _downloadItemInfoManager.AddEvents += (info) => this.Event_AddInfo(info);
            _downloadItemInfoManager.RemoveEvents += (info) => this.Event_RemoveInfo(info);

            _watchTimer = new EventScheduler(this.WatchThread);
            _watchTimer.Start(new TimeSpan(0, 1, 0));

            _cacheManager.AddedBlockEvents += (hashes) => this.Update_DownloadBlockStates(true, hashes);
            _cacheManager.RemovedBlockEvents += (hashes) => this.Update_DownloadBlockStates(false, hashes);
        }

        public IEnumerable<DownloadContentReport> GetDownloadContentReports()
        {
            lock (_lockObject)
            {
                var list = new List<DownloadContentReport>();

                foreach (var info in _downloadItemInfoManager)
                {
                    int blockCount;
                    int downloadBlockCount;
                    int parityBlockCount;
                    {
                        if (info.Depth == 0) blockCount = 1;
                        else blockCount = info.Index.Groups.Sum(n => n.Hashes.Count);

                        if (info.State == DownloadState.Downloading || info.State == DownloadState.Decoding || info.State == DownloadState.ParityDecoding)
                        {
                            if (info.Depth == 0) downloadBlockCount = _cacheManager.Contains(info.Clue.Hash) ? 1 : 0;
                            else downloadBlockCount = info.Index.Groups.Sum(n => Math.Min(n.Hashes.Count() / 2, _existManager.GetCount(n, true)));
                        }
                        else if (info.State == DownloadState.Completed)
                        {
                            if (info.Depth == 0) downloadBlockCount = _cacheManager.Contains(info.Clue.Hash) ? 1 : 0;
                            else downloadBlockCount = info.Index.Groups.Sum(n => _existManager.GetCount(n, true));
                        }
                        else
                        {
                            downloadBlockCount = 0;
                        }

                        if (info.Depth == 0) parityBlockCount = 0;
                        else parityBlockCount = info.Index.Groups.Sum(n => n.Hashes.Count() / 2);
                    }

                    list.Add(new DownloadContentReport(info.Clue, info.Path, info.State, info.Depth, blockCount, downloadBlockCount, parityBlockCount));
                }

                return list;
            }
        }

        public DownloadOptions Config
        {
            get
            {
                lock (_lockObject)
                {
                    return new DownloadOptions(_basePath);
                }
            }
        }

        public void SetConfig(DownloadOptions config)
        {
            lock (_lockObject)
            {
                _basePath = config.BasePath;
            }
        }

        private void Event_AddInfo(DownloadItemInfo info)
        {
            lock (_lockObject)
            {
                _cacheManager.Lock(info.Clue.Hash);
                this.CheckState(info.DownloadingMerkleTreeNode);
            }
        }

        private void Event_RemoveInfo(DownloadItemInfo info)
        {
            lock (_lockObject)
            {
                _cacheManager.Unlock(info.Clue.Hash);
                this.UncheckState(info.DownloadingMerkleTreeNode);

                info.State = DownloadState.Error;
            }
        }

        private void CheckState(MerkleTreeNode merkleTreeNode)
        {
            lock (_lockObject)
            {
                if (merkleTreeNode == null) return;

                foreach (var group in merkleTreeNode.Hashes)
                {
                    var hashes = new List<OmniHash>();

                    foreach (var hash in group.Hashes)
                    {
                        _cacheManager.Lock(hash);

                        if (_cacheManager.Contains(hash)) hashes.Add(hash);
                    }

                    _existManager.Add(group);
                    _existManager.Set(hashes, true);
                }
            }
        }

        private void UncheckState(MerkleTreeNode merkleTreeNode)
        {
            lock (_lockObject)
            {
                if (merkleTreeNode == null) return;

                foreach (var group in merkleTreeNode.Hashes)
                {
                    foreach (var hash in group.Hashes)
                    {
                        _cacheManager.Unlock(hash);
                    }

                    _existManager.Remove(group);
                }
            }
        }

        private void WatchThread()
        {
            lock (_lockObject)
            {
                _volatileDownloadItemInfoManager.Update();
            }
        }

        private void Update_DownloadBlockStates(bool state, IEnumerable<Hash> hashes)
        {
            try
            {
                lock (_lockObject)
                {
                    _existManager.Set(hashes, state);
                }
            }
            catch (Exception)
            {

            }
        }

        private void DownloadingThread(CancellationToken token)
        {
            for (; ; )
            {
                if (token.WaitHandle.WaitOne(1000 * 10)) return;

                var items = new List<DownloadItemInfo>();

                lock (_lockObject)
                {
                    items.AddRange(CollectionUtils.Unite(_volatileDownloadItemInfoManager, _downloadItemInfoManager).ToArray()
                        .Where(n => n.State == DownloadState.Downloading));
                }

                foreach (var item in items)
                {
                    try
                    {
                        if (!this.CheckSize(item)) throw new ArgumentException("download size too large.");

                        if (item.Depth == 0)
                        {
                            if (!_cacheManager.Contains(item.Clue.Hash))
                            {
                                item.State = DownloadState.Downloading;

                                _networkManager.Download(item.Clue.Hash);
                            }
                            else
                            {
                                item.State = DownloadState.Decoding;
                            }
                        }
                        else
                        {
                            if (!item.Index.Groups.All(n => _existManager.GetCount(n, true) >= n.Hashes.Count() / 2))
                            {
                                item.State = DownloadState.Downloading;

                                foreach (var group in item.Index.Groups.Randomize())
                                {
                                    if (_existManager.GetCount(group, true) >= group.Hashes.Count() / 2) continue;

                                    foreach (var hash in _existManager.GetHashes(group, false))
                                    {
                                        _networkManager.Download(hash);
                                    }
                                }
                            }
                            else
                            {
                                item.State = DownloadState.ParityDecoding;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        item.State = DownloadState.Error;

                        Log.Error(e);
                    }
                }
            }
        }

        private bool CheckSize(DownloadItemInfo info)
        {
            lock (_lockObject)
            {
                if (info.Clue.Depth > 32) return false;

                var hashes = new List<Hash>();
                {
                    if (info.Depth == 0) hashes.Add(info.Clue.Hash);
                    else hashes.AddRange(info.Index.Groups.SelectMany(n => n.Hashes));
                }

                long sumLength = hashes.Sum(n => (long)_cacheManager.GetLength(n));

                return (sumLength < (info.MaxLength * 3));
            }
        }

        LockedHashSet<Clue> _workingItems = new LockedHashSet<Clue>();

        private void DecodingThread(CancellationToken token)
        {
            for (; ; )
            {
                if (token.WaitHandle.WaitOne(300)) return;

                DownloadItemInfo item = null;

                lock (_lockObject)
                {
                    var tempList = new List<DownloadItemInfo>();

                    if (RandomProvider.GetThreadRandom().Next(0, 2) == 0)
                    {
                        tempList.AddRange(_volatileDownloadItemInfoManager.ToArray());
                    }
                    else
                    {
                        tempList.AddRange(_downloadItemInfoManager.ToArray());
                    }

                    item = tempList
                        .Where(n => !_workingItems.Contains(n.Clue))
                        .Where(n => n.State == DownloadState.Decoding || n.State == DownloadState.ParityDecoding)
                        .OrderBy(n => (n.Depth == n.Clue.Depth) ? 0 : 1)
                        .OrderBy(n => (n.State == DownloadState.Decoding) ? 0 : 1)
                        .FirstOrDefault();

                    if (item != null)
                    {
                        _workingItems.Add(item.Clue);
                    }
                }

                if (item == null) continue;

                try
                {
                    if ((item.Depth == 0 && !_cacheManager.Contains(item.Clue.Hash))
                        || (item.Depth > 0 && !item.Index.Groups.All(n => _existManager.GetCount(n, true) >= n.Hashes.Count() / 2)))
                    {
                        item.State = DownloadState.Downloading;
                    }
                    else
                    {
                        var hashes = new List<Hash>();
                        var totalHashes = new List<Hash>();

                        if (item.Depth == 0)
                        {
                            hashes.Add(item.Clue.Hash);
                            totalHashes.Add(item.Clue.Hash);
                        }
                        else
                        {
                            try
                            {
                                foreach (var group in item.Index.Groups)
                                {
                                    if (item.State == DownloadState.Error) throw new OperationCanceledException();

                                    hashes.AddRange(_cacheManager.ParityDecoding(group, token).Result);
                                }

                                totalHashes.AddRange(item.Index.Groups.SelectMany(n => n.Hashes));
                            }
                            catch (OperationCanceledException)
                            {
                                continue;
                            }

                            item.State = DownloadState.Decoding;
                        }

                        if (item.Depth < item.Clue.Depth)
                        {
                            Index index;

                            try
                            {
                                using (var stream = _cacheManager.Decoding(hashes))
                                using (var progressStream = new ProgressStream(stream, null, 1024 * 1024, token))
                                {
                                    if (item.State == DownloadState.Error) throw new OperationCanceledException();
                                    if (progressStream.Length > item.MaxLength) throw new ArgumentException();

                                    index = Index.Import(progressStream, _bufferPool);
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                continue;
                            }

                            lock (_lockObject)
                            {
                                if (item.Path != null)
                                {
                                    _protectCacheInfoManager.Add(new ProtectedCacheInfo(DateTime.UtcNow, totalHashes));
                                }

                                this.CheckState(index);
                                this.UncheckState(item.Index);

                                item.Index = index;

                                item.Depth++;

                                item.State = DownloadState.Downloading;
                            }
                        }
                        else
                        {
                            if (item.Path != null)
                            {
                                string filePath = null;

                                try
                                {
                                    token.ThrowIfCancellationRequested();

                                    string targetPath;

                                    if (Path.IsPathRooted(item.Path))
                                    {
                                        targetPath = item.Path;
                                    }
                                    else
                                    {
                                        targetPath = Path.GetFullPath(Path.Combine(_basePath, item.Path));

                                        // �f�B���N�g���g���o�[�T���΍�
                                        if (!targetPath.StartsWith(Path.GetFullPath(_basePath)))
                                        {
                                            targetPath = Path.GetFullPath(Path.Combine(_basePath, Path.GetFileName(item.Path)));
                                        }
                                    }

                                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath));

                                    using (var inStream = _cacheManager.Decoding(hashes))
                                    using (var outStream = DownloadManager.GetUniqueFileStream(targetPath + ".tmp"))
                                    using (var safeBuffer = _bufferPool.CreateSafeBuffer(1024 * 1024))
                                    {
                                        filePath = outStream.Name;

                                        int readLength;

                                        while ((readLength = inStream.Read(safeBuffer.Value, 0, safeBuffer.Value.Length)) > 0)
                                        {
                                            if (item.State == DownloadState.Error) throw new OperationCanceledException();
                                            token.ThrowIfCancellationRequested();

                                            outStream.Write(safeBuffer.Value, 0, readLength);
                                        }
                                    }

                                    File.Move(filePath, DownloadManager.GetUniqueFilePath(targetPath));
                                }
                                catch (OperationCanceledException)
                                {
                                    if (filePath != null) File.Delete(filePath);

                                    continue;
                                }
                            }

                            lock (_lockObject)
                            {
                                if (item.Path != null)
                                {
                                    _protectCacheInfoManager.Add(new ProtectedCacheInfo(DateTime.UtcNow, totalHashes));
                                }

                                item.ResultHashes.AddRange(hashes);

                                item.State = DownloadState.Completed;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    item.State = DownloadState.Error;

                    Log.Error(e);
                }
                finally
                {
                    _workingItems.Remove(item.Clue);
                }
            }
        }

        private static UnbufferedFileStream GetUniqueFileStream(string path)
        {
            if (!File.Exists(path))
            {
                try
                {
                    return new UnbufferedFileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.None, BufferPool.Instance);
                }
                catch (DirectoryNotFoundException)
                {
                    throw;
                }
                catch (IOException)
                {

                }
            }

            for (int index = 1; ; index++)
            {
                string text = string.Format(@"{0}\{1} ({2}){3}",
                    Path.GetDirectoryName(path),
                    Path.GetFileNameWithoutExtension(path),
                    index,
                    Path.GetExtension(path));

                if (!File.Exists(text))
                {
                    try
                    {
                        return new UnbufferedFileStream(text, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.None, BufferPool.Instance);
                    }
                    catch (DirectoryNotFoundException)
                    {
                        throw;
                    }
                    catch (IOException)
                    {
                        if (index > 1024) throw;
                    }
                }
            }
        }

        private static string GetUniqueFilePath(string path)
        {
            if (!File.Exists(path))
            {
                return path;
            }

            for (int index = 1; ; index++)
            {
                string text = string.Format(@"{0}\{1} ({2}){3}",
                    Path.GetDirectoryName(path),
                    Path.GetFileNameWithoutExtension(path),
                    index,
                    Path.GetExtension(path));

                if (!File.Exists(text))
                {
                    return text;
                }
            }
        }

        public Stream GetStream(Clue metadata, long maxLength)
        {
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            DownloadItemInfo info;

            lock (_lockObject)
            {
                info = _volatileDownloadItemInfoManager.GetInfo(metadata);

                if (info != null && info.State == DownloadState.Error)
                {
                    _volatileDownloadItemInfoManager.Remove(metadata);
                    info = null;
                }

                if (info == null)
                {
                    info = new DownloadItemInfo(metadata, null, maxLength, 0, null, DownloadState.Downloading, Array.Empty<Hash>());
                    _volatileDownloadItemInfoManager.Add(info);
                }
                else
                {
                    info.MaxLength = Math.Max(info.MaxLength, maxLength);
                }

                if (info.State != DownloadState.Completed) return null;
            }

            Stream stream = null;

            try
            {
                stream = _cacheManager.Decoding(info.ResultHashes);
                if (stream.Length > info.MaxLength) throw new ArgumentException();

                return stream;
            }
            catch (Exception)
            {
                if (stream != null)
                {
                    stream.Dispose();
                    stream = null;
                }

                info.State = DownloadState.Error;
            }

            return stream;
        }

        public void Add(Clue metadata, string path, long maxLength)
        {
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            lock (_lockObject)
            {
                if (_downloadItemInfoManager.Contains(metadata, path)) return;

                var info = new DownloadItemInfo(metadata, path, maxLength, 0, null, DownloadState.Downloading, Array.Empty<Hash>());
                _downloadItemInfoManager.Add(info);
            }
        }

        public void Remove(Clue metadata, string path)
        {
            lock (_lockObject)
            {
                _downloadItemInfoManager.Remove(metadata, path);
            }
        }

        public void Reset(Clue metadata, string path)
        {
            lock (_lockObject)
            {
                var info = _downloadItemInfoManager.GetInfo(metadata, path);
                if (info == null) return;

                this.Remove(metadata, path);
                this.Add(metadata, path, info.MaxLength);
            }
        }

        public override ManagerState StateType
        {
            get
            {
                return _state;
            }
        }

        private readonly object _stateLockObject = new object();

        public override void Start()
        {
            lock (_stateLockObject)
            {
                lock (_lockObject)
                {
                    if (this.StateType == ManagerState.Start) return;
                    _state = ManagerState.Start;

                    _downloadTaskManager.Start();

                    foreach (var taskManager in _decodeTaskManagers)
                    {
                        taskManager.Start();
                    }
                }
            }
        }

        public override void Stop()
        {
            lock (_stateLockObject)
            {
                lock (_lockObject)
                {
                    if (this.StateType == ManagerState.Stop) return;
                    _state = ManagerState.Stop;
                }

                _downloadTaskManager.Stop();

                foreach (var taskManager in _decodeTaskManagers)
                {
                    taskManager.Stop();
                }
            }
        }

        #region ISettings

        public void Load()
        {
            lock (_lockObject)
            {
                int version = _settings.Load("Version", () => 0);

                {
                    string basePath = _settings.Load<string>("BasePath", () => null);
                    int protectedRate = _settings.Load<int>("ProtectedPercentage", () => 30);

                    this.SetConfig(new DownloadOptions(basePath, protectedRate));
                }

                foreach (var info in _settings.Load<DownloadItemInfo[]>("DownloadItemInfos", () => Array.Empty<DownloadItemInfo>()))
                {
                    _downloadItemInfoManager.Add(info);
                }

                foreach (var info in _settings.Load<ProtectedCacheInfo[]>("ProtectedCacheInfos", () => Array.Empty<ProtectedCacheInfo>()))
                {
                    _protectCacheInfoManager.Add(info);
                }
            }
        }

        public void Save()
        {
            lock (_lockObject)
            {
                _settings.Save("Version", 0);

                {
                    var config = this.Config;

                    _settings.Save("BasePath", config.BasePath);
                    _settings.Save("ProtectedPercentage", config.ProtectedPercentage);
                }

                _settings.Save("DownloadItemInfos", _downloadItemInfoManager.ToArray());
                _settings.Save("ProtectedCacheInfos", _protectCacheInfoManager.ToArray());
            }
        }

        #endregion

        private class VolatileDownloadItemInfoManager : IEnumerable<DownloadItemInfo>
        {
            private Dictionary<Clue, Container<DownloadItemInfo>> _downloadItemInfos;

            public VolatileDownloadItemInfoManager()
            {
                _downloadItemInfos = new Dictionary<Clue, Container<DownloadItemInfo>>();
            }

            public event Action<DownloadItemInfo> AddEvent;
            public event Action<DownloadItemInfo> RemoveEvent;

            private void OnAdd(DownloadItemInfo info)
            {
                this.AddEvent?.Invoke(info);
            }

            private void OnRemove(DownloadItemInfo info)
            {
                this.RemoveEvent?.Invoke(info);
            }

            public void Add(DownloadItemInfo info)
            {
                var container = new Container<DownloadItemInfo>();
                container.Value = info;
                container.UpdateTime = DateTime.UtcNow;

                _downloadItemInfos.Add(info.Clue, container);

                this.OnAdd(container.Value);
            }

            public void Remove(Clue metadata)
            {
                Container<DownloadItemInfo> container;
                if (!_downloadItemInfos.TryGetValue(metadata, out container)) return;

                _downloadItemInfos.Remove(metadata);

                this.OnRemove(container.Value);
            }

            public bool Contains(Clue metadata)
            {
                return _downloadItemInfos.ContainsKey(metadata);
            }

            public DownloadItemInfo GetInfo(Clue metadata)
            {
                Container<DownloadItemInfo> container;
                if (!_downloadItemInfos.TryGetValue(metadata, out container)) return null;

                container.UpdateTime = DateTime.UtcNow;

                return container.Value;
            }

            public DownloadItemInfo[] ToArray()
            {
                return _downloadItemInfos.Values.Select(n => n.Value).ToArray();
            }

            public void Update()
            {
                var now = DateTime.UtcNow;

                foreach (var container in _downloadItemInfos.Values.ToArray())
                {
                    if ((now - container.UpdateTime).TotalMinutes < 30) continue;

                    _downloadItemInfos.Remove(container.Value.Clue);
                    this.OnRemove(container.Value);
                }
            }

            #region IEnumerable<DownloadItemInfo>

            public IEnumerator<DownloadItemInfo> GetEnumerator()
            {
                foreach (var info in _downloadItemInfos.Values.Select(n => n.Value))
                {
                    yield return info;
                }
            }

            #endregion

            #region IEnumerable

            IEnumerator IEnumerable.GetEnumerator()
            {
                return this.GetEnumerator();
            }

            #endregion

            private class Container<T>
            {
                public T Value { get; set; }
                public DateTime UpdateTime { get; set; }
            }
        }

        private class DownloadItemInfoManager : IEnumerable<DownloadItemInfo>
        {
            private Dictionary<Clue, Dictionary<string, DownloadItemInfo>> _downloadItemInfos;

            public DownloadItemInfoManager()
            {
                _downloadItemInfos = new Dictionary<Clue, Dictionary<string, DownloadItemInfo>>();
            }

            public event Action<DownloadItemInfo> AddEvents;
            public event Action<DownloadItemInfo> RemoveEvents;

            private void OnAdd(DownloadItemInfo info)
            {
                this.AddEvents?.Invoke(info);
            }

            private void OnRemove(DownloadItemInfo info)
            {
                this.RemoveEvents?.Invoke(info);
            }

            public void Add(DownloadItemInfo info)
            {
                _downloadItemInfos.GetOrAdd(info.Clue, (_) => new Dictionary<string, DownloadItemInfo>()).Add(info.Path, info);

                this.OnAdd(info);
            }

            public void Remove(Clue metadata, string path)
            {
                Dictionary<string, DownloadItemInfo> dic;
                if (!_downloadItemInfos.TryGetValue(metadata, out dic)) return;

                DownloadItemInfo info;
                if (!dic.TryGetValue(path, out info)) return;

                dic.Remove(path);
                if (dic.Count == 0) _downloadItemInfos.Remove(metadata);

                this.OnRemove(info);
            }

            public bool Contains(Clue metadata, string path)
            {
                Dictionary<string, DownloadItemInfo> dic;
                if (!_downloadItemInfos.TryGetValue(metadata, out dic)) return false;

                return dic.ContainsKey(path);
            }

            public DownloadItemInfo GetInfo(Clue metadata, string path)
            {
                Dictionary<string, DownloadItemInfo> dic;
                if (!_downloadItemInfos.TryGetValue(metadata, out dic)) return null;

                DownloadItemInfo info;
                if (!dic.TryGetValue(path, out info)) return null;

                return info;
            }

            public DownloadItemInfo[] ToArray()
            {
                return _downloadItemInfos.Values.SelectMany(n => n.Values).ToArray();
            }

            #region IEnumerable<DownloadItemInfo>

            public IEnumerator<DownloadItemInfo> GetEnumerator()
            {
                foreach (var info in _downloadItemInfos.Values.SelectMany(n => n.Values))
                {
                    yield return info;
                }
            }

            #endregion

            #region IEnumerable

            IEnumerator IEnumerable.GetEnumerator()
            {
                return this.GetEnumerator();
            }

            #endregion
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            if (disposing)
            {
                if (_watchTimer != null)
                {
                    try
                    {
                        _watchTimer.Stop();
                        _watchTimer.Dispose();
                    }
                    catch (Exception)
                    {

                    }

                    _watchTimer = null;
                }

                _downloadTaskManager.Stop();
                _downloadTaskManager.Dispose();

                foreach (var taskManager in _decodeTaskManagers)
                {
                    taskManager.Stop();
                    taskManager.Dispose();
                }
                _decodeTaskManagers.Clear();

                _protectCacheInfoManager.Dispose();
            }
        }
    }

    class DownloadManagerException : ManagerException
    {
        public DownloadManagerException() : base() { }
        public DownloadManagerException(string message) : base(message) { }
        public DownloadManagerException(string message, Exception innerException) : base(message, innerException) { }
    }
}
