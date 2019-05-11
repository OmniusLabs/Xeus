using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Omnix.Base;
using Omnix.Base.Helpers;
using Omnix.Configuration;
using Omnix.Correction;
using Omnix.Cryptography;
using Omnix.Io;
using Omnix.Serialization;
using Omnix.Serialization.RocketPack;
using Xeus.Core.Contents.Internal;
using Xeus.Core.Contents.Primitives;
using Xeus.Core.Internal;
using Xeus.Messages;
using Xeus.Messages.Reports;

namespace Xeus.Core.Contents
{
    sealed class ContentsStorage : DisposableBase, ISettings, ISetOperators<OmniHash>, IEnumerable<OmniHash>
    {
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        private readonly XeusOptions _options;
        private readonly BufferPool _bufferPool;
        private readonly BlocksStorage _blocksStorage;
        private readonly ContentMetadatasStorage _contentMetadatasStorage;

        private readonly Settings _settings;

        private readonly EventScheduler _checkTimer;

        private readonly EventQueue<OmniHash> _removedBlockEventQueue = new EventQueue<OmniHash>(new TimeSpan(0, 0, 3));

        private readonly object _lockObject = new object();

        private volatile bool _disposed;

        private readonly int _threadCount = 2;

        public ContentsStorage(XeusOptions options, BufferPool bufferPool)
        {
            _options = options;
            _bufferPool = bufferPool;
            _blocksStorage = new BlocksStorage(options, _bufferPool);
            _contentMetadatasStorage = new ContentMetadatasStorage();

            string configPath = Path.Combine(options.ConfigDirectoryPath, nameof(ContentsStorage));
            _settings = new Settings(Path.Combine(configPath, "settings"));

            _checkTimer = new EventScheduler(this.CheckTimer);
        }

        private void CheckTimer()
        {
            this.CheckMessages();
            this.CheckContents();
        }

        public ulong Size
        {
            get
            {
                return _blocksStorage.Size;
            }
        }

        public event Action<IEnumerable<OmniHash>> AddedBlockEvents
        {
            add
            {
                _blocksStorage.AddedBlockEvents += value;
            }
            remove
            {
                _blocksStorage.AddedBlockEvents -= value;
            }
        }

        public event Action<IEnumerable<OmniHash>> RemovedBlockEvents
        {
            add
            {
                _blocksStorage.RemovedBlockEvents += value;
                _removedBlockEventQueue.Events += value;
            }
            remove
            {
                _blocksStorage.RemovedBlockEvents -= value;
                _removedBlockEventQueue.Events -= value;
            }
        }

        public void Lock(OmniHash hash)
        {
            _blocksStorage.Lock(hash);
        }

        public void Unlock(OmniHash hash)
        {
            _blocksStorage.Unlock(hash);
        }

        public bool Contains(OmniHash hash)
        {
            if (_blocksStorage.Contains(hash)) return true;

            lock (_lockObject)
            {
                if (_contentMetadatasStorage.Contains(hash)) return true;
            }

            return false;
        }

        public IEnumerable<OmniHash> IntersectFrom(IEnumerable<OmniHash> collection)
        {
            var hashSet = new HashSet<OmniHash>();
            hashSet.UnionWith(_blocksStorage.IntersectFrom(collection));

            lock (_lockObject)
            {
                hashSet.UnionWith(_contentMetadatasStorage.IntersectFrom(collection));
            }

            return hashSet;
        }

        public IEnumerable<OmniHash> ExceptFrom(IEnumerable<OmniHash> collection)
        {
            var hashSet = new HashSet<OmniHash>(collection);
            hashSet.ExceptWith(_blocksStorage.IntersectFrom(collection));

            lock (_lockObject)
            {
                hashSet.ExceptWith(_contentMetadatasStorage.IntersectFrom(collection));
            }

            return hashSet;
        }

        public void Resize(ulong size)
        {
            _blocksStorage.Resize(size);
        }

