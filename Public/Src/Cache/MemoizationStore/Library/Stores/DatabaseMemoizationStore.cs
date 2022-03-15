﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;
using BuildXL.Cache.ContentStore.Interfaces.Logging;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.UtilitiesCore;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Cache.MemoizationStore.Interfaces.Results;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using BuildXL.Cache.MemoizationStore.Interfaces.Stores;
using BuildXL.Cache.MemoizationStore.Sessions;
using BuildXL.Cache.MemoizationStore.Tracing;

#nullable enable

namespace BuildXL.Cache.MemoizationStore.Stores
{
    /// <summary>
    ///     An IMemoizationStore implementation using RocksDb.
    /// </summary>
    public class DatabaseMemoizationStore : StartupShutdownBase, IMemoizationStore
    {
        /// <summary>
        /// The database backing the store
        /// </summary>
        public virtual MemoizationDatabase Database { get; }

        /// <summary>
        ///     Store tracer.
        /// </summary>
        private readonly MemoizationStoreTracer _tracer;

        /// <inheritdoc />
        protected override Tracer Tracer => _tracer;

        /// <summary>
        /// The component name
        /// </summary>
        protected string Component => Tracer.Name;

        /// <summary>
        /// Indicates calls to <see cref="AddOrGetContentHashListAsync"/> should do an optimistic write (via CompareExchange) assuming
        /// that content is not present for initial attempt.
        /// </summary>
        public bool OptimizeWrites { get; set; } = false;

        /// <summary>
        ///     Initializes a new instance of the <see cref="DatabaseMemoizationStore"/> class.
        /// </summary>
        public DatabaseMemoizationStore(MemoizationDatabase database)
        {
            Contract.RequiresNotNull(database);

            _tracer = new MemoizationStoreTracer(database.Name);
            Database = database;
        }

        /// <inheritdoc />
        public CreateSessionResult<IReadOnlyMemoizationSession> CreateReadOnlySession(Context context, string name)
        {
            var session = new ReadOnlyDatabaseMemoizationSession(name, this);
            return new CreateSessionResult<IReadOnlyMemoizationSession>(session);
        }

        /// <inheritdoc />
        public CreateSessionResult<IMemoizationSession> CreateSession(Context context, string name)
        {
            var session = new DatabaseMemoizationSession(name, this);
            return new CreateSessionResult<IMemoizationSession>(session);
        }

        /// <inheritdoc />
        public CreateSessionResult<IMemoizationSession> CreateSession(Context context, string name, IContentSession contentSession)
        {
            var session = new DatabaseMemoizationSession(name, this, contentSession);
            return new CreateSessionResult<IMemoizationSession>(session);
        }

        /// <inheritdoc />
        public Task<GetStatsResult> GetStatsAsync(Context context)
        {
            return GetStatsCall<MemoizationStoreTracer>.RunAsync(_tracer, new OperationContext(context), () =>
            {
                var counters = new CounterSet();
                counters.Merge(_tracer.GetCounters(), $"{_tracer.Name}.");
                return Task.FromResult(new GetStatsResult(counters));
            });
        }

        /// <inheritdoc />
        protected override Task<BoolResult> StartupCoreAsync(OperationContext context)
        {
            return Database.StartupAsync(context);
        }

        /// <inheritdoc />
        protected override Task<BoolResult> ShutdownCoreAsync(OperationContext context)
        {
            return Database.ShutdownAsync(context);
        }

        /// <inheritdoc />
        public IAsyncEnumerable<StructResult<StrongFingerprint>> EnumerateStrongFingerprints(Context context)
        {
            var ctx = new OperationContext(context);
            return AsyncEnumerableExtensions.CreateSingleProducerTaskAsyncEnumerable(() => EnumerateStrongFingerprintsAsync(ctx));
        }

        private async Task<IEnumerable<StructResult<StrongFingerprint>>> EnumerateStrongFingerprintsAsync(OperationContext context)
        {
            var result = await Database.EnumerateStrongFingerprintsAsync(context);
            return result.Select(r => StructResult.FromResult(r));
        }

        internal Task<GetContentHashListResult> GetContentHashListAsync(Context context, StrongFingerprint strongFingerprint, CancellationToken token, bool preferShared = false)
        {
            var ctx = new OperationContext(context, token);
            return ctx.PerformOperationAsync(_tracer, async () =>
            {
                var result = await Database.GetContentHashListAsync(ctx, strongFingerprint, preferShared: preferShared);
                return result.Succeeded
                    ? new GetContentHashListResult(result.Value.contentHashListInfo, result.Source)
                    : new GetContentHashListResult(result, result.Source);
            },
            extraEndMessage: _ => $"StrongFingerprint=[{strongFingerprint}] PreferShared=[{preferShared}]",
            traceOperationStarted: false);
        }

