using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Web.Hosting;
using CSharpTest.Net.Collections;
using Newtonsoft.Json;
using Umbraco.Core;
using Umbraco.Core.Cache;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Models.Membership;
using Umbraco.Core.Models.PublishedContent;
using Umbraco.Core.ObjectResolution;
using Umbraco.Core.Persistence;
using Umbraco.Core.Persistence.DatabaseModelDefinitions;
using Umbraco.Core.Persistence.Querying;
using Umbraco.Core.Persistence.Repositories;
using Umbraco.Core.Services;
using Umbraco.Web.Cache;
using Umbraco.Web.PublishedCache.NuCache.DataSource;
using Umbraco.Web.PublishedCache.XmlPublishedCache;
using Umbraco.Web.Routing;
using Database = Umbraco.Web.PublishedCache.NuCache.DataSource.Database;
#pragma warning disable 618
using Content = umbraco.cms.businesslogic.Content;
#pragma warning restore 618

namespace Umbraco.Web.PublishedCache.NuCache
{
    class FacadeService : FacadeServiceBase
    {
        private readonly ServiceContext _serviceContext;
        private readonly Database _dataSource;
        private readonly ILogger _logger;
        private readonly Options _options;

        // volatile because we read it with no lock
        private volatile bool _isReady;

        private readonly ContentStore2 _contentStore;
        private readonly ContentStore2 _mediaStore;
        private readonly SnapDictionary<int, Domain> _domainStore;
        private readonly object _storesLock = new object();

        private BPlusTree<int, ContentNodeKit> _localContentDb;
        private BPlusTree<int, ContentNodeKit> _localMediaDb;
        private readonly bool _localDbExists;

        // define constant - determines whether to use cache when previewing
        // to store eg routes, property converted values, anything - caching
        // means faster execution, but uses memory - not sure if we want it
        // so making it configureable.
        public static readonly bool FullCacheWhenPreviewing = true;

        // define constant - determines whether to cache the published content
        // objects (in the snapshot cache, or facade cache, depending on preview)
        // or to refetch them all the time. caching is faster but uses more
        // memory. not sure what we want.
        public static readonly bool CachePublishedContentChildren = true;

        // define constant - determines whether to cache the content cache root
        // objects (in the snapshot cache, or facade cache, depending on preview)
        // or to refecth them all the time. caching is faster but uses more
        // memory - not sure what we want.
        public static readonly bool CacheContentCacheRoots = true;

        #region Constructors

        public FacadeService(Options options, MainDom mainDom, ServiceContext serviceContext, DatabaseContext databaseContext, ILogger logger)
        {
            _serviceContext = serviceContext;
            _dataSource = new Database(databaseContext);
            _logger = logger;
            _options = options;

            // we always want to handle repository events, configured or not
            // assuming no repository event will trigger before the whole db is ready
            // (ideally we'd have Upgrading.App vs Upgrading.Data application states...)
            InitializeRepositoryEvents();

            // however, the cache is NOT available until we are configured, because loading 
            // content (and content types) from database cannot be consistent (see notes in "Handle 
            // Notifications" region), so
            // - notifications will be ignored
            // - trying to obtain a facade from the service will throw
            if (ApplicationContext.Current.IsConfigured == false)
                return;

            if (_options.IgnoreLocalDb == false)
            {
                var registered = mainDom.Register(
                    null,
                    () =>
                    {
                        lock (_storesLock)
                        {
                            _contentStore.ReleaseLocalDb();
                            _localContentDb = null;
                            _mediaStore.ReleaseLocalDb();
                            _localMediaDb = null;
                        }
                    });

                if (registered)
                {
                    var localContentDbPath = HostingEnvironment.MapPath("~/App_Data/NuCache.Content.db");
                    var localMediaDbPath = HostingEnvironment.MapPath("~/App_Data/NuCache.Media.db");
                    _localDbExists = System.IO.File.Exists(localContentDbPath) && System.IO.File.Exists(localMediaDbPath);

                    // if both local dbs exist then GetTree will open them, else new dbs will be created
                    _localContentDb = BTree.GetTree(localContentDbPath, _localDbExists);
                    _localMediaDb = BTree.GetTree(localMediaDbPath, _localDbExists);
                }

                // stores are created with a db so they can write to it, but they do not read from it,
                // stores need to be populated, happens in OnResolutionFrozen which uses _localDbExists to
                // figure out whether it can read the dbs or it should populate them from sql
                _contentStore = new ContentStore2(logger, _localContentDb);
                _mediaStore = new ContentStore2(logger, _localMediaDb);
            }
            else
            {
                _contentStore = new ContentStore2(logger);
                _mediaStore = new ContentStore2(logger);
            }

            _domainStore = new SnapDictionary<int, Domain>();

            if (Resolution.IsFrozen)
                OnResolutionFrozen();
            else 
                Resolution.Frozen += (sender, args) => OnResolutionFrozen();
        }

        private void OnResolutionFrozen()
        {
            lock (_storesLock)
            {
                // populate the stores

                try
                {
                    if (_localDbExists)
                    {
                        LockAndLoadContent(LoadContentFromLocalDbLocked);
                        LockAndLoadMedia(LoadMediaFromLocalDbLocked);
                    }
                    else
                    {
                        LockAndLoadContent(LoadContentFromDatabaseLocked);
                        LockAndLoadMedia(LoadMediaFromDatabaseLocked);
                    }

                    LockAndLoadDomains();
                }
                catch (Exception e)
                {
                    _logger.Error<FacadeService>("Panic, exception while loading cache data.", e);
                }

                // finaly, cache is ready!
                _isReady = true;
            }
        }

        private void InitializeRepositoryEvents()
        {
            // plug repository event handlers
            // these trigger within the transaction to ensure consistency
            // and are used to maintain the central, database-level XML cache
            ContentRepository.RemovedEntity += OnContentRemovedEntity;
            //ContentRepository.RemovedVersion += OnContentRemovedVersion;
            ContentRepository.RefreshedEntity += OnContentRefreshedEntity;
            MediaRepository.RemovedEntity += OnMediaRemovedEntity;
            //MediaRepository.RemovedVersion += OnMediaRemovedVersion;
            MediaRepository.RefreshedEntity += OnMediaRefreshedEntity;
            MemberRepository.RemovedEntity += OnMemberRemovedEntity;
            //MemberRepository.RemovedVersion += OnMemberRemovedVersion;
            MemberRepository.RefreshedEntity += OnMemberRefreshedEntity;

            // plug
            ContentTypeService.TxEntityRefreshed += OnContentTypeEntityRefreshed;
            MediaTypeService.TxEntityRefreshed += OnMediaTypeEntityRefreshed;
            MemberTypeService.TxEntityRefreshed += OnMemberTypeEntityRefreshed;

            // mostly to be sure - each node should have been deleted beforehand
            ContentRepository.EmptiedRecycleBin += OnEmptiedRecycleBin;
            MediaRepository.EmptiedRecycleBin += OnEmptiedRecycleBin;

            // temp - until we get rid of Content
#pragma warning disable 618
            Content.DeletedContent += OnDeletedContent;
#pragma warning restore 618
        }

