﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;

using DoubleDict = System.Collections.Generic.IReadOnlyDictionary<string, double>;
using ObjDict = System.Collections.Generic.IReadOnlyDictionary<string, object>;

namespace iChen.Persistence.Cloud
{
	/// <remarks>This type is thread-safe.</remarks>
	public class RedisCache : ISharedCache
	{
		private const int KeySpace = 1;   // 0 is reserved for Chen Hsong internal
		private const string DefaultObjectType = nameof(iChen);
		private const string KeysSetObject = nameof(iChen) + "-Keys";
		private const string TimeStampObject = nameof(iChen) + "-TimeStamps";

		private const string TrueValue = ".T.";
		private const string FalseValue = ".F.";

		private readonly SemaphoreSlim m_SyncLock = new SemaphoreSlim(1, 1);
		private readonly string m_ObjectType = null;
		private readonly ConnectionMultiplexer m_Cache = null;
		private readonly IDatabase m_Database = null;

		public RedisCache (string connection) : this(DefaultObjectType, connection) { }

		public RedisCache (string objType, string connection)
		{
			if (string.IsNullOrWhiteSpace(objType)) throw new ArgumentNullException(nameof(objType));
			if (string.IsNullOrWhiteSpace(connection)) throw new ArgumentNullException(nameof(connection));

			m_ObjectType = objType;
			m_Cache = ConnectionMultiplexer.Connect(connection);
			m_Database = m_Cache.GetDatabase(KeySpace);
		}

		public virtual void Dispose ()
		{
			m_SyncLock.Wait();
			m_SyncLock.Dispose();
			m_Cache.Dispose();
		}

		private string MakeKey (uint id, string field = null) => m_ObjectType + ":" + id + (string.IsNullOrWhiteSpace(field) ? null : ":" + field);

		public virtual async Task<double> GetAsync (uint id, string key, string field)
		{
			if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));
			if (string.IsNullOrWhiteSpace(field)) throw new ArgumentNullException(nameof(field));

			await m_SyncLock.WaitAsync().ConfigureAwait(false);

