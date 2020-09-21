using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiteDB;
using Omnius.Core;
using Omnius.Core.Cryptography;
using Omnius.Core.Io;
using Omnius.Core.Serialization;
using Omnius.Xeus.Components.Models;
using Omnius.Xeus.Components.Storages.Internal;

namespace Omnius.Xeus.Components.Storages
{
    public sealed class PushDeclaredMessageStorage : AsyncDisposableBase, IPushDeclaredMessageStorage
    {
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        private readonly PushDeclaredMessageStorageOptions _options;
        private readonly IBytesPool _bytesPool;

        private readonly Database _Database;

        private readonly AsyncLock _asyncLock = new AsyncLock();

        internal sealed class PushDeclaredMessageStorageFactory : IPushDeclaredMessageStorageFactory
        {
            public async ValueTask<IPushDeclaredMessageStorage> CreateAsync(PushDeclaredMessageStorageOptions options,
                IBytesPool bytesPool)
            {
                var result = new PushDeclaredMessageStorage(options, bytesPool);
                await result.InitAsync();

                return result;
            }
        }

        public static IPushDeclaredMessageStorageFactory Factory { get; } = new PushDeclaredMessageStorageFactory();

        private PushDeclaredMessageStorage(PushDeclaredMessageStorageOptions options, IBytesPool bytesPool)
        {
            _options = options;
            _bytesPool = bytesPool;

            _Database = new Database(Path.Combine(_options.ConfigPath, "database"));
        }

        internal async ValueTask InitAsync()
        {
            await _Database.MigrateAsync();
        }

        protected override async ValueTask OnDisposeAsync()
        {
            _Database.Dispose();
        }

        public async ValueTask<PushDeclaredMessageStorageReport> GetReportAsync(CancellationToken cancellationToken = default)
        {
            using (await _asyncLock.LockAsync())
            {
                throw new NotImplementedException();
            }
        }

        public async ValueTask CheckConsistencyAsync(Action<ConsistencyReport> callback, CancellationToken cancellationToken = default)
        {
        }

        public ValueTask<IEnumerable<OmniSignature>> GetSignaturesAsync(CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public async ValueTask<bool> ContainsMessageAsync(OmniSignature signature, DateTime since = default)
        {
            using (await _asyncLock.LockAsync())
            {
                var status = _Database.PushStatus.Get(signature);
                if (status == null) return false;
                return true;
            }
        }

        public async ValueTask RegisterPushMessageAsync(DeclaredMessage message, CancellationToken cancellationToken = default)
        {
            using (await _asyncLock.LockAsync())
            {
                var signature = message.Certificate?.GetOmniSignature();
                if (signature == null) return;

                _Database.PushStatus.Add(new PushStatus(signature, message.CreationTime.ToDateTime()));

                var filePath = Path.Combine(_options.ConfigPath, "cache", SignatureToString(signature));

                using var fileStream = new UnbufferedFileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.None, _bytesPool);
                var pipeWriter = PipeWriter.Create(fileStream);
                message.Export(pipeWriter, _bytesPool);
                await pipeWriter.CompleteAsync();
            }
        }

        public async ValueTask UnregisterPushMessageAsync(OmniSignature signature, CancellationToken cancellationToken = default)
        {
            using (await _asyncLock.LockAsync())
            {
                _Database.PushStatus.Remove(signature);
            }
        }

        public async ValueTask<DateTime?> ReadMessageCreationTimeAsync(OmniSignature signature, CancellationToken cancellationToken = default)
        {
            using (await _asyncLock.LockAsync())
            {
                var status = _Database.PushStatus.Get(signature);
                if (status == null) return null;

                return status.CreationTime;
            }
        }

        public async ValueTask<DeclaredMessage?> ReadMessageAsync(OmniSignature signature, CancellationToken cancellationToken = default)
        {
            using (await _asyncLock.LockAsync())
            {
                var status = _Database.PushStatus.Get(signature);
                if (status == null) return null;

                var filePath = Path.Combine(_options.ConfigPath, "cache", SignatureToString(signature));

                using var fileStream = new UnbufferedFileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, FileOptions.None, _bytesPool);
                var pipeReader = PipeReader.Create(fileStream);
                var readResult = await pipeReader.ReadAsync(cancellationToken);
                var message = DeclaredMessage.Import(readResult.Buffer, _bytesPool);
                await pipeReader.CompleteAsync();

                return message;
            }
        }

        private static string SignatureToString(OmniSignature signature)
        {
            var hub = new BytesHub(BytesPool.Shared);
            signature.Export(hub.Writer, BytesPool.Shared);
            var hash = new OmniHash(OmniHashAlgorithmType.Sha2_256, Sha2_256.ComputeHash(hub.Reader.GetSequence()));
            return hash.ToString(ConvertStringType.Base16);
        }

        private sealed class PushStatus
        {
            public PushStatus(OmniSignature signature, DateTime creationTime)
            {
                this.Signature = signature;
                this.CreationTime = creationTime;
            }

            public OmniSignature Signature { get; }
            public DateTime CreationTime { get; }
        }

        private sealed class Database : IDisposable
        {
            private readonly LiteDatabase _database;

            public Database(string path)
            {
                _database = new LiteDatabase(path);
                this.PushStatus = new PushStatusDatabase(_database);
            }

            public void Dispose()
            {
                _database.Dispose();
            }

            public async ValueTask MigrateAsync(CancellationToken cancellationToken = default)
            {
                if (0 <= _database.UserVersion)
                {
                    var wants = _database.GetCollection<PushStatusEntity>("pushes");
                    wants.EnsureIndex(x => x.Signature, true);
                    _database.UserVersion = 1;
                }
            }

            public PushStatusDatabase PushStatus { get; set; }

            public sealed class PushStatusDatabase
            {
                private readonly LiteDatabase _database;

                public PushStatusDatabase(LiteDatabase database)
                {
                    _database = database;
                }

                public IEnumerable<PushStatus> GetAll()
                {
                    var col = _database.GetCollection<PushStatusEntity>("pushes");
                    return col.FindAll().Select(n => n.Export());
                }

                public PushStatus? Get(OmniSignature signature)
                {
                    var col = _database.GetCollection<PushStatusEntity>("pushes");
                    var param = OmniSignatureEntity.Import(signature);
                    return col.FindOne(n => n.Signature == param).Export();
                }

                public void Add(PushStatus status)
                {
                    var col = _database.GetCollection<PushStatusEntity>("pushes");
                    col.Upsert(PushStatusEntity.Import(status));
                }

                public void Remove(OmniSignature signature)
                {
                    var col = _database.GetCollection<PushStatusEntity>("pushes");
                    var param = OmniSignatureEntity.Import(signature);
                    col.DeleteMany(n => n.Signature == param);
                }
            }

            private sealed class PushStatusEntity
            {
                public int Id { get; set; }
                public OmniSignatureEntity? Signature { get; set; }
                public DateTime CreationTime { get; set; }

                public static PushStatusEntity Import(PushStatus value)
                {
                    return new PushStatusEntity() { Signature = OmniSignatureEntity.Import(value.Signature), CreationTime = value.CreationTime };
                }

                public PushStatus Export()
                {
                    return new PushStatus(this.Signature!.Export(), this.CreationTime);
                }
            }
        }
    }
}