        public class Options
        {
            // indicates that the facade cache should reuse the application request cache
            // otherwise a new cache object would be created for the facade specifically,
            // which is the default - web boot manager uses this to optimze facades
            public bool FacadeCacheIsApplicationRequestCache;

            public bool IgnoreLocalDb;
        }

        #endregion

        #region Populate Stores

        // sudden panic... but in RepeatableRead can a content that I haven't already read, be removed
        // before I read it? NO! because the WHOLE content tree is read-locked using WithReadLocked.
        // don't panic.

        private void LockAndLoadContent(Action action)
        {
            _contentStore.WriteLocked(() =>
            {
                var contentService = _serviceContext.ContentService as ContentService;
                if (contentService == null) throw new Exception("oops");

                contentService.WithReadLocked(_ => action());
            });
        }

        private void LoadContentFromDatabaseLocked()
        {
            // locks:
            // contentStore is wlocked (1 thread)
            // content (and types) are read-locked

            var contentTypes = _serviceContext.ContentTypeService.GetAll()
                .Select(x => new PublishedContentType(PublishedItemType.Content, x));
            _contentStore.UpdateContentTypes(null, contentTypes, null);

            if (_localContentDb != null)
                _localContentDb.Clear();

            _logger.Debug<FacadeService>("Loading content from database...");
            var sw = Stopwatch.StartNew();
            var kits = _dataSource.GetAllContentSources();
            _contentStore.SetAll(kits);
            sw.Stop();
            _logger.Debug<FacadeService>("Loaded content from database (" + sw.ElapsedMilliseconds + "ms).");
        }

        private void LoadContentFromLocalDbLocked()
        {
            var contentTypes = _serviceContext.ContentTypeService.GetAll()
                .Select(x => new PublishedContentType(PublishedItemType.Content, x));
            _contentStore.UpdateContentTypes(null, contentTypes, null);

            _logger.Debug<FacadeService>("Loading content from local db...");
            var sw = Stopwatch.StartNew();
            var kits = _localContentDb.Select(x => x.Value);
            _contentStore.SetAll(kits);
            sw.Stop();
            _logger.Debug<FacadeService>("Loaded content from local db (" + sw.ElapsedMilliseconds + "ms).");
        }

        // keep these around - might be useful

        //private void LoadContentBranch(IContent content)
        //{
        //    LoadContent(content);

        //    foreach (var child in content.Children())
        //        LoadContentBranch(child);
        //}

        //private void LoadContent(IContent content)
        //{
        //    var contentService = _serviceContext.ContentService as ContentService;
        //    if (contentService == null) throw new Exception("oops");
        //    var newest = content;
        //    var published = newest.Published
        //        ? newest
        //        : (newest.HasPublishedVersion ? contentService.GetByVersion(newest.PublishedVersionGuid) : null);

        //    var contentNode = CreateContentNode(newest, published);
        //    _contentStore.Set(contentNode);
        //}

        private void LockAndLoadMedia(Action action)
        {
            _mediaStore.WriteLocked(() =>
            {
                var mediaService = _serviceContext.MediaService as MediaService;
                if (mediaService == null) throw new Exception("oops");

                mediaService.WithReadLocked(_ => action());
            });
        }

        private void LoadMediaFromDatabaseLocked()
        {
            // locks & notes: see content

            var mediaTypes = _serviceContext.MediaTypeService.GetAll()
                .Select(x => new PublishedContentType(PublishedItemType.Media, x));
            _mediaStore.UpdateContentTypes(null, mediaTypes, null);

            if (_localMediaDb != null)
                _localMediaDb.Clear();

            _logger.Debug<FacadeService>("Loading media from database...");
            var sw = Stopwatch.StartNew();
            var kits = _dataSource.GetAllMediaSources();
            _mediaStore.SetAll(kits);
            sw.Stop();
            _logger.Debug<FacadeService>("Loaded media from database (" + sw.ElapsedMilliseconds + "ms).");
        }

        private void LoadMediaFromLocalDbLocked()
        {
            var mediaTypes = _serviceContext.MediaTypeService.GetAll()
                .Select(x => new PublishedContentType(PublishedItemType.Media, x));
            _mediaStore.UpdateContentTypes(null, mediaTypes, null);

            _logger.Debug<FacadeService>("Loading media from local db...");
            var sw = Stopwatch.StartNew();
            var kits = _localMediaDb.Select(x => x.Value);
            _mediaStore.SetAll(kits);
            sw.Stop();
            _logger.Debug<FacadeService>("Loaded media from local db (" + sw.ElapsedMilliseconds + "ms).");
        }

        // keep these around - might be useful

        //private void LoadMediaBranch(IMedia media)
        //{
        //    LoadMedia(media);

        //    foreach (var child in media.Children())
        //        LoadMediaBranch(child);
        //}

        //private void LoadMedia(IMedia media)
        //{
        //    var mediaType = _contentTypeCache.Get(PublishedItemType.Media, media.ContentTypeId);

        //    var mediaData = new ContentData
        //    {
        //        Name = media.Name,
        //        Published = true,
        //        Version = media.Version,
        //        VersionDate = media.UpdateDate,
        //        WriterId = media.CreatorId, // what else?
        //        TemplateId = -1, // have none
        //        Properties = GetPropertyValues(media)
        //    };

        //    var mediaNode = new ContentNode(media.Id, mediaType,
        //        media.Level, media.Path, media.SortOrder,
        //        media.ParentId, media.CreateDate, media.CreatorId,
        //        null, mediaData);

        //    _mediaStore.Set(mediaNode);
        //}

        //private Dictionary<string, object> GetPropertyValues(IContentBase content)
        //{
        //    var propertyEditorResolver = PropertyEditorResolver.Current; // should inject

        //    return content
        //        .Properties
        //        .Select(property =>
        //        {
        //            var e = propertyEditorResolver.GetByAlias(property.PropertyType.PropertyEditorAlias);
        //            var v = e == null
        //                ? property.Value
        //                : e.ValueEditor.ConvertDbToString(property, property.PropertyType, _serviceContext.DataTypeService);
        //            return new KeyValuePair<string, object>(property.Alias, v);
        //        })
        //        .ToDictionary(x => x.Key, x => x.Value);
        //}

        //private ContentData CreateContentData(IContent content)
        //{
        //    return new ContentData
        //    {
        //        Name = content.Name,
        //        Published = content.Published,
        //        Version = content.Version,
        //        VersionDate = content.UpdateDate,
        //        WriterId = content.WriterId,
        //        TemplateId = content.Template == null ? -1 : content.Template.Id,
        //        Properties = GetPropertyValues(content)
        //    };
        //}

