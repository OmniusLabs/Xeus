using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiteDB;
using Nito.AsyncEx;
using Omnius.Core;
using Omnius.Core.Cryptography;
using Omnius.Core.Helpers;
using Omnius.Xeus.Interactors.Internal.Models;
using Omnius.Xeus.Interactors.Internal.Repositories.Entities;
using Omnius.Xeus.Utils.Extentions;

namespace Omnius.Xeus.Interactors.Internal.Repositories
{
    internal sealed class UserProfileUploaderRepository : DisposableBase
    {
        private readonly LiteDatabase _database;

        public UserProfileUploaderRepository(string dirPath)
        {
            DirectoryHelper.CreateDirectory(dirPath);

            _database = new LiteDatabase(Path.Combine(dirPath, "lite.db"));
            this.Items = new UploadingUserProfileItemRepository(_database);
        }

        protected override void OnDispose(bool disposing)
        {
            _database.Dispose();
        }

        public async ValueTask MigrateAsync(CancellationToken cancellationToken = default)
        {
            await this.Items.MigrateAsync(cancellationToken);
        }

        public UploadingUserProfileItemRepository Items { get; }

        public sealed class UploadingUserProfileItemRepository
        {
            private const string CollectionName = "uploading_items";

            private readonly LiteDatabase _database;

            private readonly AsyncReaderWriterLock _asyncLock = new();

            public UploadingUserProfileItemRepository(LiteDatabase database)
            {
                _database = database;
            }

            internal async ValueTask MigrateAsync(CancellationToken cancellationToken = default)
            {
                using (await _asyncLock.WriterLockAsync(cancellationToken))
                {
                    if (_database.GetDocumentVersion(CollectionName) <= 0)
                    {
                        var col = this.GetCollection();
                        col.EnsureIndex(x => x.Signature, false);
                        col.EnsureIndex(x => x.RootHash, false);
                    }

                    _database.SetDocumentVersion(CollectionName, 1);
                }
            }

            private ILiteCollection<UploadingUserProfileItemEntity> GetCollection()
            {
                var col = _database.GetCollection<UploadingUserProfileItemEntity>(CollectionName);
                return col;
            }

            public bool Exists(OmniSignature signature)
            {
                using (_asyncLock.ReaderLock())
                {
                    var signatureEntity = OmniSignatureEntity.Import(signature);

                    var col = this.GetCollection();
                    return col.Exists(n => n.Signature == signatureEntity);
                }
            }

            public bool Exists(OmniHash rootHash)
            {
                using (_asyncLock.ReaderLock())
                {
                    var rootHashEntity = OmniHashEntity.Import(rootHash);

                    var col = this.GetCollection();
                    return col.Exists(n => n.RootHash == rootHashEntity);
                }
            }

            public IEnumerable<UploadingUserProfileItem> FindAll()
            {
                using (_asyncLock.ReaderLock())
                {
                    var col = this.GetCollection();
                    return col.FindAll().Select(n => n.Export()).ToArray();
                }
            }

            public UploadingUserProfileItem? FindOne(OmniSignature signature)
            {
                using (_asyncLock.ReaderLock())
                {
                    var signatureEntity = OmniSignatureEntity.Import(signature);

                    var col = this.GetCollection();
                    return col.FindOne(n => n.Signature == signatureEntity).Export();
                }
            }

            public void Upsert(UploadingUserProfileItem item)
            {
                using (_asyncLock.WriterLock())
                {
                    var itemEntity = UploadingUserProfileItemEntity.Import(item);

                    var col = this.GetCollection();

                    col.DeleteMany(n => n.Signature == itemEntity.Signature && n.RootHash == itemEntity.RootHash);
                    col.Insert(itemEntity);
                }
            }

            public void Delete(OmniSignature signature)
            {
                using (_asyncLock.WriterLock())
                {
                    var signatureEntity = OmniSignatureEntity.Import(signature);

                    var col = this.GetCollection();
                    col.DeleteMany(n => n.Signature == signatureEntity);
                }
            }

            public void Delete(OmniHash rootHash)
            {
                using (_asyncLock.WriterLock())
                {
                    var rootHashEntity = OmniHashEntity.Import(rootHash);

                    var col = this.GetCollection();
                    col.DeleteMany(n => n.RootHash == rootHashEntity);
                }
            }
        }
    }
}