			try {
				var value = await m_Database.HashGetAsync(MakeKey(id, key), field).ConfigureAwait(false);
				return Convert.ToDouble(value);
			} finally { m_SyncLock.Release(); }
		}

		public virtual async Task<T> GetAsync<T> (uint id, string key)
		{
			if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));

			await m_SyncLock.WaitAsync().ConfigureAwait(false);

			if (typeof(T) == typeof(DoubleDict)) {
				try {
					var values = await m_Database.HashGetAllAsync(MakeKey(id, key)).ConfigureAwait(false);
					return (T) (values.ToDictionary(kv => kv.Name.ToString(), kv => Convert.ToDouble(kv.Value)) as DoubleDict);
				} finally { m_SyncLock.Release(); }
			}

			RedisValue value;

			try {
				value = await m_Database.HashGetAsync(MakeKey(id), key).ConfigureAwait(false);
			} finally { m_SyncLock.Release(); }

			if (typeof(T) == typeof(bool)) {
				var sval = (string) value;
				if (sval == TrueValue) return (T) Convert.ChangeType(true, typeof(T));
				if (sval == FalseValue) return (T) Convert.ChangeType(true, typeof(T));
				throw new InvalidCastException();
			}

			return (T) Convert.ChangeType(value, typeof(T));
		}

		public virtual async Task<ICollection<string>> GetAllKeys ()
		{
			await m_SyncLock.WaitAsync().ConfigureAwait(false);

			try {
				return (await m_Database.SetMembersAsync(KeysSetObject).ConfigureAwait(false)).Select(x => x.ToString()).ToList();
			} finally { m_SyncLock.Release(); }
		}

		public virtual async Task<(ObjDict dict, DateTimeOffset timestamp)> GetAsync (uint id)
		{
			HashEntry[] values;

			await m_SyncLock.WaitAsync().ConfigureAwait(false);

			try {
				values = await m_Database.HashGetAllAsync(MakeKey(id)).ConfigureAwait(false);

				var dict = values.ToDictionary(entry => (string) entry.Name, entry => {
					var val = entry.Value;

					if (val.IsNull) return (object) null;

					try {
						return (int) val;
					} catch {
						try {
							return (double) val;
						} catch (InvalidCastException) {
							var str = (string) val;

							if (str == TrueValue) return true;
							if (str == FalseValue) return false;
							return str;
						}
					}
				});

				var timestamp = DateTimeOffset.Parse(await m_Database.HashGetAsync(TimeStampObject, id.ToString()).ConfigureAwait(false));

				return (dict, timestamp);
			} finally { m_SyncLock.Release(); }
		}

		public virtual async Task SetAsync (uint id, ObjDict values)
		{
			if (values == null) throw new ArgumentNullException(nameof(values));

			var entries = values.Where(kv => !(kv.Value is DoubleDict)).Select(kv => {
				if (string.IsNullOrWhiteSpace(kv.Key)) throw new ArgumentNullException(nameof(kv.Key));

				switch (kv.Value) {
					case bool val: return new HashEntry(kv.Key, val ? TrueValue : FalseValue);
					case string val: return new HashEntry(kv.Key, val);
					case int val: return new HashEntry(kv.Key, val);
					case uint val: return new HashEntry(kv.Key, val);
					case double val: return new HashEntry(kv.Key, val);
					default: throw new ArgumentOutOfRangeException(nameof(values), $"Invalid value type for key [{kv.Key}]: {kv.Value}");
				}
			}).ToArray();

			await m_SyncLock.WaitAsync().ConfigureAwait(false);
			try {
				var objId = MakeKey(id);
				await m_Database.HashSetAsync(objId, entries).ConfigureAwait(false);
				await m_Database.SetAddAsync(KeysSetObject, objId);
				await m_Database.HashSetAsync(TimeStampObject, new[] { new HashEntry(id.ToString(), DateTimeOffset.Now.ToString("O")) }).ConfigureAwait(false);

				if (entries.Length < values.Count) {
					// If there are hashes, set each one
					foreach (var kv in values) {
						if (!(kv.Value is DoubleDict val)) continue;

						objId = MakeKey(id, kv.Key);
						await m_Database.HashSetAsync(objId, val.Select(kv2 => new HashEntry(kv2.Key, kv2.Value)).ToArray());
						await m_Database.SetAddAsync(KeysSetObject, objId);
					}
				}
			} finally { m_SyncLock.Release(); }
		}

		public virtual async Task SetAsync (uint id, string key, bool value)
		{
			if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));

			await m_SyncLock.WaitAsync().ConfigureAwait(false);
			try {
				var objId = MakeKey(id);
				await m_Database.HashSetAsync(objId, new[] { new HashEntry(key, value ? TrueValue : FalseValue) }).ConfigureAwait(false);
				await m_Database.SetAddAsync(KeysSetObject, objId);
				await m_Database.HashSetAsync(TimeStampObject, new[] { new HashEntry(id.ToString(), DateTimeOffset.Now.ToString("O")) }).ConfigureAwait(false);
			} finally { m_SyncLock.Release(); }
		}

		public virtual async Task SetAsync (uint id, string key, string value)
		{
			if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));
			await m_SyncLock.WaitAsync().ConfigureAwait(false);
			try {
				var objId = MakeKey(id);
				await m_Database.HashSetAsync(objId, new[] { new HashEntry(key, value) }).ConfigureAwait(false);
				await m_Database.SetAddAsync(KeysSetObject, objId);
				await m_Database.HashSetAsync(TimeStampObject, new[] { new HashEntry(id.ToString(), DateTimeOffset.Now.ToString("O")) }).ConfigureAwait(false);
			} finally { m_SyncLock.Release(); }
		}

		public virtual async Task SetAsync (uint id, string key, int value)
		{
			if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));
			await m_SyncLock.WaitAsync().ConfigureAwait(false);
			try {
				var objId = MakeKey(id);
				await m_Database.HashSetAsync(objId, new[] { new HashEntry(key, value) }).ConfigureAwait(false);
				await m_Database.SetAddAsync(KeysSetObject, objId);
				await m_Database.HashSetAsync(TimeStampObject, new[] { new HashEntry(id.ToString(), DateTimeOffset.Now.ToString("O")) }).ConfigureAwait(false);
			} finally { m_SyncLock.Release(); }
		}

		public virtual async Task SetAsync (uint id, string key, uint value)
		{
			if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));
			await m_SyncLock.WaitAsync().ConfigureAwait(false);
			try {
				var objId = MakeKey(id);
				await m_Database.HashSetAsync(objId, new[] { new HashEntry(key, value) }).ConfigureAwait(false);
				await m_Database.SetAddAsync(KeysSetObject, objId);
				await m_Database.HashSetAsync(TimeStampObject, new[] { new HashEntry(id.ToString(), DateTimeOffset.Now.ToString("O")) }).ConfigureAwait(false);
			} finally { m_SyncLock.Release(); }
		}

		public virtual async Task SetAsync (uint id, string key, double value)
		{
			if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));
			await m_SyncLock.WaitAsync().ConfigureAwait(false);
			try {
				var objId = MakeKey(id);
				await m_Database.HashSetAsync(objId, new[] { new HashEntry(key, value) }).ConfigureAwait(false);
				await m_Database.SetAddAsync(KeysSetObject, objId);
				await m_Database.HashSetAsync(TimeStampObject, new[] { new HashEntry(id.ToString(), DateTimeOffset.Now.ToString("O")) }).ConfigureAwait(false);
			} finally { m_SyncLock.Release(); }
		}

		public virtual Task SetAsync (uint id, string key, DoubleDict value)
			=> InternalSetAsync(id, key, value, false);

		public virtual Task UpdateAsync (uint id, string key, DoubleDict value)
			=> InternalSetAsync(id, key, value, true);

		protected virtual async Task InternalSetAsync (uint id, string key, DoubleDict value, bool update)
		{
			if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));
			if (value == null) throw new ArgumentNullException(nameof(value));
			await m_SyncLock.WaitAsync().ConfigureAwait(false);
			try {
				var objId = MakeKey(id, key);

				if (!update) await m_Database.KeyDeleteAsync(objId);

				await m_Database.HashSetAsync(objId, value.Select(kv => new HashEntry(kv.Key, kv.Value)).ToArray()).ConfigureAwait(false);
				await m_Database.SetAddAsync(KeysSetObject, objId);
				await m_Database.HashSetAsync(TimeStampObject, new[] { new HashEntry(id.ToString(), DateTimeOffset.Now.ToString("O")) }).ConfigureAwait(false);
			} finally { m_SyncLock.Release(); }
		}

		public virtual async Task SetAsync (uint id, string key, string field, double value)
		{
			if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));
			if (string.IsNullOrWhiteSpace(field)) throw new ArgumentNullException(nameof(field));
			await m_SyncLock.WaitAsync().ConfigureAwait(false);
			try {
				var objId = MakeKey(id, key);
				await m_Database.HashSetAsync(objId, field, value).ConfigureAwait(false);
				await m_Database.HashSetAsync(TimeStampObject, new[] { new HashEntry(id.ToString(), DateTimeOffset.Now.ToString("O")) }).ConfigureAwait(false);
			} finally { m_SyncLock.Release(); }
		}

		public virtual async Task<bool> HasAsync (uint id, string key)
		{
			await m_SyncLock.WaitAsync().ConfigureAwait(false);

			try {
				return await m_Database.HashExistsAsync(MakeKey(id), key).ConfigureAwait(false);
			} finally { m_SyncLock.Release(); }
		}

		public virtual async Task<DateTimeOffset> GetTimeStampAsync (uint id)
		{
			await m_SyncLock.WaitAsync().ConfigureAwait(false);

			try {
				if (!await m_Database.HashExistsAsync(TimeStampObject, id.ToString()).ConfigureAwait(false)) return default;
				return DateTimeOffset.Parse(await m_Database.HashGetAsync(TimeStampObject, id.ToString()).ConfigureAwait(false));
			} finally { m_SyncLock.Release(); }
		}

		public virtual async Task MarkActiveAsync (uint id)
		{
			await m_SyncLock.WaitAsync().ConfigureAwait(false);

			try {
				await m_Database.HashSetAsync(TimeStampObject, new[] { new HashEntry(id.ToString(), DateTimeOffset.Now.ToString("O")) }).ConfigureAwait(false);
			} finally { m_SyncLock.Release(); }
		}

		public IEnumerable<(uint id, string key, string field, object value, DateTimeOffset timestamp)> Dump ()
			=> throw new NotImplementedException();
	}
}