        //private ContentNode CreateContentNode(IContent newest, IContent published)
        //{
        //    var contentType = _contentTypeCache.Get(PublishedItemType.Content, newest.ContentTypeId);

        //    var draftData = newest.Published
        //        ? null
        //        : CreateContentData(newest);

        //    var publishedData = newest.Published
        //        ? CreateContentData(newest)
        //        : (published == null ? null : CreateContentData(published));

        //    var contentNode = new ContentNode(newest.Id, contentType,
        //        newest.Level, newest.Path, newest.SortOrder,
        //        newest.ParentId, newest.CreateDate, newest.CreatorId,
        //        draftData, publishedData);

        //    return contentNode;
        //}

        private void LockAndLoadDomains()
        {
            _domainStore.WriteLocked(() =>
            {
                var domainService = _serviceContext.DomainService as DomainService;
                if (domainService == null) throw new Exception("oops");
                domainService.WithReadLocked(_ => LoadDomainsLocked());
            });
        }

        private void LoadDomainsLocked()
        {
            var domains = _serviceContext.DomainService.GetAll(true);
            foreach (var domain in domains
                .Where(x => x.RootContentId.HasValue && x.LanguageIsoCode.IsNullOrWhiteSpace() == false)
                .Select(x => new Domain(x.Id, x.DomainName, x.RootContentId.Value, CultureInfo.GetCultureInfo(x.LanguageIsoCode), x.IsWildcard)))
            {
                _domainStore.Set(domain.Id, domain);
            }
        }

        #endregion

        #region Handle Notifications

        // note: if the service is not ready, ie _isReady is false, then notifications are ignored

        // SetUmbracoVersionStep issues a DistributedCache.Instance.RefreshAllFacade() call which should cause
        // the entire content, media etc caches to reload from database -- and then the app restarts -- however,
        // at the time SetUmbracoVersionStep runs, Umbraco is not fully initialized and therefore some property
        // value converters, etc are not registered, and rebuilding the NuCache may not work properly.
        //
        // More details: ApplicationContext.IsConfigured being false, ApplicationEventHandler.ExecuteWhen... is
        // called and in most cases events are skipped, so property value converters are not registered or
        // removed, so PublishedPropertyType either initializes with the wrong converter, or throws because it
        // detects more than one converter for a property type.
        //
        // It's not an issue for XmlStore - the app restart takes place *after* the install has refreshed the
        // cache, and XmlStore just writes a new umbraco.config file upon RefreshAll, so that's OK.
        //
        // But for NuCache... we cannot rebuild the cache now. So it will NOT work and we are not fixing it,
        // because now we should ALWAYS run with the database server messenger, and then the RefreshAll will
        // be processed as soon as we are configured and the messenger processes instructions.

        public override void Notify(ContentCacheRefresher.JsonPayload[] payloads, out bool draftChanged, out bool publishedChanged)
        {
            // no cache, nothing we can do
            if (_isReady == false)
            {
                draftChanged = publishedChanged = false;
                return;
            }

            var draftChanged2 = false;
            var publishedChanged2 = false;
            _contentStore.WriteLocked(() =>
            {
                NotifyLocked(payloads, out draftChanged2, out publishedChanged2);
            });
            draftChanged = draftChanged2;
            publishedChanged = publishedChanged2;

            if (draftChanged || publishedChanged)
                Facade.Current.Resync();
        }

        private void NotifyLocked(IEnumerable<ContentCacheRefresher.JsonPayload> payloads, out bool draftChanged, out bool publishedChanged)
        {
            publishedChanged = false;
            draftChanged = false;

            var contentService = _serviceContext.ContentService as ContentService;
            if (contentService == null) throw new Exception("oops");

            // locks:
            // content (and content types) are read-locked while reading content
            // contentStore is wlocked (so readable, only no new views)
            // and it can be wlocked by 1 thread only at a time
            // contentStore is write-locked during changes

            foreach (var payload in payloads)
            {
                _logger.Debug<FacadeService>("Notified {0} for content {1}".FormatWith(payload.ChangeTypes, payload.Id));

                if (payload.ChangeTypes.HasType(TreeChangeTypes.RefreshAll))
                {
                    contentService.WithReadLocked(_ => LoadContentFromDatabaseLocked());
                    draftChanged = publishedChanged = true;
                    continue;
                }

                if (payload.ChangeTypes.HasType(TreeChangeTypes.Remove))
                {
                    if (_contentStore.Clear(payload.Id))
                        draftChanged = publishedChanged = true;
                    continue;
                }

                if (payload.ChangeTypes.HasTypesNone(TreeChangeTypes.RefreshNode | TreeChangeTypes.RefreshBranch))
                {
                    // ?!
                    continue;
                }

                // fixme - should we do some RV check here? (later)

                var capture = payload;
                contentService.WithReadLocked(repository =>
                {
                    if (capture.ChangeTypes.HasType(TreeChangeTypes.RefreshBranch))
                    {
                        // ?? should we do some RV check here?
                        var kits = _dataSource.GetBranchContentSources(capture.Id);
                        _contentStore.SetBranch(capture.Id, kits);
                    }
                    else
                    {
                        // ?? should we do some RV check here?
                        var kit = _dataSource.GetContentSource(capture.Id);
                        if (kit.IsEmpty)
                        {
                            _contentStore.Clear(capture.Id);
                        }
                        else
                        {
                            _contentStore.Set(kit);
                        }
                    }
                });

                // ?? cannot tell really because we're not doing RV checks
                draftChanged = publishedChanged = true;
            }
        }

        public override void Notify(MediaCacheRefresher.JsonPayload[] payloads, out bool anythingChanged)
        {
            // no cache, nothing we can do
            if (_isReady == false)
            {
                anythingChanged = false;
                return;
            }

            var anythingChanged2 = false;
            _mediaStore.WriteLocked(() =>
            {
                NotifyLocked(payloads, out anythingChanged2);
            });
            anythingChanged = anythingChanged2;

            if (anythingChanged)
                Facade.Current.Resync();
        }