        public async ValueTask CheckBlocks(Action<CheckBlocksProgressReport> progress, CancellationToken token)
        {
            await _blocksStorage.CheckBlocks(progress, token);
        }

        public bool TryGetBlock(OmniHash hash, out IMemoryOwner<byte>? memoryOwner)
        {
            if (!EnumHelper.IsValid(hash.AlgorithmType)) throw new ArgumentException($"Incorrect HashAlgorithmType: {hash.AlgorithmType}");

            // Cache
            {
                var result = _blocksStorage.TryGet(hash, out memoryOwner);

                if (result)
                {
                    return true;
                }
            }

            bool success = false;
            string? path = null;

            // Share
            try
            {

                lock (_lockObject)
                {
                    var sharedBlocksInfo = _contentMetadatasStorage.GetSharedBlocksInfo(hash);

                    if (sharedBlocksInfo != null)
                    {
                        ulong position = (ulong)sharedBlocksInfo.GetIndex(hash) * sharedBlocksInfo.BlockLength;
                        uint length = (uint)Math.Min(sharedBlocksInfo.Length - position, sharedBlocksInfo.BlockLength);

                        memoryOwner = _bufferPool.Rent((int)length);

                        try
                        {
                            using (var stream = new UnbufferedFileStream(sharedBlocksInfo.Path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None, _bufferPool))
                            {
                                stream.Seek((long)position, SeekOrigin.Begin);
                                stream.Read(memoryOwner.Memory.Span);
                            }

                            path = sharedBlocksInfo.Path;
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(e);

                            return false;
                        }
                    }
                }

                if (memoryOwner == null)
                {
                    return false;
                }

                if (hash.AlgorithmType == OmniHashAlgorithmType.Sha2_256
                    && BytesOperations.SequenceEqual(Sha2_256.ComputeHash(memoryOwner.Memory.Span), hash.Value.Span))
                {
                    success = true;

                    return true;
                }
                else
                {
                    _logger.Warn("Broken block.");

                    return false;
                }
            }
            finally
            {
                if (!success)
                {
                    if (memoryOwner != null)
                    {
                        memoryOwner.Dispose();
                        memoryOwner = null;
                    }

                    if (path != null)
                    {
                        this.RemoveContent(path);
                    }
                }
            }
        }

        public bool TrySetBlock(OmniHash hash, ReadOnlySpan<byte> value)
        {
            return _blocksStorage.TrySet(hash, value);
        }

        public uint GetLength(OmniHash hash)
        {
            // Cache
            {
                uint length = _blocksStorage.GetLength(hash);
                if (length != 0) return length;
            }

            // Share
            {
                lock (_lockObject)
                {
                    var sharedBlocksInfo = _contentMetadatasStorage.GetSharedBlocksInfo(hash);

                    if (sharedBlocksInfo != null)
                    {
                        return (uint)Math.Min(sharedBlocksInfo.Length - ((ulong)sharedBlocksInfo.BlockLength * (uint)sharedBlocksInfo.GetIndex(hash)), sharedBlocksInfo.BlockLength);
                    }
                }
            }

            return 0;
        }