        internal Task<AddOrGetContentHashListResult> AddOrGetContentHashListAsync(Context context, StrongFingerprint strongFingerprint, ContentHashListWithDeterminism contentHashListWithDeterminism, IContentSession contentSession, CancellationToken token)
        {
            var ctx = new OperationContext(context, token);

            return ctx.PerformOperationAsync(_tracer, async () =>
            {
                // We do multiple attempts here because we have a "CompareExchange" RocksDB in the heart
                // of this implementation, and this may fail if the database is heavily contended.
                // Unfortunately, there is not much we can do at the time of writing to avoid this
                // requirement.
                const int MaxAttempts = 5;
                for (int attempt = 0; attempt < MaxAttempts; attempt++)
                {
                    var contentHashList = contentHashListWithDeterminism.ContentHashList;
                    var determinism = contentHashListWithDeterminism.Determinism;

                    // Load old value. Notice that this get updates the time, regardless of whether we replace the value or not.
                    var (oldContentHashListInfo, replacementToken, _) = (!OptimizeWrites || attempt > 0)
                        ? await Database.GetContentHashListAsync(
                            ctx,
                            strongFingerprint,
                            // Prefer shared result because conflicts are resolved at shared level
                            preferShared: true).ThrowIfFailureAsync()
                        : new ContentHashListResult(default(ContentHashListWithDeterminism), string.Empty);

                    var oldContentHashList = oldContentHashListInfo.ContentHashList;
                    var oldDeterminism = oldContentHashListInfo.Determinism;

                    // Make sure we're not mixing SinglePhaseNonDeterminism records
                    if (!(oldContentHashList is null) && oldDeterminism.IsSinglePhaseNonDeterministic != determinism.IsSinglePhaseNonDeterministic)
                    {
                        return AddOrGetContentHashListResult.SinglePhaseMixingError;
                    }

                    if (oldContentHashList is null ||
                        oldDeterminism.ShouldBeReplacedWith(determinism) ||
                        !(await contentSession.EnsureContentIsAvailableAsync(ctx, Tracer.Name, oldContentHashList, ctx.Token).ConfigureAwait(false)))
                    {
                        // Replace if incoming has better determinism or some content for the existing
                        // entry is missing. The entry could have changed since we fetched the old value
                        // earlier, hence, we need to check it hasn't.
                        var exchanged = await Database.CompareExchange(
                           ctx,
                           strongFingerprint,
                           replacementToken,
                           oldContentHashListInfo,
                           contentHashListWithDeterminism).ThrowIfFailureAsync();
                        if (!exchanged)
                        {
                            // Our update lost, need to retry
                            continue;
                        }

                        // Returning null as the content hash list to indicate that the new value was accepted.
                        return new AddOrGetContentHashListResult(new ContentHashListWithDeterminism(null, determinism));
                    }

                    // If we didn't accept the new value because it is the same as before, just with a not
                    // necessarily better determinism, then let the user know.
                    if (oldContentHashList.Equals(contentHashList))
                    {
                        return new AddOrGetContentHashListResult(new ContentHashListWithDeterminism(null, oldDeterminism));
                    }

                    // If we didn't accept a deterministic tool's data, then we're in an inconsistent state
                    if (determinism.IsDeterministicTool)
                    {
                        return new AddOrGetContentHashListResult(
                            AddOrGetContentHashListResult.ResultCode.InvalidToolDeterminismError,
                            oldContentHashListInfo);
                    }

                    // If we did not accept the given value, return the value in the cache
                    return new AddOrGetContentHashListResult(oldContentHashListInfo);
                }

                return new AddOrGetContentHashListResult("Hit too many races attempting to add content hash list into the cache");
            },
            extraEndMessage: _ => $"StrongFingerprint=[{strongFingerprint}], Determinism=[{contentHashListWithDeterminism.Determinism}]",
            traceOperationStarted: false);
        }

        internal Task<Result<LevelSelectors>> GetLevelSelectorsAsync(Context context, Fingerprint weakFingerprint, CancellationToken cts, int level)
        {
            var ctx = new OperationContext(context);

            return ctx.PerformOperationAsync(_tracer, () => Database.GetLevelSelectorsAsync(ctx, weakFingerprint, level),
                extraEndMessage: _ => $"WeakFingerprint=[{weakFingerprint}], Level=[{level}]",
                traceOperationStarted: false);
        }
    }
}