        private void NotifyLocked(IEnumerable<MediaCacheRefresher.JsonPayload> payloads, out bool anythingChanged)
        {
            anythingChanged = false;

            var mediaService = _serviceContext.MediaService as MediaService;
            if (mediaService == null) throw new Exception("oops");

            // locks:
            // see notes for content cache refresher

            foreach (var payload in payloads)
            {
                _logger.Debug<FacadeService>("Notified {0} for media {1}".FormatWith(payload.ChangeTypes, payload.Id));

                if (payload.ChangeTypes.HasType(TreeChangeTypes.RefreshAll))
                {
                    mediaService.WithReadLocked(_ => LoadMediaFromDatabaseLocked());
                    anythingChanged = true;
                    continue;
                }

                if (payload.ChangeTypes.HasType(TreeChangeTypes.Remove))
                {
                    if (_mediaStore.Clear(payload.Id))
                        anythingChanged = true;
                    continue;
                }

                if (payload.ChangeTypes.HasTypesNone(TreeChangeTypes.RefreshNode | TreeChangeTypes.RefreshBranch))
                {
                    // ?!
                    continue;
                }

                // fixme - should we do some RV checks here? (later)

                var capture = payload;
                mediaService.WithReadLocked(repository =>
                {
                    if (capture.ChangeTypes.HasType(TreeChangeTypes.RefreshBranch))
                    {
                        // ?? should we do some RV check here?
                        var kits = _dataSource.GetBranchMediaSources(capture.Id);
                        _mediaStore.SetBranch(capture.Id, kits);
                    }
                    else
                    {
                        // ?? should we do some RV check here?
                        var kit = _dataSource.GetMediaSource(capture.Id);
                        if (kit.IsEmpty)
                        {
                            _mediaStore.Clear(capture.Id);
                        }
                        else
                        {
                            _mediaStore.Set(kit);
                        }
                    }
                });

                // ?? cannot tell really because we're not doing RV checks
                anythingChanged = true;
            }
        }

        public override void Notify(ContentTypeCacheRefresher.JsonPayload[] payloads)
        {
            // no cache, nothing we can do
            if (_isReady == false)
                return;

            foreach (var payload in payloads)
                LogHelper.Debug<XmlStore>("Notified {0} for {1} {2}".FormatWith(payload.ChangeTypes, payload.ItemType, payload.Id));

            var removedIds = payloads
                .Where(x => x.ItemType == typeof(IContentType).Name && x.ChangeTypes.HasType(ContentTypeServiceBase.ChangeTypes.Remove))
                .Select(x => x.Id)
                .ToArray();

            var refreshedIds = payloads
                .Where(x => x.ItemType == typeof(IContentType).Name && x.ChangeTypes.HasType(ContentTypeServiceBase.ChangeTypes.RefreshMain))
                .Select(x => x.Id)
                .ToArray();

            if (removedIds.Length > 0 || refreshedIds.Length > 0)
                _contentStore.WriteLocked(() =>
                {
                    // ReSharper disable AccessToModifiedClosure
                    RefreshContentTypesLocked(removedIds, refreshedIds);
                    // ReSharper restore AccessToModifiedClosure
                });

            // same for media cache

            removedIds = payloads
                .Where(x => x.ItemType == typeof(IMediaType).Name && x.ChangeTypes.HasType(ContentTypeServiceBase.ChangeTypes.Remove))
                .Select(x => x.Id)
                .ToArray();

            refreshedIds = payloads
                .Where(x => x.ItemType == typeof(IMediaType).Name && x.ChangeTypes.HasType(ContentTypeServiceBase.ChangeTypes.RefreshMain))
                .Select(x => x.Id)
                .ToArray();

            if (removedIds.Length > 0 || refreshedIds.Length > 0)
                _mediaStore.WriteLocked(() =>
                {
                    RefreshMediaTypesLocked(removedIds, refreshedIds);
                });

            Facade.Current.Resync();
        }

        public override void Notify(DataTypeCacheRefresher.JsonPayload[] payloads)
        {
            // no cache, nothing we can do
            if (_isReady == false)
                return;

            var idsA = payloads.Select(x => x.Id).ToArray();

            foreach (var payload in payloads)
                LogHelper.Debug<FacadeService>("Notified {0} for data type {1}".FormatWith(payload.Removed ? "Removed" : "Refreshed", payload.Id));

            _contentStore.WriteLocked(() =>
                _mediaStore.WriteLocked(() =>
                {
                    var contentService = _serviceContext.ContentService as ContentService;
                    if (contentService == null) throw new Exception("oops");

                    contentService.WithReadLocked(
                        _ => _contentStore.UpdateDataTypes(idsA, id => CreateContentType(PublishedItemType.Content, id)));

                    var mediaService = _serviceContext.MediaService as MediaService;
                    if (mediaService == null) throw new Exception("oops");

                    mediaService.WithReadLocked(
                        _ => _mediaStore.UpdateDataTypes(idsA, id => CreateContentType(PublishedItemType.Media, id)));
                }));

            Facade.Current.Resync();
        }

        public override void Notify(DomainCacheRefresher.JsonPayload[] payloads)
        {
            // no cache, nothing we can do
            if (_isReady == false)
                return;

            _domainStore.WriteLocked(() =>
            {
                foreach (var payload in payloads)
                {
                    switch (payload.ChangeType)
                    {
                        case DomainCacheRefresher.ChangeTypes.RefreshAll:
                            var domainService = _serviceContext.DomainService as DomainService;
                            if (domainService == null) throw new Exception("oops");
                            domainService.WithReadLocked(_ => LoadDomainsLocked());
                            break;
                        case DomainCacheRefresher.ChangeTypes.Remove:
                            _domainStore.Clear(payload.Id);
                            break;
                        case DomainCacheRefresher.ChangeTypes.Refresh:
                            var domain = _serviceContext.DomainService.GetById(payload.Id);
                            if (domain == null) continue;
                            if (domain.RootContentId.HasValue == false) continue; // anomaly
                            if (domain.LanguageIsoCode.IsNullOrWhiteSpace()) continue; // anomaly
                            var culture = CultureInfo.GetCultureInfo(domain.LanguageIsoCode);
                            _domainStore.Set(domain.Id, new Domain(domain.Id, domain.DomainName, domain.RootContentId.Value, culture, domain.IsWildcard));
                            break;
                    }
                }
            });
        }

        #endregion

        #region Content Types

        private IEnumerable<PublishedContentType> CreateContentTypes(PublishedItemType itemType, params int[] ids)
        {
            IEnumerable<IContentTypeComposition> contentTypes;
            switch (itemType)
            {
                case PublishedItemType.Content:
                    contentTypes = _serviceContext.ContentTypeService.GetAll(ids);
                    break;
                case PublishedItemType.Media:
                    contentTypes = _serviceContext.MediaTypeService.GetAll(ids);
                    break;
                case PublishedItemType.Member:
                    contentTypes = _serviceContext.MemberTypeService.GetAll(ids);
                    break;
                default:
                    throw new ArgumentOutOfRangeException("itemType");
            }

            // some may be missing - not checking here

            return contentTypes.Select(x => new PublishedContentType(itemType, x));
        }

        private PublishedContentType CreateContentType(PublishedItemType itemType, int id)
        {
            IContentTypeComposition contentType;
            switch (itemType)
            {
                case PublishedItemType.Content:
                    contentType = _serviceContext.ContentTypeService.Get(id);
                    break;
                case PublishedItemType.Media:
                    contentType = _serviceContext.MediaTypeService.Get(id);
                    break;
                case PublishedItemType.Member:
                    contentType = _serviceContext.MemberTypeService.Get(id);
                    break;
                default:
                    throw new ArgumentOutOfRangeException("itemType");
            }

            return contentType == null ? null : new PublishedContentType(itemType, contentType);
        }

