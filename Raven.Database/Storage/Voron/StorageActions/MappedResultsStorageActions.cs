﻿namespace Raven.Database.Storage.Voron.StorageActions
{
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.IO;
	using System.Linq;

	using Raven.Abstractions;
	using Raven.Abstractions.Data;
	using Raven.Abstractions.Extensions;
	using Raven.Abstractions.MEF;
	using Raven.Database.Impl;
	using Raven.Database.Indexing;
	using Raven.Database.Plugins;
	using Raven.Database.Storage.Voron.Impl;
	using Raven.Json.Linq;

	using global::Voron;
	using global::Voron.Impl;

	using Index = Raven.Database.Storage.Voron.Impl.Index;

	public class MappedResultsStorageActions : StorageActionsBase, IMappedResultsStorageAction
	{
		private readonly TableStorage tableStorage;

		private readonly IUuidGenerator generator;

		private readonly WriteBatch writeBatch;

		private readonly OrderedPartCollection<AbstractDocumentCodec> documentCodecs;

		public MappedResultsStorageActions(TableStorage tableStorage, IUuidGenerator generator, OrderedPartCollection<AbstractDocumentCodec> documentCodecs, SnapshotReader snapshot, WriteBatch writeBatch)
			: base(snapshot)
		{
			this.tableStorage = tableStorage;
			this.generator = generator;
			this.documentCodecs = documentCodecs;
			this.writeBatch = writeBatch;
		}

		public IEnumerable<ReduceKeyAndCount> GetKeysStats(string view, int start, int pageSize)
		{
			var reduceKeyCountsByView = tableStorage.ReduceKeyCounts.GetIndex(Tables.ReduceKeyCounts.Indices.ByView);
			using (var iterator = reduceKeyCountsByView.MultiRead(Snapshot, view))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys) || !iterator.Skip(start))
					yield break;

				var count = 0;
				do
				{
					ushort version;
					var value = LoadJson(tableStorage.ReduceKeyCounts, iterator.CurrentKey, out version);

					yield return new ReduceKeyAndCount
								 {
									 Count = value.Value<int>("mappedItemsCount"),
									 Key = value.Value<string>("reduceKey")
								 };

					count++;
				}
				while (iterator.MoveNext() && count < pageSize);
			}
		}

		public void PutMappedResult(string view, string docId, string reduceKey, RavenJObject data)
		{
			var mappedResultsByViewAndDocumentId = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByViewAndDocumentId);
			var mappedResultsByView = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByView);
			var mappedResultsByViewAndReduceKey = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByViewAndReduceKey);
			var mappedResultsByViewAndReduceKeyAndSourceBucket = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByViewAndReduceKeyAndSourceBucket);

			var mappedResultsData = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.Data);

			var ms = new MemoryStream();
			using (var stream = documentCodecs.Aggregate((Stream)ms, (ds, codec) => codec.Value.Encode(reduceKey, data, null, ds)))
				data.WriteTo(stream);

			var id = generator.CreateSequentialUuid(UuidType.MappedResults);
			var idAsString = id.ToString();
			var bucket = IndexingUtil.MapBucket(docId);

			tableStorage.MappedResults.Add(
				writeBatch,
				idAsString,
				new RavenJObject
				{
					{ "view", view },
					{ "reduceKey", reduceKey },
					{ "docId", docId },
					{ "etag", id.ToByteArray() },
					{ "bucket", bucket },
					{ "timestamp", SystemTime.UtcNow }
				}, 0);

			mappedResultsData.Add(writeBatch, idAsString, ms.ToArray(), 0);

			mappedResultsByViewAndDocumentId.MultiAdd(writeBatch, CreateKey(view, docId), idAsString);
			mappedResultsByView.MultiAdd(writeBatch, view, idAsString);
			mappedResultsByViewAndReduceKey.MultiAdd(writeBatch, CreateKey(view, reduceKey), idAsString);
			mappedResultsByViewAndReduceKeyAndSourceBucket.MultiAdd(writeBatch, CreateKey(view, reduceKey, bucket), idAsString);
		}

		public void IncrementReduceKeyCounter(string view, string reduceKey, int val)
		{
			var key = CreateKey(view, reduceKey);

			ushort version;
			var value = LoadJson(tableStorage.ReduceKeyCounts, key, out version);

			var newValue = val;
			if (value != null)
				newValue += value.Value<int>("mappedItemsCount");

			AddReduceKeyCount(key, view, reduceKey, newValue, version);
		}

		private void DecrementReduceKeyCounter(string view, string reduceKey, int val)
		{
			var key = CreateKey(view, reduceKey);

			ushort version;
			var value = LoadJson(tableStorage.ReduceKeyCounts, key, out version);

			var newValue = -val;
			if (value != null)
			{
				var currentValue = value.Value<int>("mappedItemsCount");
				if (currentValue == val)
				{
					DeleteReduceKeyCount(key, view, version);
					return;
				}

				newValue += currentValue;
			}

			AddReduceKeyCount(key, view, reduceKey, newValue, version);
		}

		public void DeleteMappedResultsForDocumentId(string documentId, string view, Dictionary<ReduceKeyAndBucket, int> removed)
		{
			var viewAndDocumentId = CreateKey(view, documentId);

			var mappedResultsByViewAndDocumentId = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByViewAndDocumentId);
			using (var iterator = mappedResultsByViewAndDocumentId.MultiRead(Snapshot, viewAndDocumentId))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys))
					return;

				do
				{
					var id = iterator.CurrentKey.Clone();

					ushort version;
					var value = LoadJson(tableStorage.MappedResults, id, out version);
					var reduceKey = value.Value<string>("reduceKey");
					var bucket = value.Value<int>("bucket");

					DeleteMappedResult(id, view, documentId, reduceKey, bucket.ToString(CultureInfo.InvariantCulture));

					var reduceKeyAndBucket = new ReduceKeyAndBucket(bucket, reduceKey);
					removed[reduceKeyAndBucket] = removed.GetOrDefault(reduceKeyAndBucket) + 1;
				}
				while (iterator.MoveNext());
			}
		}

		public void UpdateRemovedMapReduceStats(string view, Dictionary<ReduceKeyAndBucket, int> removed)
		{
			foreach (var keyAndBucket in removed)
			{
				DecrementReduceKeyCounter(view, keyAndBucket.Key.ReduceKey, keyAndBucket.Value);
			}
		}

		public void DeleteMappedResultsForView(string view)
		{
			var deletedReduceKeys = new List<string>();
			var mappedResultsByView = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByView);

			using (var iterator = mappedResultsByView.MultiRead(Snapshot, view))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys))
					return;

				do
				{
					var id = iterator.CurrentKey.Clone();

					ushort version;
					var value = LoadJson(tableStorage.MappedResults, id, out version);
					var reduceKey = value.Value<string>("reduceKey");
					var bucket = value.Value<string>("bucket");

					DeleteMappedResult(id, view, value.Value<string>("docId"), reduceKey, bucket);

					deletedReduceKeys.Add(reduceKey);
				}
				while (iterator.MoveNext());
			}

			foreach (var g in deletedReduceKeys.GroupBy(x => x, StringComparer.InvariantCultureIgnoreCase))
			{
				DecrementReduceKeyCounter(view, g.Key, g.Count());
			}
		}

		public IEnumerable<string> GetKeysForIndexForDebug(string view, int start, int take)
		{
			var mappedResultsByView = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByView);
			using (var iterator = mappedResultsByView.MultiRead(Snapshot, view))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys))
					return Enumerable.Empty<string>();

				var results = new List<string>();
				do
				{
					ushort version;
					var value = LoadJson(tableStorage.MappedResults, iterator.CurrentKey, out version);

					results.Add(value.Value<string>("reduceKey"));
				}
				while (iterator.MoveNext());

				return results
					.Distinct()
					.Skip(start)
					.Take(take);
			}
		}

		public IEnumerable<MappedResultInfo> GetMappedResultsForDebug(string view, string key, int start, int take)
		{
			var viewAndReduceKey = CreateKey(view, key);
			var mappedResultsByViewAndReduceKey = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByViewAndReduceKey);
			var mappedResultsData = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.Data);

			using (var iterator = mappedResultsByViewAndReduceKey.MultiRead(Snapshot, viewAndReduceKey))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys) || !iterator.Skip(start))
					yield break;

				var count = 0;
				do
				{
					ushort version;
					var value = LoadJson(tableStorage.MappedResults, iterator.CurrentKey, out version);
					var size = tableStorage.MappedResults.GetDataSize(Snapshot, iterator.CurrentKey);
					yield return new MappedResultInfo
					{
						ReduceKey = value.Value<string>("reduceKey"),
						Etag = Etag.Parse(value.Value<byte[]>("etag")),
						Timestamp = value.Value<DateTime>("timestamp"),
						Bucket = value.Value<int>("bucket"),
						Source = value.Value<string>("docId"),
						Size = size,
						Data = LoadMappedResult(key, value, mappedResultsData)
					};

					count++;
				}
				while (iterator.MoveNext() && count < take);
			}
		}

		public IEnumerable<MappedResultInfo> GetReducedResultsForDebug(string view, string key, int level, int start, int take)
		{
			var viewAndReduceKeyAndLevel = CreateKey(view, key, level);
			var reduceResultsByViewAndReduceKeyAndLevel =
				tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.ByViewAndReduceKeyAndLevel);
			var reduceResultsData = tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.Data);

			using (var iterator = reduceResultsByViewAndReduceKeyAndLevel.MultiRead(Snapshot, viewAndReduceKeyAndLevel))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys) || !iterator.Skip(start))
					yield break;

				var count = 0;
				do
				{
					ushort version;
					var value = LoadJson(tableStorage.ReduceResults, iterator.CurrentKey, out version);
					var size = tableStorage.ReduceResults.GetDataSize(Snapshot, iterator.CurrentKey);

					yield return
						new MappedResultInfo
						{
							ReduceKey = value.Value<string>("reduceKey"),
							Etag = Etag.Parse(value.Value<byte[]>("etag")),
							Timestamp = value.Value<DateTime>("timestamp"),
							Bucket = value.Value<int>("bucket"),
							Source = value.Value<string>("sourceBucket"),
							Size = size,
							Data = LoadMappedResult(iterator.CurrentKey, value, reduceResultsData)
						};

					count++;
				}
				while (iterator.MoveNext() && count < take);
			}
		}

		public IEnumerable<ScheduledReductionDebugInfo> GetScheduledReductionForDebug(string view, int start, int take)
		{
			var scheduledReductionsByView = tableStorage.ScheduledReductions.GetIndex(Tables.ScheduledReductions.Indices.ByView);
			using (var iterator = scheduledReductionsByView.MultiRead(Snapshot, view))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys) || !iterator.Skip(start))
					yield break;

				var count = 0;
				do
				{
					ushort version;
					var value = LoadJson(tableStorage.ScheduledReductions, iterator.CurrentKey, out version);

					yield return new ScheduledReductionDebugInfo
					{
						Key = value.Value<string>("reduceKey"),
						Bucket = value.Value<int>("bucket"),
						Etag = new Guid(value.Value<byte[]>("etag")),
						Level = value.Value<int>("level"),
						Timestamp = value.Value<DateTime>("timestamp"),
					};

					count++;
				}
				while (iterator.MoveNext() && count < take);
			}
		}

		public void ScheduleReductions(string view, int level, ReduceKeyAndBucket reduceKeysAndBuckets)
		{
			var scheduledReductionsByView = tableStorage.ScheduledReductions.GetIndex(Tables.ScheduledReductions.Indices.ByView);
			var scheduledReductionsByViewAndLevelAndReduceKey = tableStorage.ScheduledReductions.GetIndex(Tables.ScheduledReductions.Indices.ByViewAndLevelAndReduceKey);
			var scheduledReductionsByViewAndLevel = tableStorage.ScheduledReductions.GetIndex(Tables.ScheduledReductions.Indices.ByViewAndLevel);

			var id = generator.CreateSequentialUuid(UuidType.ScheduledReductions);
			var idAsString = id.ToString();

			tableStorage.ScheduledReductions.Add(writeBatch, idAsString, new RavenJObject
			{
				{"view", view},
				{"reduceKey", reduceKeysAndBuckets.ReduceKey},
				{"bucket", reduceKeysAndBuckets.Bucket},
				{"level", level},
				{"etag", id.ToByteArray()},
				{"timestamp", SystemTime.UtcNow}
			});

			scheduledReductionsByView.MultiAdd(writeBatch, view, idAsString);
			scheduledReductionsByViewAndLevelAndReduceKey.MultiAdd(writeBatch, CreateKey(view, level, reduceKeysAndBuckets.ReduceKey), idAsString);
			scheduledReductionsByViewAndLevel.MultiAdd(writeBatch, CreateKey(view, level), idAsString);
		}

		public IEnumerable<MappedResultInfo> GetItemsToReduce(GetItemsToReduceParams getItemsToReduceParams)
		{
			var scheduledReductionsByViewAndLevelAndReduceKey = tableStorage.ScheduledReductions.GetIndex(Tables.ScheduledReductions.Indices.ByViewAndLevelAndReduceKey);

			var seenLocally = new HashSet<Tuple<string, int>>();
			foreach (var reduceKey in getItemsToReduceParams.ReduceKeys.ToArray())
			{
				var viewAndLevelAndReduceKey = CreateKey(getItemsToReduceParams.Index, getItemsToReduceParams.Level, reduceKey);
				using (var iterator = scheduledReductionsByViewAndLevelAndReduceKey.MultiRead(Snapshot, viewAndLevelAndReduceKey))
				{
					if (!iterator.Seek(Slice.BeforeAllKeys))
						continue;

					do
					{
						if (getItemsToReduceParams.Take <= 0)
							break;

						ushort version;
						var value = LoadJson(tableStorage.ScheduledReductions, iterator.CurrentKey, out version);

						var indexFromDb = value.Value<string>("view");
						var levelFromDb = value.Value<int>("level");
						var reduceKeyFromDb = value.Value<string>("reduceKey");

						if (string.Equals(indexFromDb, getItemsToReduceParams.Index, StringComparison.OrdinalIgnoreCase) == false ||
						levelFromDb != getItemsToReduceParams.Level)
							break;

						if (string.Equals(reduceKeyFromDb, reduceKey, StringComparison.Ordinal) == false)
							break;

						var bucket = value.Value<int>("bucket");
						var rowKey = Tuple.Create(reduceKeyFromDb, bucket);
						var thisIsNewScheduledReductionRow = getItemsToReduceParams.ItemsToDelete.Contains(value, RavenJTokenEqualityComparer.Default) == false;
						var neverSeenThisKeyAndBucket = getItemsToReduceParams.ItemsAlreadySeen.Add(rowKey);
						if (thisIsNewScheduledReductionRow || neverSeenThisKeyAndBucket)
						{
							if (seenLocally.Add(rowKey))
							{
								foreach (var mappedResultInfo in GetResultsForBucket(getItemsToReduceParams.Index, getItemsToReduceParams.Level, reduceKeyFromDb, bucket, getItemsToReduceParams.LoadData))
								{
									getItemsToReduceParams.Take--;
									yield return mappedResultInfo;
								}
							}
						}

						if (thisIsNewScheduledReductionRow)
							getItemsToReduceParams.ItemsToDelete.Add(value);

						if (getItemsToReduceParams.Take <= 0)
							yield break;
					}
					while (iterator.MoveNext());
				}

				getItemsToReduceParams.ReduceKeys.Remove(reduceKey);

				if (getItemsToReduceParams.Take <= 0)
					break;
			}
		}

		private IEnumerable<MappedResultInfo> GetResultsForBucket(string view, int level, string reduceKey, int bucket, bool loadData)
		{
			switch (level)
			{
				case 0:
					return GetMappedResultsForBucket(view, reduceKey, bucket, loadData);
				case 1:
				case 2:
					return GetReducedResultsForBucket(view, reduceKey, level, bucket, loadData);
				default:
					throw new ArgumentException("Invalid level: " + level);
			}
		}

		private IEnumerable<MappedResultInfo> GetReducedResultsForBucket(string view, string reduceKey, int level, int bucket, bool loadData)
		{
			var viewAndReduceKeyAndLevelAndSourceBucket = CreateKey(view, reduceKey, level, bucket);

			var reduceResultsByViewAndReduceKeyAndLevelAndSourceBucket = tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.ByViewAndReduceKeyAndLevelAndSourceBucket);
			var reduceResultsData = tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.Data);
			using (var iterator = reduceResultsByViewAndReduceKeyAndLevelAndSourceBucket.MultiRead(Snapshot, viewAndReduceKeyAndLevelAndSourceBucket))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys))
				{
					yield return new MappedResultInfo
								 {
									 Bucket = bucket,
									 ReduceKey = reduceKey
								 };

					yield break;
				}

				do
				{
					ushort version;
					var value = LoadJson(tableStorage.ReduceResults, iterator.CurrentKey, out version);
					var size = tableStorage.ReduceResults.GetDataSize(Snapshot, iterator.CurrentKey);

					yield return new MappedResultInfo
					{
						ReduceKey = value.Value<string>("reduceKey"),
						Etag = Etag.Parse(value.Value<byte[]>("etag")),
						Timestamp = value.Value<DateTime>("timestamp"),
						Bucket = value.Value<int>("bucket"),
						Source = null,
						Size = size,
						Data = loadData ? LoadMappedResult(iterator.CurrentKey, value, reduceResultsData) : null
					};
				}
				while (iterator.MoveNext());
			}
		}

		private IEnumerable<MappedResultInfo> GetMappedResultsForBucket(string view, string reduceKey, int bucket, bool loadData)
		{
			var viewAndReduceKeyAndSourceBucket = CreateKey(view, reduceKey, bucket);

			var mappedResultsByViewAndReduceKeyAndSourceBucket = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByViewAndReduceKeyAndSourceBucket);
			var mappedResultsData = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.Data);

			using (var iterator = mappedResultsByViewAndReduceKeyAndSourceBucket.MultiRead(Snapshot, viewAndReduceKeyAndSourceBucket))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys))
				{
					yield return new MappedResultInfo
					{
						Bucket = bucket,
						ReduceKey = reduceKey
					};

					yield break;
				}

				do
				{
					ushort version;
					var value = LoadJson(tableStorage.MappedResults, iterator.CurrentKey, out version);
					var size = tableStorage.MappedResults.GetDataSize(Snapshot, iterator.CurrentKey);

					yield return new MappedResultInfo
					{
						ReduceKey = value.Value<string>("reduceKey"),
						Etag = Etag.Parse(value.Value<byte[]>("etag")),
						Timestamp = value.Value<DateTime>("timestamp"),
						Bucket = value.Value<int>("bucket"),
						Source = null,
						Size = size,
						Data = loadData ? LoadMappedResult(iterator.CurrentKey, value, mappedResultsData) : null
					};
				}
				while (iterator.MoveNext());
			}
		}

		public ScheduledReductionInfo DeleteScheduledReduction(List<object> itemsToDelete)
		{
			if (itemsToDelete == null)
				return null;

			var result = new ScheduledReductionInfo();
			var hasResult = false;
			var currentEtag = Etag.Empty;
			foreach (RavenJToken token in itemsToDelete)
			{
				var etag = Etag.Parse(token.Value<byte[]>("etag"));
				var etagAsString = etag.ToString();

				ushort version;
				var value = LoadJson(tableStorage.ScheduledReductions, etagAsString, out version);
				if (value == null)
					continue;

				if (etag.CompareTo(currentEtag) > 0)
				{
					hasResult = true;
					result.Etag = etag;
					result.Timestamp = value.Value<DateTime>("timestamp");
				}

				var view = value.Value<string>("view");
				var level = value.Value<int>("level");
				var reduceKey = value.Value<string>("reduceKey");

				DeleteScheduledReduction(etagAsString, view, level, reduceKey);
			}

			return hasResult ? result : null;
		}

		public void DeleteScheduledReduction(string view, int level, string reduceKey)
		{
			var scheduledReductionsByViewAndLevelAndReduceKey = tableStorage.ScheduledReductions.GetIndex(Tables.ScheduledReductions.Indices.ByViewAndLevelAndReduceKey);
			using (var iterator = scheduledReductionsByViewAndLevelAndReduceKey.MultiRead(Snapshot, CreateKey(view, level, reduceKey)))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys))
					return;

				do
				{
					var id = iterator.CurrentKey;
					DeleteScheduledReduction(id, view, level, reduceKey);
				}
				while (iterator.MoveNext());
			}
		}

		public void PutReducedResult(string view, string reduceKey, int level, int sourceBucket, int bucket, RavenJObject data)
		{
			var reduceResultsByViewAndReduceKeyAndLevelAndSourceBucket =
				tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.ByViewAndReduceKeyAndLevelAndSourceBucket);
			var reduceResultsByViewAndReduceKeyAndLevel =
				tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.ByViewAndReduceKeyAndLevel);
			var reduceResultsData = tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.Data);

			var ms = new MemoryStream();
			using (var stream = documentCodecs.Aggregate((Stream)ms, (ds, codec) => codec.Value.Encode(reduceKey, data, null, ds)))
				data.WriteTo(stream);

			var id = generator.CreateSequentialUuid(UuidType.MappedResults);
			var idAsString = id.ToString();

			tableStorage.ReduceResults.Add(
				writeBatch,
				idAsString,
				new RavenJObject
				{
					{ "view", view },
					{ "etag", id.ToByteArray() },
					{ "reduceKey", reduceKey },
					{ "level", level },
					{ "sourceBucket", sourceBucket },
					{ "bucket", bucket },
					{ "timestamp", SystemTime.UtcNow }
				},
				0);

			reduceResultsData.Add(writeBatch, idAsString, ms.ToArray(), 0);

			var viewAndReduceKeyAndLevelAndSourceBucket = CreateKey(view, reduceKey, level, sourceBucket);
			var viewAndReduceKeyAndLevel = CreateKey(view, reduceKey, level);

			reduceResultsByViewAndReduceKeyAndLevelAndSourceBucket.MultiAdd(writeBatch, viewAndReduceKeyAndLevelAndSourceBucket, idAsString);
			reduceResultsByViewAndReduceKeyAndLevel.MultiAdd(writeBatch, viewAndReduceKeyAndLevel, idAsString);
		}

		public void RemoveReduceResults(string view, int level, string reduceKey, int sourceBucket)
		{
			var viewAndReduceKeyAndLevelAndSourceBucket = CreateKey(view, reduceKey, level, sourceBucket);
			var reduceResultsByViewAndReduceKeyAndLevelAndSourceBucket =
				tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.ByViewAndReduceKeyAndLevelAndSourceBucket);

			using (var iterator = reduceResultsByViewAndReduceKeyAndLevelAndSourceBucket.MultiRead(Snapshot, viewAndReduceKeyAndLevelAndSourceBucket))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys))
					return;

				do
				{
					RemoveReduceResult(iterator.CurrentKey, view, level, reduceKey, sourceBucket);
				}
				while (iterator.MoveNext());
			}
		}

		public IEnumerable<ReduceTypePerKey> GetReduceTypesPerKeys(string view, int take, int limitOfItemsToReduceInSingleStep)
		{
			if (take <= 0)
				take = 1;

			var allKeysToReduce = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			var viewAndLevel = CreateKey(view, 0);
			var scheduledReductionsByViewAndLevel = tableStorage.ScheduledReductions.GetIndex(Tables.ScheduledReductions.Indices.ByViewAndLevel);
			using (var iterator = scheduledReductionsByViewAndLevel.MultiRead(Snapshot, viewAndLevel))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys))
					return Enumerable.Empty<ReduceTypePerKey>();

				do
				{
					ushort version;
					var value = LoadJson(tableStorage.ScheduledReductions, iterator.CurrentKey, out version);

					allKeysToReduce.Add(value.Value<string>("reduceKey"));
				}
				while (iterator.MoveNext());
			}

			var reduceTypesPerKeys = allKeysToReduce.ToDictionary(x => x, x => ReduceType.SingleStep);

			foreach (var reduceKey in allKeysToReduce)
			{
				var count = GetNumberOfMappedItemsPerReduceKey(view, reduceKey);
				if (count >= limitOfItemsToReduceInSingleStep)
				{
					reduceTypesPerKeys[reduceKey] = ReduceType.MultiStep;
				}
			}

			return reduceTypesPerKeys
				.Select(x => new ReduceTypePerKey(x.Key, x.Value))
				.Take(take);
		}

		private int GetNumberOfMappedItemsPerReduceKey(string view, string reduceKey)
		{
			var key = CreateKey(view, reduceKey);

			ushort version;
			var value = LoadJson(tableStorage.ReduceKeyCounts, key, out version);
			if (value == null)
				return 0;

			return value.Value<int>("mappedItemsCount");
		}

		public void UpdatePerformedReduceType(string view, string reduceKey, ReduceType reduceType)
		{
			var key = CreateKey(view, reduceKey);
			var version = tableStorage.ReduceKeyTypes.ReadVersion(Snapshot, key);

			AddReduceKeyType(key, view, reduceKey, reduceType, version);
		}

		private void DeleteReduceKeyCount(string key, string view, ushort? expectedVersion)
		{
			var reduceKeyCountsByView = tableStorage.ReduceKeyCounts.GetIndex(Tables.ReduceKeyCounts.Indices.ByView);

			tableStorage.ReduceKeyCounts.Delete(writeBatch, key, expectedVersion);
			reduceKeyCountsByView.MultiDelete(writeBatch, view, key);
		}

		private void AddReduceKeyCount(string key, string view, string reduceKey, int count, ushort? expectedVersion)
		{
			var reduceKeyCountsByView = tableStorage.ReduceKeyCounts.GetIndex(Tables.ReduceKeyCounts.Indices.ByView);

			tableStorage.ReduceKeyCounts.Add(
						writeBatch,
						key,
						new RavenJObject
						{
							{ "view", view },
							{ "reduceKey", reduceKey },
							{ "mappedItemsCount", count }
						}, expectedVersion);

			reduceKeyCountsByView.MultiAdd(writeBatch, view, key);
		}

		private void AddReduceKeyType(string key, string view, string reduceKey, ReduceType status, ushort? expectedVersion)
		{
			var reduceKeyTypesByView = tableStorage.ReduceKeyTypes.GetIndex(Tables.ReduceKeyTypes.Indices.ByView);

			tableStorage.ReduceKeyTypes.Add(
						writeBatch,
						key,
						new RavenJObject
						{
							{ "view", view },
							{ "reduceKey", reduceKey },
							{ "reduceType", (int)status }
						}, expectedVersion);

			reduceKeyTypesByView.MultiAdd(writeBatch, view, key);
		}

		public ReduceType GetLastPerformedReduceType(string view, string reduceKey)
		{
			var key = CreateKey(view, reduceKey);

			ushort version;
			var value = LoadJson(tableStorage.ReduceKeyTypes, key, out version);
			if (value == null)
				return ReduceType.None;

			return (ReduceType)value.Value<int>("reduceType");
		}

		public IEnumerable<int> GetMappedBuckets(string view, string reduceKey)
		{
			var viewAndReduceKey = CreateKey(view, reduceKey);
			var mappedResultsByViewAndReduceKey = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByViewAndReduceKey);

			using (var iterator = mappedResultsByViewAndReduceKey.MultiRead(Snapshot, viewAndReduceKey))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys))
					yield break;

				do
				{
					ushort version;
					var value = LoadJson(tableStorage.MappedResults, iterator.CurrentKey, out version);

					yield return value.Value<int>("bucket");
				}
				while (iterator.MoveNext());
			}
		}

		public IEnumerable<MappedResultInfo> GetMappedResults(string view, IEnumerable<string> keysToReduce, bool loadData)
		{
			var mappedResultsByViewAndReduceKey = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByViewAndReduceKey);
			var mappedResultsData = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.Data);

			foreach (var reduceKey in keysToReduce)
			{
				var viewAndReduceKey = CreateKey(view, reduceKey);
				using (var iterator = mappedResultsByViewAndReduceKey.MultiRead(Snapshot, viewAndReduceKey))
				{
					if (!iterator.Seek(Slice.BeforeAllKeys))
						continue;

					do
					{
						ushort version;
						var value = LoadJson(tableStorage.MappedResults, iterator.CurrentKey, out version);
						var size = tableStorage.MappedResults.GetDataSize(Snapshot, iterator.CurrentKey);

						yield return new MappedResultInfo
						{
							Bucket = value.Value<int>("bucket"),
							ReduceKey = value.Value<string>("reduceKey"),
							Etag = Etag.Parse(value.Value<byte[]>("etag")),
							Timestamp = value.Value<DateTime>("timestamp"),
							Data = loadData ? LoadMappedResult(iterator.CurrentKey, value, mappedResultsData) : null,
							Size = size
						};
					}
					while (iterator.MoveNext());
				}
			}
		}

		private RavenJObject LoadMappedResult(Slice key, RavenJObject value, Index dataIndex)
		{
			var reduceKey = value.Value<string>("reduceKey");

			using (var read = dataIndex.Read(Snapshot, key, writeBatch))
			{
				if (read == null)
					return null;

				using (var stream = documentCodecs.Aggregate(read.Stream, (ds, codec) => codec.Decode(reduceKey, null, ds)))
					return stream.ToJObject();
			}
		}

		public IEnumerable<ReduceTypePerKey> GetReduceKeysAndTypes(string view, int start, int take)
		{
			var reduceKeyTypesByView = tableStorage.ReduceKeyTypes.GetIndex(Tables.ReduceKeyTypes.Indices.ByView);
			using (var iterator = reduceKeyTypesByView.MultiRead(Snapshot, view))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys) || !iterator.Skip(start))
					yield break;

				var count = 0;
				do
				{
					ushort version;
					var value = LoadJson(tableStorage.ReduceKeyTypes, iterator.CurrentKey, out version);

					yield return new ReduceTypePerKey(value.Value<string>("reduceKey"), (ReduceType)value.Value<int>("reduceType"));

					count++;
				}
				while (iterator.MoveNext() && count < take);
			}
		}

		private void DeleteScheduledReduction(Slice id, string view, int level, string reduceKey)
		{
			var scheduledReductionsByView = tableStorage.ScheduledReductions.GetIndex(Tables.ScheduledReductions.Indices.ByView);
			var scheduledReductionsByViewAndLevelAndReduceKey = tableStorage.ScheduledReductions.GetIndex(Tables.ScheduledReductions.Indices.ByViewAndLevelAndReduceKey);
			var scheduledReductionsByViewAndLevel = tableStorage.ScheduledReductions.GetIndex(Tables.ScheduledReductions.Indices.ByViewAndLevel);

			tableStorage.ScheduledReductions.Delete(writeBatch, id);
			scheduledReductionsByView.MultiDelete(writeBatch, view, id);
			scheduledReductionsByViewAndLevelAndReduceKey.MultiDelete(writeBatch, CreateKey(view, level, reduceKey), id);
			scheduledReductionsByViewAndLevel.MultiDelete(writeBatch, CreateKey(view, level), id);
		}

		private void DeleteMappedResult(Slice id, string view, string documentId, string reduceKey, string bucket)
		{
			var viewAndDocumentId = CreateKey(view, documentId);
			var viewAndReduceKey = CreateKey(view, reduceKey);
			var viewAndReduceKeyAndSourceBucket = CreateKey(view, reduceKey, bucket);
			var mappedResultsByViewAndDocumentId = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByViewAndDocumentId);
			var mappedResultsByView = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByView);
			var mappedResultsByViewAndReduceKey = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByViewAndReduceKey);
			var mappedResultsByViewAndReduceKeyAndSourceBucket = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByViewAndReduceKeyAndSourceBucket);
			var mappedResultsData = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.Data);

			tableStorage.MappedResults.Delete(writeBatch, id);
			mappedResultsByViewAndDocumentId.MultiDelete(writeBatch, viewAndDocumentId, id);
			mappedResultsByView.MultiDelete(writeBatch, view, id);
			mappedResultsByViewAndReduceKey.MultiDelete(writeBatch, viewAndReduceKey, id);
			mappedResultsByViewAndReduceKeyAndSourceBucket.MultiDelete(writeBatch, viewAndReduceKeyAndSourceBucket, id);
			mappedResultsData.Delete(writeBatch, id);
		}

		private void RemoveReduceResult(Slice id, string view, int level, string reduceKey, int sourceBucket)
		{
			var viewAndReduceKeyAndLevelAndSourceBucket = CreateKey(view, reduceKey, level, sourceBucket);
			var viewAndReduceKeyAndLevel = CreateKey(view, reduceKey, level);
			var reduceResultsByViewAndReduceKeyAndLevelAndSourceBucket =
				tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.ByViewAndReduceKeyAndLevelAndSourceBucket);
			var reduceResultsByViewAndReduceKeyAndLevel =
				tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.ByViewAndReduceKeyAndLevel);
			var reduceResultsData = tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.Data);

			tableStorage.ReduceResults.Delete(writeBatch, id);
			reduceResultsByViewAndReduceKeyAndLevelAndSourceBucket.MultiDelete(writeBatch, viewAndReduceKeyAndLevelAndSourceBucket, id);
			reduceResultsByViewAndReduceKeyAndLevel.MultiDelete(writeBatch, viewAndReduceKeyAndLevel, id);
			reduceResultsData.Delete(writeBatch, id);
		}
	}
}