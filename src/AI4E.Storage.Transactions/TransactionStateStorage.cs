﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AI4E.Internal;
using AI4E.Utils;
using Newtonsoft.Json;
using static System.Diagnostics.Debug;

namespace AI4E.Storage.Transactions
{
    public sealed class TransactionStateStorage : ITransactionStateStorage
    {
        private readonly IDatabase _database;

        public TransactionStateStorage(IDatabase database)
        {
            if (database == null)
                throw new ArgumentNullException(nameof(database));

            _database = database;
        }

        public Task<bool> CompareExchangeAsync(ITransactionState transaction,
                                                    ITransactionState comparand,
                                                    CancellationToken cancellation = default)
        {
            var data = AsStoredTransaction(transaction);
            var cmpr = AsStoredTransaction(comparand);

            return _database.CompareExchangeAsync(data, cmpr, (p, q) => (p.Version == q.Version), cancellation);
        }

        public Task RemoveAsync(ITransactionState transaction, CancellationToken cancellation = default)
        {
            var data = AsStoredTransaction(transaction);
            return _database.RemoveAsync(data, cancellation);
        }

        private sealed class NextTransactionId
        {
            public string Id { get; } = string.Empty;
            public long LastId { get; set; }
        }

        public async ValueTask<long> GetUniqueTransactionIdAsync(CancellationToken cancellation = default)
        {
            // TODO: This is not very performant.

            NextTransactionId current, desired;

            do
            {
                current = await _database.GetOneAsync<NextTransactionId>(cancellation);
                desired = new NextTransactionId { LastId = (current?.LastId ?? 0) + 1 };
            }
            while (!await _database.CompareExchangeAsync(desired, current, (p, q) => (p.LastId == q.LastId), cancellation));

            return desired.LastId;
        }

        public async ValueTask<ITransactionState> GetTransactionAsync(long id, CancellationToken cancellation = default)
        {
            return await _database.GetOneAsync<StoredTransaction>(p => p.Id == id, cancellation);
        }

        public IAsyncEnumerable<ITransactionState> GetNonCommittedTransactionsAsync(CancellationToken cancellation = default)
        {
            return _database.GetAsync<StoredTransaction>(p => p.Status == TransactionStatus.AbortRequested || p.Status == TransactionStatus.Pending, cancellation);
        }

        private static StoredTransaction AsStoredTransaction(ITransactionState transaction)
        {
            if (transaction == null)
                return null;

            if (transaction is StoredTransaction result)
                return result;

            return new StoredTransaction(transaction);
        }

        private static StoredOperation AsStoredOperation(IOperation operation)
        {
            if (operation == null)
                return null;

            if (operation is StoredOperation result)
                return result;

            return new StoredOperation(operation);
        }

        private sealed class StoredTransaction : ITransactionState
        {
            public StoredTransaction()
            {
                Operations = new List<StoredOperation>();
            }

            public StoredTransaction(ITransactionState transaction)
            {
                Assert(transaction != null);

                Id = transaction.Id;
                Operations = new List<StoredOperation>(transaction.Operations.Select(p => AsStoredOperation(p)));
                Status = transaction.Status;
                Version = transaction.Version;
            }

            public long Id { get; private set; }

            public List<StoredOperation> Operations { get; private set; }

            public TransactionStatus Status { get; private set; }

            public int Version { get; private set; }

            ImmutableArray<IOperation> ITransactionState.Operations => ImmutableArray<IOperation>.CastUp(Operations.ToImmutableArray());
        }

        private sealed class StoredOperation : IOperation
        {
            private readonly JsonSerializerSettings _serializerSettings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                Formatting = Formatting.Indented,
                PreserveReferencesHandling = PreserveReferencesHandling.Objects,
                ReferenceLoopHandling = ReferenceLoopHandling.Serialize,
                ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor
            };

            public StoredOperation() { }

            public StoredOperation(IOperation operation)
            {
                Assert(operation != null);

                Id = operation.Id;

                var json = JsonConvert.SerializeObject(operation.Entry,
                                                       operation.EntryType,
                                                       _serializerSettings);

                Entry = CompressionHelper.Zip(json);
                EntryType = operation.EntryType.GetUnqualifiedTypeName();
                ExpectedVersion = operation.ExpectedVersion;
                OperationType = operation.OperationType;
                State = operation.State;
                TransactionId = operation.TransactionId;
            }

            public byte[] Entry { get; private set; }

            public string EntryType { get; private set; }

            public int? ExpectedVersion { get; private set; }

            public OperationType OperationType { get; private set; }

            public OperationState State { get; private set; }

            public long Id { get; private set; }

            public long TransactionId { get; private set; }

            Type IOperation.EntryType => TypeLoadHelper.LoadTypeFromUnqualifiedName(EntryType);

            object IOperation.Entry
            {
                get
                {
                    var json = CompressionHelper.Unzip(Entry);

                    return JsonConvert.DeserializeObject(json, TypeLoadHelper.LoadTypeFromUnqualifiedName(EntryType), _serializerSettings);
                }
            }
        }
    }
}