        private void RefreshContentTypesLocked(IEnumerable<int> removedIds, IEnumerable<int> refreshedIds)
        {
            // locks:
            // content (and content types) are read-locked while reading content
            // contentStore is wlocked (so readable, only no new views)
            // and it can be wlocked by 1 thread only at a time

            var contentService = _serviceContext.ContentService as ContentService;
            if (contentService == null) throw new Exception("oops");

            var refreshedIdsA = refreshedIds.ToArray();

            contentService.WithReadLocked(repository =>
            {
                var typesA = CreateContentTypes(PublishedItemType.Content, refreshedIdsA).ToArray();
                var kits = _dataSource.GetTypeContentSources(refreshedIdsA);
                _contentStore.UpdateContentTypes(removedIds, typesA, kits);
            });
        }

        private void RefreshMediaTypesLocked(IEnumerable<int> removedIds, IEnumerable<int> refreshedIds)
        {
            // locks:
            // media (and content types) are read-locked while reading media
            // mediaStore is wlocked (so readable, only no new views)
            // and it can be wlocked by 1 thread only at a time

            var mediaService = _serviceContext.MediaService as MediaService;
            if (mediaService == null) throw new Exception("oops");

            var refreshedIdsA = refreshedIds.ToArray();

            mediaService.WithReadLocked(repository =>
            {
                var typesA = CreateContentTypes(PublishedItemType.Media, refreshedIdsA).ToArray();
                var kits = _dataSource.GetTypeMediaSources(refreshedIdsA);
                _mediaStore.UpdateContentTypes(removedIds, typesA, kits);
            });
        }

        #endregion

        #region Create, Get Facade

        private long _contentGen, _mediaGen, _domainGen;
        private ICacheProvider _snapshotCache;

        public override IFacade CreateFacade(string previewToken)
        {
            // no cache, no joy
            if (_isReady == false)
                throw new InvalidOperationException("The facade service has not properly initialized.");

            var preview = previewToken.IsNullOrWhiteSpace() == false;
            return new Facade(this, preview);
        }

        public Facade.FacadeElements GetElements(bool previewDefault)
        {
            // note: using ObjectCacheRuntimeCacheProvider for snapshot and facade caches
            // is not recommended because it creates an inner MemoryCache which is a heavy
            // thing - better use a StaticCacheProvider which "just" creates a concurrent
            // dictionary

            // for facade cache, StaticCacheProvider MAY be OK but it is not thread-safe,
            // nothing like that...
            // for snapshot cache, StaticCacheProvider is a No-No, use something better.

            ContentStore2.Snapshot contentSnap, mediaSnap;
            SnapDictionary<int, Domain>.Snapshot domainSnap;
            ICacheProvider snapshotCache;
            lock (_storesLock)
            {
                contentSnap = _contentStore.CreateSnapshot();
                mediaSnap = _mediaStore.CreateSnapshot();
                domainSnap = _domainStore.CreateSnapshot();
                snapshotCache = _snapshotCache;

                // create a new snapshot cache if snapshots are different gens
                if (contentSnap.Gen != _contentGen || mediaSnap.Gen != _mediaGen || domainSnap.Gen != _domainGen || _snapshotCache == null)
                {
                    _contentGen = contentSnap.Gen;
                    _mediaGen = mediaSnap.Gen;
                    _domainGen = domainSnap.Gen;
                    snapshotCache = _snapshotCache = new DictionaryCacheProvider();
                }
            }

            var facadeCache = _options.FacadeCacheIsApplicationRequestCache
                ? ApplicationContext.Current.ApplicationCache.RequestCache
                : new StaticCacheProvider(); // assuming that's OK for tests, etc
            var memberTypeCache = new PublishedContentTypeCache(null, null, _serviceContext.MemberTypeService);

            var domainCache = new DomainCache(domainSnap);

            return new Facade.FacadeElements
            {
                ContentCache = new ContentCache(previewDefault, contentSnap, facadeCache, snapshotCache, new DomainHelper(domainCache)),
                MediaCache = new MediaCache(previewDefault, mediaSnap),
                MemberCache = new MemberCache(previewDefault, _serviceContext.MemberService, _serviceContext.DataTypeService, memberTypeCache),
                DomainCache = domainCache,
                FacadeCache = facadeCache,
                SnapshotCache = snapshotCache
            };
        }

        #endregion

        #region Preview

        public override string EnterPreview(IUser user, int contentId)
        {
            return "preview"; // anything
        }

        public override void RefreshPreview(string previewToken, int contentId)
        {
            // nothing
        }

        public override void ExitPreview(string previewToken)
        {
            // nothing
        }

        #endregion

        #region Handle Repository Events For Database PreCache

        // note: if the service is not ready, ie _isReady is false, then we still handle repository events,
        // because we can, we do not need a working facade to do it - the only reason why it could cause an
        // issue is if the database table is not ready, but that should be prevented by migrations.

        // we need them to be "repository" events ie to trigger from within the repository transaction,
        // because they need to be consistent with the content that is being refreshed/removed - and that
        // should be guaranteed by a DB transaction

        private void OnContentRemovedEntity(object sender, VersionableRepositoryBase<int, IContent>.EntityChangeEventArgs args)
        {
            OnRemovedEntity(args.UnitOfWork.Database, args.Entities);
        }

        private void OnMediaRemovedEntity(object sender, VersionableRepositoryBase<int, IMedia>.EntityChangeEventArgs args)
        {
            OnRemovedEntity(args.UnitOfWork.Database, args.Entities);
        }

        private void OnMemberRemovedEntity(object sender, VersionableRepositoryBase<int, IMember>.EntityChangeEventArgs args)
        {
            OnRemovedEntity(args.UnitOfWork.Database, args.Entities);
        }

        private void OnRemovedEntity(UmbracoDatabase db, IEnumerable<IContentBase> items)
        {
            foreach (var item in items)
            {
                var parms = new { id = item.Id };
                db.Execute("DELETE FROM cmsContentNu WHERE nodeId=@id", parms);
            }

            // note: could be optimized by using "WHERE nodeId IN (...)" delete clauses
        }

        private static readonly string[] PropertiesImpactingAllVersions = { "SortOrder", "ParentId", "Level", "Path", "Trashed" };

        private static bool HasChangesImpactingAllVersions(IContent icontent)
        {
            var content = (Core.Models.Content) icontent;

            // UpdateDate will be dirty
            // Published may be dirty if saving a Published entity
            // so cannot do this (would always be true):
            //return content.IsEntityDirty();

            // have to be more precise & specify properties
            return PropertiesImpactingAllVersions.Any(content.IsPropertyDirty);
        }

