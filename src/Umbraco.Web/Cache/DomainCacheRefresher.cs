using System;
using Newtonsoft.Json;
using Umbraco.Core;
using Umbraco.Core.Cache;
using Umbraco.Core.Models;
using Umbraco.Core.Persistence.Repositories;
using Umbraco.Web.PublishedCache;

namespace Umbraco.Web.Cache
{
    public sealed class DomainCacheRefresher : PayloadCacheRefresherBase<DomainCacheRefresher>
    {
        #region Json

        internal class JsonPayload
        {
            public JsonPayload(int id, ChangeTypes changeType)
            {
                Id = id;
                ChangeType = changeType;
            }

            public int Id { get; private set; }
            public ChangeTypes ChangeType { get; private set; }
        }

        protected override object Deserialize(string json)
        {
            return JsonConvert.DeserializeObject<JsonPayload[]>(json);
        }

        internal JsonPayload[] GetAsPayload(object o)
        {
            if ((o is JsonPayload[]) == false)
                throw new Exception("Invalid payload object, got {0}, expected JsonPayload[].".FormatWith(o.GetType().FullName));
            return (JsonPayload[])o;
        }

        public enum ChangeTypes : byte
        {
            None = 0,
            RefreshAll = 1,
            Refresh = 2,
            Remove = 3
        }

        #endregion

        #region Define

        protected override DomainCacheRefresher Instance
        {
            get { return this; }
        }

        public override Guid UniqueIdentifier
        {
            get { return DistributedCache.DomainCacheRefresherGuid; }
        }

        public override string Name
        {
            get { return "DomainCacheRefresher"; }
        }

        #endregion

        #region Events

        public override void Refresh(object o)
        {
            var payloads = GetAsPayload(o);

            var runtimeCache = ApplicationContext.Current.ApplicationCache.RuntimeCache;
            runtimeCache.ClearCacheObjectTypes<IDomain>();

            // note: must do what's above FIRST else the repositories still have the old cached
            // content and when the PublishedCachesService is notified of changes it does not see
            // the new content...

            // notify
            var svce = FacadeServiceResolver.Current.Service;
            svce.Notify(payloads);
            // then trigger event
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

        #region Helpers

        private void ClearCache()
        {            
            ClearAllIsolatedCacheByEntityType<IDomain>();
        }

        #endregion
    }
}