        public async ValueTask<OmniHash[]> ParityDecode(MerkleTreeSection merkleTreeSection, CancellationToken token = default)
        {
            return await Task.Run(async () =>
            {
                if (merkleTreeSection.CorrectionAlgorithmType == CorrectionAlgorithmType.ReedSolomon8)
                {
                    uint blockLength = merkleTreeSection.Hashes.Max(n => this.GetLength(n));
                    int informationCount = merkleTreeSection.Hashes.Count / 2;

                    if (merkleTreeSection.Hashes.Take(informationCount).All(n => this.Contains(n)))
                    {
                        return merkleTreeSection.Hashes.Take(informationCount).ToArray();
                    }

                    var blockMemoryOwners = new IMemoryOwner<byte>[informationCount];
                    var indexes = new int[informationCount];

                    try
                    {
                        // Load
                        {
                            int count = 0;

                            for (int i = 0; i < merkleTreeSection.Hashes.Count; i++)
                            {
                                token.ThrowIfCancellationRequested();

                                if (!this.TryGetBlock(merkleTreeSection.Hashes[i], out var blockMemoryOwner) || blockMemoryOwner == null)
                                {
                                    continue;
                                }

                                if (blockMemoryOwner.Memory.Length < blockLength)
                                {
                                    var tempMemoryOwner = _bufferPool.Rent((int)blockLength);

                                    BytesOperations.Copy(blockMemoryOwner.Memory.Span, tempMemoryOwner.Memory.Span, blockMemoryOwner.Memory.Length);
                                    BytesOperations.Zero(tempMemoryOwner.Memory.Span.Slice(blockMemoryOwner.Memory.Length));

                                    blockMemoryOwner.Dispose();
                                    blockMemoryOwner = tempMemoryOwner;
                                }

                                indexes[count] = i;
                                blockMemoryOwners[count] = blockMemoryOwner;

                                count++;

                                if (count >= informationCount) break;
                            }

                            if (count < informationCount) throw new BlockNotFoundException();
                        }

                        var reedSolomon = new ReedSolomon8(informationCount, _threadCount, _bufferPool);
                        await reedSolomon.Decode(blockMemoryOwners.Select(n => n.Memory).ToArray(), indexes, (int)blockLength, informationCount * 2, token);

                        // Set
                        {
                            ulong length = merkleTreeSection.Length;

                            for (int i = 0; i < informationCount; length -= blockLength, i++)
                            {
                                bool result = _blocksStorage.TrySet(merkleTreeSection.Hashes[i], blockMemoryOwners[i].Memory.Span.Slice(0, (int)Math.Min(length, blockLength)));

                                if (!result)
                                {
                                    throw new ParityDecodeFailed("Failed to save Block.");
                                }
                            }
                        }
                    }
                    finally
                    {
                        foreach (var memoryOwner in blockMemoryOwners)
                        {
                            memoryOwner.Dispose();
                        }
                    }

                    return merkleTreeSection.Hashes.Take(informationCount).ToArray();
                }
                else
                {
                    throw new NotSupportedException();
                }
            });
        }