        private void OnContentRefreshedEntity(VersionableRepositoryBase<int, IContent> sender, VersionableRepositoryBase<int, IContent>.EntityChangeEventArgs args)
        {
            var db = args.UnitOfWork.Database;

            foreach (var c in args.Entities)
            {
                OnRepositoryRefreshed(db, c, false);

                // if unpublishing, remove from table
                if (((Core.Models.Content) c).PublishedState == PublishedState.Unpublishing)
                {
                    db.Execute("DELETE FROM cmsContentNu WHERE nodeId=@id AND published=1", new { id = c.Id });
                    continue;
                }

                // need to update the published data if we're saving the published version,
                // or having an impact on that version - we update the published data even when masked

                IContent pc = null;
                if (c.Published)
                {
                    // saving the published version = update data
                    pc = c;
                }
                else
                {
                    // saving the non-published version, but there is a published version
                    // check whether we have changes that impact the published version (move...)
                    if (c.HasPublishedVersion && HasChangesImpactingAllVersions(c))
                        pc = sender.GetByVersion(c.PublishedVersionGuid);
                }

                if (pc == null)
                    continue;

                OnRepositoryRefreshed(db, pc, true);
            }
        }

        private void OnMediaRefreshedEntity(object sender, VersionableRepositoryBase<int, IMedia>.EntityChangeEventArgs args)
        {
            var db = args.UnitOfWork.Database;

            foreach (var m in args.Entities)
            {
                // for whatever reason we delete some data when the media is trashed
                // at least that's what the MediaService implementation did
                if (m.Trashed)
                    db.Execute("DELETE FROM cmsContentXml WHERE nodeId=@id", new { id = m.Id });

                OnRepositoryRefreshed(db, m, true);
            }
        }

        private void OnMemberRefreshedEntity(object sender, VersionableRepositoryBase<int, IMember>.EntityChangeEventArgs args)
        {
            var db = args.UnitOfWork.Database;

            foreach (var m in args.Entities)
            {
                OnRepositoryRefreshed(db, m, true);
            }
        }

        private void OnRepositoryRefreshed(UmbracoDatabase db, IContentBase content, bool published)
        {
            // use a custom SQL to update row version on each update
            //db.InsertOrUpdate(dto);

            var dto = GetDto(content, published);
            db.InsertOrUpdate(dto,
                "SET data=@data, rv=rv+1 WHERE nodeId=@id AND published=@published",
                new
                {
                    data = dto.Data,
                    id = dto.NodeId,
                    published = dto.Published
                });
        }

        private static void OnEmptiedRecycleBin(object sender, RecycleBinRepository<int, IContent>.RecycleBinEventArgs args)
        {
            OnEmptiedRecycleBin(args.UnitOfWork.Database, args.NodeObjectType);
        }

        private static void OnEmptiedRecycleBin(object sender, RecycleBinRepository<int, IMedia>.RecycleBinEventArgs args)
        {
            OnEmptiedRecycleBin(args.UnitOfWork.Database, args.NodeObjectType);
        }

        // mostly to be sure - each node should have been deleted beforehand
        private static void OnEmptiedRecycleBin(UmbracoDatabase db, Guid nodeObjectType)
        {
            // required by SQL-CE
            const string sql = @"DELETE FROM cmsContentNu
WHERE cmsContentNu.nodeId IN (
    SELECT id FROM umbracoNode
    WHERE trashed=1 AND nodeObjectType=@nodeObjectType
)";

            var parms = new { /*@nodeObjectType =*/ nodeObjectType };
            db.Execute(sql, parms);
        }

#pragma warning disable 618
        private static void OnDeletedContent(object sender, Content.ContentDeleteEventArgs args)
#pragma warning restore 618
        {
            var db = args.Database;
            var parms = new { @nodeId = args.Id };
            db.Execute("DELETE FROM cmsContentNu WHERE nodeId=@nodeId", parms);
        }

        private void OnContentTypeEntityRefreshed(ContentTypeServiceBase<IContentType> sender, ContentTypeServiceBase<IContentType>.Change.EventArgs args)
        {
            // handling a transaction event that does not play well with cache...
            RepositoryBase.SetCacheEnabledForCurrentRequest(false);

            const ContentTypeServiceBase.ChangeTypes types // only for those that have been refreshed
                = ContentTypeServiceBase.ChangeTypes.RefreshMain | ContentTypeServiceBase.ChangeTypes.RefreshOther;
            var contentTypeIds = args.Changes.Where(x => x.ChangeTypes.HasTypesAny(types)).Select(x => x.Item.Id).ToArray();
            if (contentTypeIds.Any())
                RebuildContentDbCache(contentTypeIds: contentTypeIds);
        }

        private void OnMediaTypeEntityRefreshed(ContentTypeServiceBase<IMediaType> sender, ContentTypeServiceBase<IMediaType>.Change.EventArgs args)
        {
            // handling a transaction event that does not play well with cache...
            RepositoryBase.SetCacheEnabledForCurrentRequest(false);

            const ContentTypeServiceBase.ChangeTypes types // only for those that have been refreshed
                = ContentTypeServiceBase.ChangeTypes.RefreshMain | ContentTypeServiceBase.ChangeTypes.RefreshOther;
            var mediaTypeIds = args.Changes.Where(x => x.ChangeTypes.HasTypesAny(types)).Select(x => x.Item.Id).ToArray();
            if (mediaTypeIds.Any())
                RebuildMediaDbCache(contentTypeIds: mediaTypeIds);
        }

        private void OnMemberTypeEntityRefreshed(ContentTypeServiceBase<IMemberType> sender, ContentTypeServiceBase<IMemberType>.Change.EventArgs args)
        {
            // handling a transaction event that does not play well with cache...
            RepositoryBase.SetCacheEnabledForCurrentRequest(false);

            const ContentTypeServiceBase.ChangeTypes types // only for those that have been refreshed
                = ContentTypeServiceBase.ChangeTypes.RefreshMain | ContentTypeServiceBase.ChangeTypes.RefreshOther;
            var memberTypeIds = args.Changes.Where(x => x.ChangeTypes.HasTypesAny(types)).Select(x => x.Item.Id).ToArray();
            if (memberTypeIds.Any())
                RebuildMemberDbCache(contentTypeIds: memberTypeIds);
        }

