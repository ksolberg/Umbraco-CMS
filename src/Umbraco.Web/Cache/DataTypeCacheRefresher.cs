using System;
using System.Web.Script.Serialization;
using Umbraco.Core;
using Umbraco.Core.Cache;
using System.Linq;
using Newtonsoft.Json;
using Umbraco.Core.Models;
using Umbraco.Web.PublishedCache;

namespace Umbraco.Web.Cache
{
    /// <summary>
    /// A cache refresher to ensure member cache is updated when members change
    /// </summary>    
    public sealed class DataTypeCacheRefresher : PayloadCacheRefresherBase<DataTypeCacheRefresher>
    {
        #region Json

        public class JsonPayload
        {
            public JsonPayload(int id, Guid uniqueId, bool removed)
            {
                Id = id;
                UniqueId = uniqueId;
                Removed = removed;
            }

            public int Id { get; private set; }
            public Guid UniqueId { get; private set; }
            public bool Removed { get; private set; }
        }

        protected override object Deserialize(string json)
        {
            return JsonConvert.DeserializeObject<JsonPayload[]>(json);
        }

        public JsonPayload[] GetAsPayload(object o)
        {
            if ((o is JsonPayload[]) == false)
                throw new Exception("Invalid payload object, got {0}, expected JsonPayload[].".FormatWith(o.GetType().FullName));
            return (JsonPayload[]) o;
        }

        #endregion

        #region Define

        protected override DataTypeCacheRefresher Instance
        {
            get { return this; }
        }

        public override Guid UniqueIdentifier
        {
            get { return new Guid(DistributedCache.DataTypeCacheRefresherId); }
        }

        public override string Name
        {
            get { return "DataTypeCacheRefresher"; }
        }

        #endregion

        #region Events

        public override void Refresh(object o)
        {
            var payloads = GetAsPayload(o);

            //we need to clear the ContentType runtime cache since that is what caches the
            // db data type to store the value against and anytime a datatype changes, this also might change
            // we basically need to clear all sorts of runtime caches here because so many things depend upon a data type
            
            ClearAllIsolatedCacheByEntityType<IContent>();
            ClearAllIsolatedCacheByEntityType<IContentType>();
            ClearAllIsolatedCacheByEntityType<IMedia>();
            ClearAllIsolatedCacheByEntityType<IMediaType>();
            ClearAllIsolatedCacheByEntityType<IMember>();
            ClearAllIsolatedCacheByEntityType<IMemberType>();
            ApplicationContext.Current.ApplicationCache.RuntimeCache.ClearCacheByKeySearch(CacheKeys.IdToKeyCacheKey);
            ApplicationContext.Current.ApplicationCache.RuntimeCache.ClearCacheByKeySearch(CacheKeys.KeyToIdCacheKey);

            payloads.ForEach(payload =>
            {
                //clears the prevalue cache
                var dataTypeCache = ApplicationContext.Current.ApplicationCache.IsolatedRuntimeCache.GetCache<IDataTypeDefinition>();
                if (dataTypeCache)
                    dataTypeCache.Result.ClearCacheByKeySearch(string.Format("{0}{1}", CacheKeys.DataTypePreValuesCacheKey, payload.Id));
            });

            // notify
            var svce = FacadeServiceResolver.Current.Service;
            svce.Notify(payloads);

            // now we can trigger the event
            base.Refresh(o);
        }

        // these events should never trigger
        // everything should be PAYLOAD/JSON

        public override void RefreshAll()
        {
            throw new NotSupportedException();
        }

        public override void Refresh(int id)
        {
            throw new NotSupportedException();
        }

        public override void Refresh(Guid id)
        {
            throw new NotSupportedException();
        }

        public override void Remove(int id)
        {
            throw new NotSupportedException();
        }

        #endregion
    }
}