        public async ValueTask<Clue> Import(Stream stream, CancellationToken token = default)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            return await Task.Run(async () =>
            {
                Clue? clue = null;
                var lockedHashes = new HashSet<OmniHash>();

                try
                {
                    const uint blockLength = 1024 * 1024;
                    const OmniHashAlgorithmType hashAlgorithmType = OmniHashAlgorithmType.Sha2_256;
                    const CorrectionAlgorithmType correctionAlgorithmType = CorrectionAlgorithmType.ReedSolomon8;

                    byte depth = 0;
                    var creationTime = DateTime.UtcNow;

                    var merkleTreeSectionList = new List<MerkleTreeSection>();

                    for (; ; )
                    {
                        if (stream.Length <= blockLength)
                        {
                            OmniHash hash;

                            using (var bufferMemoryOwner = _bufferPool.Rent((int)blockLength))
                            {
                                stream.Read(bufferMemoryOwner.Memory.Span);

                                if (hashAlgorithmType == OmniHashAlgorithmType.Sha2_256)
                                {
                                    hash = new OmniHash(OmniHashAlgorithmType.Sha2_256, Sha2_256.ComputeHash(bufferMemoryOwner.Memory.Span));
                                }

                                _blocksStorage.Lock(hash);
                                lockedHashes.Add(hash);

                                bool result = _blocksStorage.TrySet(hash, bufferMemoryOwner.Memory.Span);

                                if (!result)
                                {
                                    throw new ImportFailed("Failed to save Block.");
                                }
                            }

                            stream.Dispose();

                            clue = new Clue(hash, depth);

                            break;
                        }
                        else
                        {
                            for (; ; )
                            {
                                var targetHashes = new List<OmniHash>();
                                var targetMemoryOwners = new List<IMemoryOwner<byte>>();
                                ulong sumLength = 0;

                                try
                                {
                                    for (int i = 0; stream.Position < stream.Length; i++)
                                    {
                                        token.ThrowIfCancellationRequested();

                                        uint length = (uint)Math.Min(stream.Length - stream.Position, blockLength);
                                        var bufferMemoryOwner = _bufferPool.Rent((int)length);

                                        try
                                        {
                                            stream.Read(bufferMemoryOwner.Memory.Span);

                                            sumLength += length;
                                        }
                                        catch (Exception e)
                                        {
                                            bufferMemoryOwner.Dispose();

                                            throw e;
                                        }

                                        OmniHash hash;

                                        if (hashAlgorithmType == OmniHashAlgorithmType.Sha2_256)
                                        {
                                            hash = new OmniHash(OmniHashAlgorithmType.Sha2_256, Sha2_256.ComputeHash(bufferMemoryOwner.Memory.Span));
                                        }

                                        _blocksStorage.Lock(hash);
                                        lockedHashes.Add(hash);

                                        bool result = _blocksStorage.TrySet(hash, bufferMemoryOwner.Memory.Span);

                                        if (!result)
                                        {
                                            throw new ImportFailed("Failed to save Block.");
                                        }

                                        targetHashes.Add(hash);
                                        targetMemoryOwners.Add(bufferMemoryOwner);

                                        if (targetMemoryOwners.Count >= 128) break;
                                    }

                                    var parityHashes = await this.ParityEncode(targetMemoryOwners.Select(n => n.Memory), hashAlgorithmType, correctionAlgorithmType, token);
                                    lockedHashes.UnionWith(parityHashes);

                                    merkleTreeSectionList.Add(new MerkleTreeSection(correctionAlgorithmType, sumLength, CollectionHelper.Unite(targetHashes, parityHashes).ToArray()));
                                }
                                finally
                                {
                                    foreach (var memoryOwner in targetMemoryOwners)
                                    {
                                        memoryOwner.Dispose();
                                    }
                                }

                                if (stream.Position == stream.Length) break;
                            }

                            depth++;

                            stream.Dispose();
                            stream = RocketPackHelper.MessageToStream(new MerkleTreeNode(merkleTreeSectionList.ToArray()));
                        }
                    }
                }
                finally
                {
                    stream.Dispose();
                }

                lock (_lockObject)
                {
                    if (!_contentMetadatasStorage.ContainsMessageContentMetadata(clue))
                    {
                        _contentMetadatasStorage.Add(new ContentMetadata(clue, Timestamp.FromDateTime(DateTime.UtcNow), lockedHashes.ToArray(), null));
                    }
                    else
                    {
                        foreach (var hash in lockedHashes)
                        {
                            _blocksStorage.Unlock(hash);
                        }
                    }
                }

                return clue;
            }, token);
        }