        private static ContentNuDto GetDto(IContentBase content, bool published)
        {
            // should inject these in ctor
            // BUT for the time being we decide not to support ConvertDbToXml/String
            //var propertyEditorResolver = PropertyEditorResolver.Current;
            //var dataTypeService = ApplicationContext.Current.Services.DataTypeService;

            var data = new Dictionary<string, object>();
            foreach (var prop in content.Properties)
            {
                var value = prop.Value;
                //if (value != null)
                //{
                //    var e = propertyEditorResolver.GetByAlias(prop.PropertyType.PropertyEditorAlias);

                //    // We are converting to string, even for database values which are integer or
                //    // DateTime, which is not optimum. Doing differently would require that we have a way to tell
                //    // whether the conversion to XML string changes something or not... which we don't, and we
                //    // don't want to implement it as PropertyValueEditor.ConvertDbToXml/String should die anyway.

                //    // Don't think about improving the situation here: this is a corner case and the real
                //    // thing to do is to get rig of PropertyValueEditor.ConvertDbToXml/String.

                //    // Use ConvertDbToString to keep it simple, although everywhere we use ConvertDbToXml and
                //    // nothing ensures that the two methods are consistent.

                //    if (e != null)
                //        value = e.ValueEditor.ConvertDbToString(prop, prop.PropertyType, dataTypeService);
                //}
                data[prop.Alias] = value;
            }

            var dto = new ContentNuDto
            {
                NodeId = content.Id,
                Published = published,

                // note that numeric values (which are Int32) are serialized without their
                // type (eg "value":1234) and JsonConvert by default deserializes them as Int64

                Data = JsonConvert.SerializeObject(data)
            };

            return dto;
        }

        #endregion

        #region Rebuild Database PreCache

        public void RebuildContentDbCache(int groupSize = 5000, IEnumerable<int> contentTypeIds = null)
        {
            var svc = _serviceContext.ContentService as ContentService;
            if (svc == null) throw new Exception("oops");
            svc.WithWriteLocked(repository => RebuildContentDbCacheLocked(repository, groupSize, contentTypeIds));
        }

        // assumes content tree lock
        private void RebuildContentDbCacheLocked(ContentRepository repository, int groupSize, IEnumerable<int> contentTypeIds)
        {
            var contentTypeIdsA = contentTypeIds == null ? null : contentTypeIds.ToArray();
            var contentObjectType = Guid.Parse(Constants.ObjectTypes.Document);
            var db = repository.UnitOfWork.Database;

            // remove all - if anything fails the transaction will rollback
            if (contentTypeIds == null || contentTypeIdsA.Length == 0)
            {
                // must support SQL-CE
                db.Execute(@"DELETE FROM cmsContentNu
WHERE cmsContentNu.nodeId IN (
    SELECT id FROM umbracoNode WHERE umbracoNode.nodeObjectType=@objType
)",
                    new { objType = contentObjectType });
            }
            else
            {
                // assume number of ctypes won't blow IN(...)
                // must support SQL-CE
                db.Execute(@"DELETE FROM cmsContentNu
WHERE cmsContentNu.nodeId IN (
    SELECT id FROM umbracoNode
    JOIN cmsContent ON cmsContent.nodeId=umbracoNode.id
    WHERE umbracoNode.nodeObjectType=@objType
    AND cmsContent.contentType IN (@ctypes) 
)",
                    new { objType = contentObjectType, ctypes = contentTypeIdsA });
            }

            // insert back - if anything fails the transaction will rollback
            var query = Query<IContent>.Builder;
            if (contentTypeIds != null && contentTypeIdsA.Length > 0)
                query = query.WhereIn(x => x.ContentTypeId, contentTypeIdsA); // assume number of ctypes won't blow IN(...)

            long pageIndex = 0;
            long processed = 0;
            long total;
            do
            {
                // .GetPagedResultsByQuery implicitely adds (cmsDocument.newest = 1)
                var descendants = repository.GetPagedResultsByQuery(query, pageIndex++, groupSize, out total, "Path", Direction.Ascending, true);
                var items = new List<ContentNuDto>();
                var guids = new List<Guid>();
                foreach (var c in descendants)
                {
                    items.Add(GetDto(c, c.Published));
                    if (c.Published == false && c.HasPublishedVersion)
                        guids.Add(c.PublishedVersionGuid);
                }
                items.AddRange(guids.Select(x => GetDto(repository.GetByVersion(x), true)));

                // ReSharper disable once RedundantArgumentDefaultValue
                db.BulkInsertRecords(items, null, false); // run within the current transaction and do NOT commit
                processed += items.Count;
            } while (processed < total);
        }

        public void RebuildMediaDbCache(int groupSize = 5000, IEnumerable<int> contentTypeIds = null)
        {
            var svc = _serviceContext.MediaService as MediaService;
            if (svc == null) throw new Exception("oops");
            svc.WithWriteLocked(repository => RebuildMediaDbCacheLocked(repository, groupSize, contentTypeIds));
        }

        // assumes media tree lock
        public void RebuildMediaDbCacheLocked(MediaRepository repository, int groupSize, IEnumerable<int> contentTypeIds)
        {
            var contentTypeIdsA = contentTypeIds == null ? null : contentTypeIds.ToArray();
            var mediaObjectType = Guid.Parse(Constants.ObjectTypes.Media);
            var db = repository.UnitOfWork.Database;

            // remove all - if anything fails the transaction will rollback
            if (contentTypeIds == null || contentTypeIdsA.Length == 0)
            {
                // must support SQL-CE
                db.Execute(@"DELETE FROM cmsContentNu
WHERE cmsContentNu.nodeId IN (
    SELECT id FROM umbracoNode WHERE umbracoNode.nodeObjectType=@objType
)",
                    new { objType = mediaObjectType });
            }
            else
            {
                // assume number of ctypes won't blow IN(...)
                // must support SQL-CE
                db.Execute(@"DELETE FROM cmsContentNu
WHERE cmsContentNu.nodeId IN (
    SELECT id FROM umbracoNode
    JOIN cmsContent ON cmsContent.nodeId=umbracoNode.id
    WHERE umbracoNode.nodeObjectType=@objType
    AND cmsContent.contentType IN (@ctypes) 
)",
                    new { objType = mediaObjectType, ctypes = contentTypeIdsA });
            }

            // insert back - if anything fails the transaction will rollback
            var query = Query<IMedia>.Builder;
            if (contentTypeIds != null && contentTypeIdsA.Length > 0)
                query = query.WhereIn(x => x.ContentTypeId, contentTypeIdsA); // assume number of ctypes won't blow IN(...)

            long pageIndex = 0;
            long processed = 0;
            long total;
            do
            {
                var descendants = repository.GetPagedResultsByQuery(query, pageIndex++, groupSize, out total, "Path", Direction.Ascending, true);
                var items = descendants.Select(m => GetDto(m, true)).ToArray();
                // ReSharper disable once RedundantArgumentDefaultValue
                db.BulkInsertRecords(items, null, false); // run within the current transaction and do NOT commit
                processed += items.Length;
            } while (processed < total);
        }

        public void RebuildMemberDbCache(int groupSize = 5000, IEnumerable<int> contentTypeIds = null)
        {
            var svc = _serviceContext.MemberService as MemberService;
            if (svc == null) throw new Exception("oops");
            svc.WithWriteLocked(repository => RebuildMemberDbCacheLocked(repository, groupSize, contentTypeIds));
        }

