﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Documents.Operations;
using Raven.Client.Util.RateLimiting;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;
using PatchRequest = Raven.Server.Documents.Patch.PatchRequest;

namespace Raven.Server.Documents
{
    internal class CollectionRunner
    {
        protected readonly DocumentsOperationContext Context;
        protected readonly DocumentDatabase Database;

        public CollectionRunner(DocumentDatabase database, DocumentsOperationContext context)
        {
            Database = database;
            Context = context;
        }

        public virtual Task<IOperationResult> ExecuteDelete(string collectionName, CollectionOperationOptions options, Action<IOperationProgress> onProgress, OperationCancelToken token)
        {
            return ExecuteOperation(collectionName, options, Context, onProgress, (key, context) => Database.DocumentsStorage.Delete(context, key, null), token);
        }

        public Task<IOperationResult> ExecutePatch(string collectionName, CollectionOperationOptions options, PatchRequest patch, Action<IOperationProgress> onProgress, OperationCancelToken token)
        {
            return ExecuteOperation(collectionName, options, Context, onProgress, (key, context) => Database.Patcher.Apply(context, key, etag: null, patch: patch, patchIfMissing: null, skipPatchIfEtagMismatch: false, debugMode: false), token);
        }

        protected async Task<IOperationResult> ExecuteOperation(string collectionName, CollectionOperationOptions options, DocumentsOperationContext context,
             Action<DeterminateProgress> onProgress, Action<LazyStringValue, DocumentsOperationContext> action, OperationCancelToken token)
        {
            const int batchSize = 1024;
            var progress = new DeterminateProgress();
            var cancellationToken = token.Token;

            long lastEtag;
            long totalCount;
            using (context.OpenReadTransaction())
            {
                lastEtag = GetLastEtagForCollection(context, collectionName);
                totalCount = GetTotalCountForCollection(context, collectionName);
            }
            progress.Total = totalCount;

            // send initial progress with total count set, and 0 as processed count
            onProgress(progress);

            long startEtag = 0;
            using (var rateGate = options.MaxOpsPerSecond.HasValue
                    ? new RateGate(options.MaxOpsPerSecond.Value, TimeSpan.FromSeconds(1))
                    : null)
            {
                bool done = false;
                //The reason i do this nested loop is because i can't operate on a document while iterating the document tree.
                while (startEtag <= lastEtag)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var wait = false;

                    using (context.OpenReadTransaction())
                    {
                        var documents = GetDocuments(context, collectionName, startEtag, batchSize);

                        var ids = new List<LazyStringValue>();

                        foreach (var document in documents)
                        {
                            token.Delay();

                            cancellationToken.ThrowIfCancellationRequested();

                            if (document.Etag > lastEtag)// we don't want to go over the documents that we have patched
                            {
                                done = true;
                                break;
                            }

                            if (rateGate != null && rateGate.WaitToProceed(0) == false)
                            {
                                wait = true;
                                break;
                            }

                            startEtag = document.Etag + 1;

                            ids.Add(document.Key);

                            progress.Processed++;

                        }

                        await Database.TxMerger.Enqueue(new ExecuteOperationsOnCollection(ids, action));

                        onProgress(progress);

                        if (wait)
                            rateGate.WaitToProceed();
                        if (done || documents.Count == 0)
                            break;
                    }
                }
            }

            return new BulkOperationResult
            {
                Total = progress.Processed
            };
        }

        protected virtual List<Document> GetDocuments(DocumentsOperationContext context, string collectionName, long startEtag, int batchSize)
        {
            return Database.DocumentsStorage.GetDocumentsFrom(context, collectionName, startEtag, 0, batchSize).ToList();
        }

        protected virtual long GetTotalCountForCollection(DocumentsOperationContext context, string collectionName)
        {
            long totalCount;
            Database.DocumentsStorage.GetNumberOfDocumentsToProcess(context, collectionName, 0, out totalCount);
            return totalCount;
        }

        protected virtual long GetLastEtagForCollection(DocumentsOperationContext context, string collection)
        {
            return Database.DocumentsStorage.GetLastDocumentEtag(context, collection);
        }

        private class ExecuteOperationsOnCollection : TransactionOperationsMerger.MergedTransactionCommand
        {
            private readonly List<LazyStringValue> _documentIds;
            private readonly Action<LazyStringValue, DocumentsOperationContext> _action;

            public ExecuteOperationsOnCollection(List<LazyStringValue> documentIds, Action<LazyStringValue, DocumentsOperationContext> action)
            {
                _documentIds = documentIds;
                _action = action;
            }

            public override int Execute(DocumentsOperationContext context)
            {
                var count = 0;

                // TODO arek: need to handle concurrency 

                foreach (var id in _documentIds)
                {
                    _action(id, context);

                    count++;
                }

                return count;
            }
        }
    }
}