        public async ValueTask<Clue> Import(string path, DateTime creationTime, CancellationToken token = default)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));

            return await Task.Run(async () =>
            {
                // Check
                lock (_lockObject)
                {
                    var info = _contentMetadatasStorage.GetFileContentMetadata(path);
                    if (info != null) return info.Clue;
                }

                Clue? clue = null;
                var lockedHashes = new HashSet<OmniHash>();
                SharedBlocksMetadata? sharedBlocksInfo = null;

                {
                    const int blockLength = 1024 * 1024;
                    const OmniHashAlgorithmType hashAlgorithmType = OmniHashAlgorithmType.Sha2_256;
                    const CorrectionAlgorithmType correctionAlgorithmType = CorrectionAlgorithmType.ReedSolomon8;

                    byte depth = 0;

                    var merkleTreeSectionList = new List<MerkleTreeSection>();

                    // File
                    using (var stream = new UnbufferedFileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None, _bufferPool))
                    {
                        if (stream.Length <= blockLength)
                        {
                            OmniHash hash;

                            using (var bufferMemoryOwner = _bufferPool.Rent((int)stream.Length))
                            {
                                stream.Read(bufferMemoryOwner.Memory.Span);

                                if (hashAlgorithmType == OmniHashAlgorithmType.Sha2_256)
                                {
                                    hash = new OmniHash(OmniHashAlgorithmType.Sha2_256, Sha2_256.ComputeHash(bufferMemoryOwner.Memory.Span));
                                }
                            }

                            sharedBlocksInfo = new SharedBlocksMetadata(path, (ulong)stream.Length, (uint)stream.Length, new OmniHash[] { hash });
                            clue = new Clue(hash, depth);
                        }
                        else
                        {
                            var sharedHashes = new List<OmniHash>();

                            for (; ; )
                            {
                                var targetHashes = new List<OmniHash>();
                                var targetMemoryOwners = new List<IMemoryOwner<byte>>();
                                ulong sumLength = 0;

                                try
                                {
                                    for (int i = 0; stream.Position < stream.Length; i++)
                                    {
                                        token.ThrowIfCancellationRequested();

                                        uint length = (uint)Math.Min(stream.Length - stream.Position, blockLength);
                                        var bufferMemoryOwner = _bufferPool.Rent((int)length);

                                        try
                                        {
                                            stream.Read(bufferMemoryOwner.Memory.Span);

                                            sumLength += length;
                                        }
                                        catch (Exception e)
                                        {
                                            bufferMemoryOwner.Dispose();

                                            throw e;
                                        }

                                        OmniHash hash;

                                        if (hashAlgorithmType == OmniHashAlgorithmType.Sha2_256)
                                        {
                                            hash = new OmniHash( OmniHashAlgorithmType.Sha2_256, Sha2_256.ComputeHash(bufferMemoryOwner.Memory.Span));
                                        }

                                        sharedHashes.Add(hash);

                                        targetHashes.Add(hash);
                                        targetMemoryOwners.Add(bufferMemoryOwner);

                                        if (targetMemoryOwners.Count >= 128) break;
                                    }

                                    var parityHashes = await this.ParityEncode(targetMemoryOwners.Select(n=>n.Memory), hashAlgorithmType, correctionAlgorithmType, token);
                                    lockedHashes.UnionWith(parityHashes);

                                    merkleTreeSectionList.Add(new MerkleTreeSection(correctionAlgorithmType, sumLength, CollectionHelper.Unite(targetHashes, parityHashes).ToArray()));
                                }
                                finally
                                {
                                    foreach (var memoryOwner in targetMemoryOwners)
                                    {
                                        memoryOwner.Dispose();
                                    }
                                }

                                if (stream.Position == stream.Length) break;
                            }

                            sharedBlocksInfo = new SharedBlocksMetadata(path, (ulong)stream.Length, blockLength, sharedHashes.ToArray());

                            depth++;
                        }
                    }

                    while (merkleTreeSectionList.Count > 0)
                    {
                        // Index
                        using (var stream = RocketPackHelper.MessageToStream(new MerkleTreeNode(merkleTreeSectionList.ToArray())))
                        {
                            merkleTreeSectionList.Clear();

                            if (stream.Length <= blockLength)
                            {
                                OmniHash hash;

                                using (var bufferMemoryOwner = _bufferPool.Rent((int)stream.Length))
                                {
                                    stream.Read(bufferMemoryOwner.Memory.Span);
 
                                    if (hashAlgorithmType == OmniHashAlgorithmType.Sha2_256)
                                    {
                                        hash = new OmniHash(OmniHashAlgorithmType.Sha2_256, Sha2_256.ComputeHash(bufferMemoryOwner.Memory.Span));
                                    }

                                    _blocksStorage.Lock(hash);

                                    bool result = _blocksStorage.TrySet(hash, bufferMemoryOwner.Memory.Span);

                                    if (!result)
                                    {
                                        throw new ImportFailed("Failed to save Block.");
                                    }

                                    lockedHashes.Add(hash);
                                }

                                clue = new Clue(hash, depth);
                            }
                            else
                            {
                                for (; ; )
                                {
                                    var targetHashes = new List<OmniHash>();
                                    var targetMemoryOwners = new List<IMemoryOwner<byte>>();
                                    ulong sumLength = 0;

                                    try
                                    {
                                        for (int i = 0; stream.Position < stream.Length; i++)
                                        {
                                            token.ThrowIfCancellationRequested();

                                            uint length = (uint)Math.Min(stream.Length - stream.Position, blockLength);
                                            var bufferMemoryOwner = _bufferPool.Rent((int)length);

                                            try
                                            {
                                                stream.Read(bufferMemoryOwner.Memory.Span);

                                                sumLength += length;
                                            }
                                            catch (Exception e)
                                            {
                                                bufferMemoryOwner.Dispose();

                                                throw e;
                                            }

                                            OmniHash hash;

                                            if (hashAlgorithmType ==  OmniHashAlgorithmType.Sha2_256)
                                            {
                                                hash = new OmniHash(OmniHashAlgorithmType.Sha2_256, Sha2_256.ComputeHash(bufferMemoryOwner.Memory.Span));
                                            }

                                            _blocksStorage.Lock(hash);

                                            bool result = _blocksStorage.TrySet(hash, bufferMemoryOwner.Memory.Span);

                                            if (!result)
                                            {
                                                throw new ImportFailed("Failed to save Block.");
                                            }

                                            lockedHashes.Add(hash);

                                            targetHashes.Add(hash);
                                           targetMemoryOwners.Add(bufferMemoryOwner);

                                            if (targetMemoryOwners.Count >= 128) break;
                                        }

                                        var parityHashes = await this.ParityEncode(targetMemoryOwners.Select(n=>n.Memory), hashAlgorithmType, correctionAlgorithmType, token);
                                        lockedHashes.UnionWith(parityHashes);

                                        merkleTreeSectionList.Add(new MerkleTreeSection(correctionAlgorithmType, sumLength, CollectionHelper.Unite(targetHashes, parityHashes).ToArray()));
                                    }
                                    finally
                                    {
                                        foreach (var memoryOwner in targetMemoryOwners)
                                        {
                                            memoryOwner.Dispose();
                                        }
                                    }

                                    if (stream.Position == stream.Length) break;
                                }

                                depth++;
                            }
                        }
                    }
                }

                if (clue == null)
                {
                    throw new ImportFailed("clue is null");
                }

                lock (_lockObject)
                {
                    if (!_contentMetadatasStorage.ContainsFileContentMetadata(path))
                    {
                        _contentMetadatasStorage.Add(new ContentMetadata(clue, Timestamp.FromDateTime(creationTime), lockedHashes.ToArray(), sharedBlocksInfo));

                        foreach (var hash in lockedHashes)
                        {
                            _blocksStorage.Lock(hash);
                        }
                    }
                }

                return clue;
            }, token);
        }

        private async ValueTask<OmniHash[]> ParityEncode(IEnumerable<Memory<byte>> buffers, OmniHashAlgorithmType hashAlgorithmType, CorrectionAlgorithmType correctionAlgorithmType, CancellationToken token = default)
        {
            return await Task.Run(() =>
            {
                if (correctionAlgorithmType == CorrectionAlgorithmType.ReedSolomon8)
                {
                    if (buffers.Count() > 128) throw new ArgumentOutOfRangeException(nameof(buffers));

                    var createdMemoryOwners = new List<IMemoryOwner<byte>>();

                    try
                    {
                        var targetBuffers = new ReadOnlyMemory<byte>[buffers.Count()];
                        var parityMemoryOwners = new IMemoryOwner<byte>[buffers.Count()];

                        int blockLength = buffers.Max(n => n.Length);

                        // Normalize
                        {
                            int index = 0;

                            foreach (var buffer in buffers)
                            {
                                token.ThrowIfCancellationRequested();

                                if (buffer.Length < blockLength)
                                {
                                    var tempMemoryOwner = _bufferPool.Rent((int)blockLength);

                                    BytesOperations.Copy(buffer.Span, tempMemoryOwner.Memory.Span, buffer.Length);
                                    BytesOperations.Zero(tempMemoryOwner.Memory.Span.Slice(buffer.Length));

                                    createdMemoryOwners.Add(tempMemoryOwner);

                                    targetBuffers[index] = tempMemoryOwner.Memory;
                                }
                                else
                                {
                                    targetBuffers[index] = buffer;
                                }

                                index++;
                            }
                        }

                        for (int i = 0; i < parityMemoryOwners.Length; i++)
                        {
                            parityMemoryOwners[i] = _bufferPool.Rent(blockLength);
                        }

                        var indexes = new int[parityMemoryOwners.Length];

                        for (int i = 0; i < parityMemoryOwners.Length; i++)
                        {
                            indexes[i] = targetBuffers.Length + i;
                        }

                        var reedSolomon = new ReedSolomon8(targetBuffers.Length, targetBuffers.Length + parityMemoryOwners.Length, _bufferPool);
                        reedSolomon.Encode(targetBuffers, indexes, parityMemoryOwners.Select(n => n.Memory).ToArray(), blockLength, _threadCount, token).Wait();

                        token.ThrowIfCancellationRequested();

                        var parityHashes = new List<OmniHash>();

                        for (int i = 0; i < parityMemoryOwners.Length; i++)
                        {
                            OmniHash hash;

                            if (hashAlgorithmType == OmniHashAlgorithmType.Sha2_256)
                            {
                                hash = new OmniHash(OmniHashAlgorithmType.Sha2_256, Sha2_256.ComputeHash(parityMemoryOwners[i].Memory.Span));
                            }
                            else
                            {
                                throw new NotSupportedException();
                            }

                            _blocksStorage.Lock(hash);

                            bool result = _blocksStorage.TrySet(hash, parityMemoryOwners[i].Memory.Span);

                            if (!result)
                            {
                                throw new ImportFailed("Failed to save Block.");
                            }

                            parityHashes.Add(hash);
                        }

                        return parityHashes.ToArray();
                    }
                    finally
                    {
                        foreach (var memoryOwner in createdMemoryOwners)
                        {
                            memoryOwner.Dispose();
                        }
                    }
                }
                else
                {
                    throw new NotSupportedException();
                }
            });
        }

        #region Message

        private void CheckMessages()
        {
            lock (_lockObject)
            {
                foreach (var contentInfo in _contentMetadatasStorage.GetMessageContentMetadatas())
                {
                    if (contentInfo.LockedHashes.All(n => this.Contains(n))) continue;

                    this.RemoveMessage(contentInfo.Clue);
                }
            }
        }

        public void RemoveMessage(Clue clue)
        {
            lock (_lockObject)
            {
                var contentInfo = _contentMetadatasStorage.GetMessageContentMetadata(clue);
                if (contentInfo == null) return;

                _contentMetadatasStorage.RemoveMessageContentMetadata(clue);

                foreach (var hash in contentInfo.LockedHashes)
                {
                    _blocksStorage.Unlock(hash);
                }

                if (contentInfo.SharedBlocksMetadata != null)
                {
                    // Event
                    _removedBlockEventQueue.Enqueue(contentInfo.SharedBlocksMetadata.Hashes.Where(n => !this.Contains(n)).ToArray());
                }
            }
        }

        #endregion

        #region Content

        private void CheckContents()
        {
            lock (_lockObject)
            {
                foreach (var contentInfo in _contentMetadatasStorage.GetFileContentMetadatas())
                {
                    if (contentInfo.LockedHashes.All(n => this.Contains(n))) continue;

                    if (contentInfo.SharedBlocksMetadata != null)
                    {
                        this.RemoveContent(contentInfo.SharedBlocksMetadata.Path);
                    }
                }
            }
        }

        public void RemoveContent(string path)
        {
            lock (_lockObject)
            {
                var contentInfo = _contentMetadatasStorage.GetFileContentMetadata(path);
                if (contentInfo == null) return;

                _contentMetadatasStorage.RemoveFileContentMetadata(path);

                foreach (var hash in contentInfo.LockedHashes)
                {
                    _blocksStorage.Unlock(hash);
                }

                if (contentInfo.SharedBlocksMetadata != null)
                {
                    // Event
                    _removedBlockEventQueue.Enqueue(contentInfo.SharedBlocksMetadata.Hashes.Where(n => !this.Contains(n)).ToArray());
                }
            }
        }

        public IEnumerable<OmniHash> GetContentHashes(string path)
        {
            lock (_lockObject)
            {
                var contentInfo = _contentMetadatasStorage.GetFileContentMetadata(path);
                if (contentInfo == null) Enumerable.Empty<OmniHash>();

                return contentInfo.LockedHashes.ToArray();
            }
        }

        #endregion

        #region ISettings

        public void Load()
        {
            lock (_lockObject)
            {
                _blocksStorage.Load();

                var config = _settings.Load<ContentsManagerConfig>("Config");

                foreach (var contentInfo in config.ContentMetadatas)
                {
                    _contentMetadatasStorage.Add(contentInfo);

                    foreach (var hash in contentInfo.LockedHashes)
                    {
                        _blocksStorage.Lock(hash);
                    }
                }

                _checkTimer.Start(new TimeSpan(0, 0, 0), new TimeSpan(0, 10, 0));
            }
        }

        public void Save()
        {
            lock (_lockObject)
            {
                _blocksStorage.Save();

                var config = new ContentsManagerConfig(0, _contentMetadatasStorage.ToArray());
                _settings.Save("Config", config);
            }
        }

        #endregion

        public OmniHash[] ToArray()
        {
            lock (_lockObject)
            {
                var hashSet = new HashSet<OmniHash>();
                hashSet.UnionWith(_blocksStorage.ToArray());
                hashSet.UnionWith(_contentMetadatasStorage.GetHashes());

                return hashSet.ToArray();
            }
        }

        #region IEnumerable<OmniHash>

        public IEnumerator<OmniHash> GetEnumerator()
        {
            lock (_lockObject)
            {
                foreach (var hash in this.ToArray())
                {
                    yield return hash;
                }
            }
        }

        #endregion

        #region IEnumerable

        IEnumerator IEnumerable.GetEnumerator()
        {
            lock (_lockObject)
            {
                return this.GetEnumerator();
            }
        }

        #endregion

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            if (disposing)
            {
                _removedBlockEventQueue.Dispose();

                _blocksStorage.Dispose();
                _checkTimer.Dispose();
            }
        }
    }

    sealed class BlockNotFoundException : Exception
    {
        public BlockNotFoundException() { }
        public BlockNotFoundException(string message) : base(message) { }
        public BlockNotFoundException(string message, Exception innerException) : base(message, innerException) { }
    }

    sealed class ParityDecodeFailed : Exception
    {
        public ParityDecodeFailed() { }
        public ParityDecodeFailed(string message) : base(message) { }
        public ParityDecodeFailed(string message, Exception innerException) : base(message, innerException) { }
    }

    sealed class ImportFailed : Exception
    {
        public ImportFailed() { }
        public ImportFailed(string message) : base(message) { }
        public ImportFailed(string message, Exception innerException) : base(message, innerException) { }
    }
}