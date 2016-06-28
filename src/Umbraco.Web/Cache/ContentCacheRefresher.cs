using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Umbraco.Core;
using Umbraco.Core.Cache;
using Umbraco.Core.Configuration;
using Umbraco.Core.Models;
using Umbraco.Core.Persistence.Repositories;
using Umbraco.Core.Services;
using Umbraco.Web.PublishedCache;

namespace Umbraco.Web.Cache
{
    public sealed class ContentCacheRefresher : PayloadCacheRefresherBase<ContentCacheRefresher>
    {
        #region Json

        public class JsonPayload
        {
            public JsonPayload(int id, TreeChangeTypes changeTypes)
            {
                Id = id;
                ChangeTypes = changeTypes;
            }

            public int Id { get; private set; }
            public TreeChangeTypes ChangeTypes { get; private set; }
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

        protected override ContentCacheRefresher Instance
        {
            get { return this; }
        }

        public override Guid UniqueIdentifier
        {
            get { return DistributedCache.ContentCacheRefresherGuid; }
        }

        public override string Name
        {
            get { return "ContentCacheRefresher"; }
        }

        #endregion

        #region Events

        public override void Refresh(object o)
        {
            var payloads = GetAsPayload(o);

            var runtimeCache = ApplicationContext.Current.ApplicationCache.RuntimeCache;
            runtimeCache.ClearCacheObjectTypes<PublicAccessEntry>();
            foreach (var payload in payloads)
            {
                // remove that one
                runtimeCache.ClearCacheItem(RepositoryBase.GetCacheIdKey<IContent>(payload.Id));

                // remove those that are in the branch
                if (payload.ChangeTypes.HasTypesAny(TreeChangeTypes.RefreshBranch | TreeChangeTypes.Remove))
                {
                    var pathid = "," + payload.Id + ",";
                    runtimeCache.ClearCacheObjectTypes<IContent>((k, v) => v.Path.Contains(pathid));
                }
            }

            // note: must do what's above FIRST else the repositories still have the old cached
            // content and when the PublishedCachesService is notified of changes it does not see
            // the new content...

            var svce = FacadeServiceResolver.Current.Service;
            bool draftChanged, publishedChanged;
            svce.Notify(payloads, out draftChanged, out publishedChanged);

            if (payloads.Any(x => x.ChangeTypes.HasType(TreeChangeTypes.RefreshAll)) || publishedChanged)
            {
                // when a public version changes
                ApplicationContext.Current.ApplicationCache.ClearPartialViewCache();
                MacroCacheRefresher.ClearMacroContentCache(); // just the content
                ClearXsltCache();

                ApplicationContext.Current.ApplicationCache.RuntimeCache.ClearCacheByKeySearch(CacheKeys.IdToKeyCacheKey);
                ApplicationContext.Current.ApplicationCache.RuntimeCache.ClearCacheByKeySearch(CacheKeys.KeyToIdCacheKey);
            }

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

        #region Indirect

        public void RefreshContentTypes()
        {
            // we could try to have a mechanism to notify the PublishedCachesService
            // and figure out whether published items were modified or not... keep it
            // simple for now, just clear the whole thing

            ApplicationContext.Current.ApplicationCache.ClearPartialViewCache();
            MacroCacheRefresher.ClearMacroContentCache(); // just the content
            ClearXsltCache();

            var runtimeCache = ApplicationContext.Current.ApplicationCache.RuntimeCache;
            runtimeCache.ClearCacheObjectTypes<PublicAccessEntry>();
            runtimeCache.ClearCacheObjectTypes<IContent>();
        }

        #endregion

        #region Helpers

        private static void ClearXsltCache()
        {
            // todo: document where this is coming from
            if (UmbracoConfig.For.UmbracoSettings().Content.UmbracoLibraryCacheDuration <= 0) return;
            ApplicationContext.Current.ApplicationCache.ClearCacheObjectTypes("MS.Internal.Xml.XPath.XPathSelectionIterator");
        }

        #endregion
    }
}
