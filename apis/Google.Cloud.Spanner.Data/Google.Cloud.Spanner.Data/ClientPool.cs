﻿// Copyright 2017 Google Inc. All Rights Reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Cloud.Spanner.V1;
using Google.Cloud.Spanner.V1.Internal;
using Google.Cloud.Spanner.V1.Internal.Logging;
using Grpc.Core;

namespace Google.Cloud.Spanner.Data
{

    internal class ClientPool
    {
        public static ClientPool Default { get; } = new ClientPool();

        private readonly ConcurrentDictionary<ClientCredentialKey, CredentialClientPool> _clientPoolByCredential =
            new ConcurrentDictionary<ClientCredentialKey, CredentialClientPool>();
        private readonly ISpannerClientFactory _clientFactory;

        internal ClientPool(ISpannerClientFactory clientFactory = null)
        {
            _clientFactory = clientFactory ?? SpannerClientFactory.Default;
        }

        /// <summary>
        /// This property is intended for internal use only.
        /// </summary>
        private Logger Logger { get; } = Logger.DefaultLogger;

        internal async Task<SpannerClient> AcquireClientAsync(SpannerConnectionStringBuilder connectionStringBuilder)
        {
            var key = new ClientCredentialKey(connectionStringBuilder);
            var poolEntry = _clientPoolByCredential.GetOrAdd(key, k => new CredentialClientPool(k));
            var result = await poolEntry.AcquireClientAsync(_clientFactory, Logger).ConfigureAwait(false);
            Logger.LogPerformanceCounter("SpannerClient.TotalCount", () => _clientPoolByCredential.Count);
            return result;
        }

        //ReSharper disable once UnusedMember.Global
        //Returns the total of all reference counts.
        //For test purposes only.
        // poolContents will be filled with the current contents of the pool and may not be null.
        internal int GetPoolInfo(StringBuilder poolContents)
        {
            GaxPreconditions.CheckNotNull(poolContents, nameof(poolContents));
            int referenceCountTotal = 0;
            poolContents.AppendLine("ClientPool.Contents:");
            int i = 0;
            foreach (var kvp in _clientPoolByCredential)
            {
                poolContents.AppendLine($"s_clientPoolByCredential({i}) Key:${kvp.Key}");
                referenceCountTotal += kvp.Value.DumpCredentialPoolContents(poolContents);
                i++;
            }
            return referenceCountTotal;
        }

        /// <summary>
        /// For test purposes only. Removes all cached clients.
        /// </summary>
        internal void Reset()
        {
            _clientPoolByCredential.Clear();
        }

        /// <summary>
        /// Returns a diagnostic summary of the state of the pool.
        /// </summary>
        internal string ToDiagnosticSummary()
        {
            var pools = _clientPoolByCredential.Values.ToList();
            string perPool = string.Join(", ", pools.Select(p => p.ToDiagnosticSummary()));
            return $"Pools count: {pools.Count}; Size/RefCount per pool: {perPool}";
        }

        internal void ReleaseClient(SpannerClient spannerClient, SpannerConnectionStringBuilder connectionStringBuilder)
        {
            if (spannerClient != null)
            {
                var key = new ClientCredentialKey(connectionStringBuilder);
                CredentialClientPool poolEntry;
                if (_clientPoolByCredential.TryGetValue(key, out poolEntry))
                {
                    poolEntry.ReleaseClient(spannerClient);
                }
                else
                {
                    Logger.Error(() => "An attempt was made to release an unrecognized spanner client to the pool.");
                }
            }
        }

        private struct ClientCredentialKey : IEquatable<ClientCredentialKey>
        {
            public ChannelCredentials Credentials { get; }
            public ServiceEndpoint Endpoint { get; }
            public IDictionary AdditionalOptions { get; }

            public ClientCredentialKey(SpannerConnectionStringBuilder connectionStringBuilder)
            {
                Credentials = connectionStringBuilder.GetCredentials();
                Endpoint = connectionStringBuilder.EndPoint ?? SpannerClient.DefaultEndpoint;
                AdditionalOptions = connectionStringBuilder;
            }

            public bool Equals(ClientCredentialKey other) =>
                Equals(Credentials, other.Credentials)
                && Equals(Endpoint, other.Endpoint)
                && TypeUtil.DictionaryEquals(AdditionalOptions, other.AdditionalOptions);

            public override bool Equals(object obj) => obj is ClientCredentialKey other && Equals(other);

            /// <inheritdoc />
            public override int GetHashCode()
            {
                unchecked
                {
                    return ((Credentials?.GetHashCode() ?? 0) * 397) ^
                        (Endpoint?.GetHashCode() ?? 0) ^
                        (AdditionalOptions?.Count.GetHashCode() ?? 0);
                }
            }
            /// <inheritdoc />
            public override string ToString() =>
                $"Credential: {Credentials?.ToString() ?? "null"}; Endpoint: {Endpoint}; AdditionalOptions: {TypeUtil.DictionaryToString(AdditionalOptions)}";
        }