        // assumes member tree lock
        public void RebuildMemberDbCacheLocked(MemberRepository repository, int groupSize, IEnumerable<int> contentTypeIds)
        {
            var contentTypeIdsA = contentTypeIds == null ? null : contentTypeIds.ToArray();
            var memberObjectType = Guid.Parse(Constants.ObjectTypes.Member);
            var db = repository.UnitOfWork.Database;

            // remove all - if anything fails the transaction will rollback
            if (contentTypeIds == null || contentTypeIdsA.Length == 0)
            {
                // must support SQL-CE
                db.Execute(@"DELETE FROM cmsContentNu
WHERE cmsContentNu.nodeId IN (
    SELECT id FROM umbracoNode WHERE umbracoNode.nodeObjectType=@objType
)",
                    new { objType = memberObjectType });
            }
            else
            {
                // assume number of ctypes won't blow IN(...)
                // must support SQL-CE
                db.Execute(@"DELETE FROM cmsContentNu
WHERE cmsContentNu.nodeId IN (
    SELECT id FROM umbracoNode
    JOIN cmsContent ON cmsContent.nodeId=umbracoNode.id
    WHERE umbracoNode.nodeObjectType=@objType
    AND cmsContent.contentType IN (@ctypes) 
)",
                    new { objType = memberObjectType, ctypes = contentTypeIdsA });
            }

            // insert back - if anything fails the transaction will rollback
            var query = Query<IMember>.Builder;
            if (contentTypeIds != null && contentTypeIdsA.Length > 0)
                query = query.WhereIn(x => x.ContentTypeId, contentTypeIdsA); // assume number of ctypes won't blow IN(...)

            long pageIndex = 0;
            long processed = 0;
            long total;
            do
            {
                var descendants = repository.GetPagedResultsByQuery(query, pageIndex++, groupSize, out total, "Path", Direction.Ascending, true);
                var items = descendants.Select(m => GetDto(m, true)).ToArray();
                // ReSharper disable once RedundantArgumentDefaultValue
                db.BulkInsertRecords(items, null, false); // run within the current transaction and do NOT commit
                processed += items.Length;
            } while (processed < total);
        }

        public bool VerifyContentDbCache()
        {
            var svc = _serviceContext.ContentService as ContentService;
            if (svc == null) throw new Exception("oops");
            return svc.WithReadLocked(x => VerifyContentDbCacheLocked(x));
        }

        // assumes content tree lock
        private bool VerifyContentDbCacheLocked(ContentRepository repository)
        {
            // every published content item should have a corresponding row in cmsContentXml
            // every content item should have a corresponding row in cmsPreviewXml

            var contentObjectType = Guid.Parse(Constants.ObjectTypes.Document);
            var db = repository.UnitOfWork.Database;

            var count = db.ExecuteScalar<int>(@"SELECT COUNT(*)
FROM umbracoNode
JOIN cmsDocument ON (umbracoNode.id=cmsDocument.nodeId AND (cmsDocument.newest=1 OR cmsDocument.published=1))
LEFT JOIN cmsContentNu ON (umbracoNode.id=cmsContentNu.nodeId AND cmsContentNu.published=cmsDocument.published)
WHERE umbracoNode.nodeObjectType=@objType
AND cmsContentNu.nodeId IS NULL;"
                , new { objType = contentObjectType });

            return count == 0;
        }

        public bool VerifyMediaDbCache()
        {
            var svc = _serviceContext.MediaService as MediaService;
            if (svc == null) throw new Exception("oops");
            return svc.WithReadLocked(x => VerifyMediaDbCacheLocked(x));
        }

        // assumes media tree lock
        public bool VerifyMediaDbCacheLocked(MediaRepository repository)
        {
            // every non-trashed media item should have a corresponding row in cmsContentXml

            var mediaObjectType = Guid.Parse(Constants.ObjectTypes.Media);
            var db = repository.UnitOfWork.Database;

            var count = db.ExecuteScalar<int>(@"SELECT COUNT(*)
FROM umbracoNode
JOIN cmsDocument ON (umbracoNode.id=cmsDocument.nodeId AND cmsDocument.published=1)
LEFT JOIN cmsContentNu ON (umbracoNode.id=cmsContentNu.nodeId AND cmsContentNu.published=1)
WHERE umbracoNode.nodeObjectType=@objType
AND cmsContentNu.nodeId IS NULL
", new { objType = mediaObjectType });

            return count == 0;
        }

        public bool VerifyMemberDbCache()
        {
            var svc = _serviceContext.MemberService as MemberService;
            if (svc == null) throw new Exception("oops");
            return svc.WithReadLocked(VerifyMemberDbCacheLocked);
        }

        // assumes member tree lock
        public bool VerifyMemberDbCacheLocked(MemberRepository repository)
        {
            // every member item should have a corresponding row in cmsContentXml

            var memberObjectType = Guid.Parse(Constants.ObjectTypes.Member);
            var db = repository.UnitOfWork.Database;

            var count = db.ExecuteScalar<int>(@"SELECT COUNT(*)
FROM umbracoNode
LEFT JOIN cmsContentNu ON (umbracoNode.id=cmsContentNu.nodeId AND cmsContentNu.published=1)
WHERE umbracoNode.nodeObjectType=@objType
AND cmsContentNu.nodeId IS NULL
", new { objType = memberObjectType });

            return count == 0;
        }

        #endregion

        #region Instrument

        public string GetStatus()
        {
            var dbCacheIsOk = VerifyContentDbCache()
                && VerifyMediaDbCache()
                && VerifyMemberDbCache();

            var cg = _contentStore.GenCount;
            var mg = _mediaStore.GenCount;
            var cs = _contentStore.SnapCount;
            var ms = _mediaStore.SnapCount;
            var ce = _contentStore.Count;
            var me = _mediaStore.Count;

            return "I'm feeling good, really." +
                " Database cache is " + (dbCacheIsOk ? "ok" : "NOT ok (rebuild?)") + "." +
                " ContentStore has " + cg + " generation" + (cg > 1 ? "s" : "") +
                ", " + cs + " snapshot" + (cs > 1 ? "s" : "") +
                " and " + ce + " entr" + (ce > 1 ? "ies" : "y") + "." +
                " MediaStore has " + mg + " generation" + (mg > 1 ? "s" : "") +
                ", " + ms + " snapshot" + (ms > 1 ? "s" : "") +
                " and " + me + " entr" + (me > 1 ? "ies" : "y") + ".";
        }

        public void Collect()
        {
            var contentCollect = _contentStore.CollectAsync();
            var mediaCollect = _mediaStore.CollectAsync();
            System.Threading.Tasks.Task.WaitAll(contentCollect, mediaCollect);
        }

        #endregion
    }
}
