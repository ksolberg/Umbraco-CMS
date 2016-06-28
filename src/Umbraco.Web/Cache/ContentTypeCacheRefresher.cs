using System;
using System.Collections.Generic;
using System.Linq;
using Umbraco.Core;
using Umbraco.Core.Cache;
using Umbraco.Core.Models;
using Umbraco.Core.Services;
using Umbraco.Web.PublishedCache;
using Umbraco.Web.PublishedCache.XmlPublishedCache;

// fixme - should use ClearAllIsolatedCacheByEntityType<IContent>(); etc everywhere, see 7.4!!

namespace Umbraco.Web.Cache
{
    /// <summary>
    /// A cache refresher to ensure content type cache is updated when content types change - this is applicable to content, media and member types
    /// </summary>
    /// <remarks>
    /// This is not intended to be used directly in your code
    /// </remarks>
    public sealed class ContentTypeCacheRefresher : PayloadCacheRefresherBase<ContentTypeCacheRefresher>
    {
        #region Json

        public class JsonPayload
        {
            public JsonPayload(string itemType, int id, ContentTypeServiceBase.ChangeTypes changeTypes)
            {
                ItemType = itemType;
                Id = id;
                ChangeTypes = changeTypes;
            }

            public string ItemType { get; private set; }
            public int Id { get; private set; }
            public ContentTypeServiceBase.ChangeTypes ChangeTypes { get; private set; }
        }

        protected override object Deserialize(string json)
        {
            return Newtonsoft.Json.JsonConvert.DeserializeObject<JsonPayload[]>(json);
        }

        public JsonPayload[] GetAsPayload(object o)
        {
            if ((o is JsonPayload[]) == false)
                throw new Exception("Invalid payload object, got {0}, expected JsonPayload[].".FormatWith(o.GetType().FullName));
            return (JsonPayload[]) o;
        }

        #endregion

        #region Define

        protected override ContentTypeCacheRefresher Instance
        {
            get { return this; }
        }

        public override Guid UniqueIdentifier
        {
            get { return DistributedCache.ContentTypeCacheRefresherGuid; }
        }

        public override string Name
        {
            get { return "ContentTypeCacheRefresher"; }
        }
        
        #endregion

        #region Events
        
        public override void Refresh(object o)
        {
            var payloads = GetAsPayload(o);

            // TODO: refactor
            // we should NOT directly clear caches here, but instead ask whatever class
            // is managing the cache to please clear that cache properly

            if (payloads.Any(x => x.ItemType == typeof(IContentType).Name))
            {
                ApplicationContext.Current.ApplicationCache.RuntimeCache.ClearCacheObjectTypes<IContent>();
                ApplicationContext.Current.ApplicationCache.RuntimeCache.ClearCacheObjectTypes<IContentType>();
            }

            if (payloads.Any(x => x.ItemType == typeof(IMediaType).Name))
            {
                ApplicationContext.Current.ApplicationCache.RuntimeCache.ClearCacheObjectTypes<IMedia>();
                ApplicationContext.Current.ApplicationCache.RuntimeCache.ClearCacheObjectTypes<IMediaType>();
            }

            if (payloads.Any(x => x.ItemType == typeof(IMemberType).Name))
            {
                ApplicationContext.Current.ApplicationCache.RuntimeCache.ClearCacheObjectTypes<IMember>();
                ApplicationContext.Current.ApplicationCache.RuntimeCache.ClearCacheObjectTypes<IMemberType>();
            }

            ApplicationContext.Current.ApplicationCache.RuntimeCache.ClearCacheByKeySearch(CacheKeys.IdToKeyCacheKey);
            ApplicationContext.Current.ApplicationCache.RuntimeCache.ClearCacheByKeySearch(CacheKeys.KeyToIdCacheKey);

            foreach (var id in payloads.Select(x => x.Id))
                ClearLegacyCaches(id);

            if (payloads.Any(x => x.ItemType == typeof(IContentType).Name))
                RefreshContentCache();

            if (payloads.Any(x => x.ItemType == typeof(IMediaType).Name))
                RefreshMediaCache();

            if (payloads.Any(x => x.ItemType == typeof(IMemberType).Name))
                RefreshMemberCache();

            // notify
            var svce = FacadeServiceResolver.Current.Service;
            svce.Notify(payloads);
            // now we can trigger the event
            base.Refresh(o);
        }

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

        private static void RefreshContentCache()
        {
            var contentCacheRefresher = CacheRefreshersResolver.Current.GetById(DistributedCache.ContentCacheRefresherGuid) as ContentCacheRefresher;
            if (contentCacheRefresher == null) throw new Exception("oops");

            // don't try to be clever - refresh all
            contentCacheRefresher.RefreshContentTypes();
        }

        private static void RefreshMediaCache()
        {
            var mediaCacheRefresher = CacheRefreshersResolver.Current.GetById(DistributedCache.MediaCacheRefresherGuid) as MediaCacheRefresher;
            if (mediaCacheRefresher == null) throw new Exception("oops");

            // don't try to be clever - refresh all
            mediaCacheRefresher.RefreshMediaTypes();
        }

        private static void RefreshMemberCache()
        {
            var memberCacheRefresher = CacheRefreshersResolver.Current.GetById(DistributedCache.MemberCacheRefresherGuid) as MemberCacheRefresher;
            if (memberCacheRefresher == null) throw new Exception("oops");

            // don't try to be clever - refresh all
            memberCacheRefresher.RefreshMemberTypes();
        }
        
        private static void ClearLegacyCaches(int contentTypeId /*, string contentTypeAlias, IEnumerable<int> propertyTypeIds*/)
        {
            // legacy umbraco.cms.businesslogic.ContentType

            // TODO - get rid of all this mess

            // clears the cache for each property type associated with the content type
            // see src/umbraco.cms/businesslogic/propertytype/propertytype.cs
            // that cache is disabled because we could not clear it properly
            //foreach (var pid in propertyTypeIds)
            //    ApplicationContext.Current.ApplicationCache.RuntimeCache.ClearCacheItem(CacheKeys.PropertyTypeCacheKey + pid);

            // clears the cache associated with the content type itself
            ApplicationContext.Current.ApplicationCache.RuntimeCache.ClearCacheItem(CacheKeys.ContentTypeCacheKey + contentTypeId);

            // clears the cache associated with the content type properties collection
            ApplicationContext.Current.ApplicationCache.RuntimeCache.ClearCacheItem(CacheKeys.ContentTypePropertiesCacheKey + contentTypeId);

            // clears the dictionary object cache of the legacy ContentType
            // see src/umbraco.cms/businesslogic/ContentType.cs
            // that cache is disabled because we could not clear it properly
            //global::umbraco.cms.businesslogic.ContentType.RemoveFromDataTypeCache(contentTypeAlias);
        }

        #endregion
    }
}