        private class CredentialClientPool
        {
            private readonly ClientCredentialKey _key;
            private readonly PriorityList<SpannerClientCreator> _clientPriorityList =
                new PriorityList<SpannerClientCreator>();
            private readonly object _sync = new object();


            public CredentialClientPool(ClientCredentialKey key) => _key = key;

            //Returns the total sum of reference counts.
            internal int DumpCredentialPoolContents(StringBuilder stringBuilder)
            {
                lock (_sync)
                {
                    int referenceCountTotal = 0;
                    int i = 0;
                    foreach (var item in _clientPriorityList.GetSnapshot())
                    {
                        stringBuilder.AppendLine($"  {i}:{item}");
                        referenceCountTotal += item.RefCount;
                        i++;
                    }
                    return referenceCountTotal;
                }
            }

            /// <summary>
            /// Returns a diagnostic summary of the state of this pool - the number
            /// of clients in it, and the total reference count.
            /// </summary>
            internal string ToDiagnosticSummary()
            {
                var snapshot = _clientPriorityList.GetSnapshot();
                return $"({snapshot.Count()} / {snapshot.Sum(p => p.RefCount)})";
            }

            public Task<SpannerClient> AcquireClientAsync(ISpannerClientFactory clientFactory, Logger logger)
            {
                Task<SpannerClient> result;

                lock (_sync)
                {
                    var snapshotMaximumChannels = SpannerOptions.Instance.MaximumGrpcChannels;
                    //first ensure that the pool is of the correct size.
                    while (_clientPriorityList.Count > snapshotMaximumChannels)
                    {
                        _clientPriorityList.RemoveLast();
                    }
                    while (_clientPriorityList.Count < snapshotMaximumChannels)
                    {
                        var newEntry = new SpannerClientCreator(_key);
                        _clientPriorityList.Add(newEntry);
                    }

                    //now grab the first item in the sorted list, increment refcnt, re-sort and return.
                    // The re-sorting will happen as a consequence of AcquireClientAsync changing its
                    // state and firing an event the priority list listens to.
                    result = _clientPriorityList.GetTop().AcquireClientAsync(clientFactory, logger);
                }
                return result;
            }

            public void ReleaseClient(SpannerClient client)
            {
                lock (_sync)
                {
                    //find the entry and release refcnt and re-sort
                    SpannerClientCreator match;
                    if (_clientPriorityList.TryFindLinear(x => x.MatchesClient(client), out match)) {
                        match.Release();
                    }
                }
            }
        }

        private class SpannerClientCreator : IPriorityListItem<SpannerClientCreator>
        {
            private Lazy<Task<SpannerClient>> _creationTask;
            private int _refCount = 0;
            private readonly ClientCredentialKey _parentKey;

            public SpannerClientCreator(ClientCredentialKey parentKey)
            {
                _parentKey = parentKey;
            }

            internal int RefCount => _refCount;

            public bool MatchesClient(SpannerClient client) => _creationTask != null
                && _creationTask.IsValueCreated
                && !_creationTask.Value.IsFaulted
                && ReferenceEquals(_creationTask.Value.ResultWithUnwrappedExceptions(), client);

            public async Task<SpannerClient> AcquireClientAsync(ISpannerClientFactory clientFactory, Logger logger)
            {
                if (_creationTask == null || _creationTask.Value.IsFaulted)
                {
                    //retry an already failed task.
                    _creationTask = new Lazy<Task<SpannerClient>>(
                        () => clientFactory.CreateClientAsync(_parentKey.Endpoint, _parentKey.Credentials,
                        _parentKey.AdditionalOptions, logger));
                }

                var spannerClient = await _creationTask.Value.ConfigureAwait(false);
                Interlocked.Increment(ref _refCount);
                OnPriorityChanged();

                return spannerClient;
            }

            public void Release()
            {
                Interlocked.Decrement(ref _refCount);
                OnPriorityChanged();
            }

            /// <inheritdoc />
            public int CompareTo(SpannerClientCreator other)
            {
                if (ReferenceEquals(this, other))
                {
                    return 0;
                }
                if (ReferenceEquals(null, other))
                {
                    return 1;
                }
                int refCountComparison = RefCount.CompareTo(other.RefCount);
                if (refCountComparison != 0)
                {
                    return refCountComparison;
                }
                // This has a chance of returning 0 even if the object is not the same instance.
                // That is fine and is handled by the PriorityHeap.

                // Note: SpannerClients if created sequentially should only use a single channel.
                // This is to faciliate good session pool hits (which is per channel).  If we always
                // round robin'd clients, then we would get hard cache misses until we populated all
                // of the caches per channel.
                // We accomplish this by having a specified sort order based on hash if there is
                // a tie based on refcnt.
                return GetHashCode().CompareTo(other.GetHashCode());
            }

            /// <inheritdoc />
            public override string ToString()
            {
                return $"RefCount:{RefCount}. ParentHashCode{GetHashCode()}";
            }
            private void OnPriorityChanged()
            {
                PriorityChanged?.Invoke(this, EventArgs.Empty);
            }

            /// <inheritdoc />
            public event EventHandler<EventArgs> PriorityChanged;
        }
    }
}
