using System;
using System.Collections.Generic;
using System.Data;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Umbraco.Core.Cache;
using Umbraco.Core.Events;
using Umbraco.Core.IO;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Models.Rdbms;
using Umbraco.Core.Persistence;
using Umbraco.Core.Persistence.DatabaseModelDefinitions;
using Umbraco.Core.Persistence.Querying;
using Umbraco.Core.Persistence.Repositories;
using Umbraco.Core.Persistence.UnitOfWork;

namespace Umbraco.Core.Services
{
    /// <summary>
    /// Represents the Media Service, which is an easy access to operations involving <see cref="IMedia"/>
    /// </summary>
    public class MediaService : RepositoryService, IMediaService, IMediaServiceOperations
    {
        private IMediaTypeService _mediaTypeService;

        #region Constructors

        public MediaService(IDatabaseUnitOfWorkProvider provider, RepositoryFactory repositoryFactory, ILogger logger, IEventMessagesFactory eventMessagesFactory, IDataTypeService dataTypeService, IUserService userService)
            : base(provider, repositoryFactory, logger, eventMessagesFactory)
        {
            // though... these are not used?
            Mandate.ParameterNotNull(dataTypeService, "dataTypeService");
            Mandate.ParameterNotNull(userService, "userService");

            _lrepo = new LockingRepository<MediaRepository>(UowProvider,
                uow => RepositoryFactory.CreateMediaRepository(uow) as MediaRepository,
                LockingRepositoryLockIds, LockingRepositoryLockIds);
        }

        internal IMediaTypeService MediaTypeService
        {
            get
            {
                if (_mediaTypeService == null)
                    throw new InvalidOperationException("MediaService.MediaTypeService has not been initialized.");
                return _mediaTypeService;
            }
            set { _mediaTypeService = value; }
        }

        #endregion

        #region Locking

        // constant
        private static readonly int[] LockingRepositoryLockIds = { Constants.System.MediaTreeLock };

        private readonly LockingRepository<MediaRepository> _lrepo;

        internal void WithReadLocked(Action<MediaRepository> action, bool autoCommit = true)
        {
            _lrepo.WithReadLocked(xr => action(xr.Repository), autoCommit);
        }

        internal TResult WithReadLocked<TResult>(Func<MediaRepository, TResult> func, bool autoCommit = true)
        {
            return _lrepo.WithReadLocked(xr => func(xr.Repository), autoCommit);
        }

        internal void WithWriteLocked(Action<MediaRepository> action, bool autoCommit = true)
        {
            _lrepo.WithWriteLocked(xr => action(xr.Repository), autoCommit);
        }

        #endregion

        #region Count

        public int Count(string contentTypeAlias = null)
        {
            return WithReadLocked(repository => repository.Count(contentTypeAlias));
        }

        public int CountChildren(int parentId, string contentTypeAlias = null)
        {
            return WithReadLocked(repository => repository.CountChildren(parentId, contentTypeAlias));
        }

        public int CountDescendants(int parentId, string contentTypeAlias = null)
        {
            return WithReadLocked(repository => repository.CountDescendants(parentId, contentTypeAlias));
        }

        #endregion

        #region Create

        /// <summary>
        /// Creates an <see cref="IMedia"/> object using the alias of the <see cref="IMediaType"/>
        /// that this Media should based on.
        /// </summary>
        /// <remarks>
        /// Note that using this method will simply return a new IMedia without any identity
        /// as it has not yet been persisted. It is intended as a shortcut to creating new media objects
        /// that does not invoke a save operation against the database.
        /// </remarks>
        /// <param name="name">Name of the Media object</param>
        /// <param name="parentId">Id of Parent for the new Media item</param>
        /// <param name="mediaTypeAlias">Alias of the <see cref="IMediaType"/></param>
        /// <param name="userId">Optional id of the user creating the media item</param>
        /// <returns><see cref="IMedia"/></returns>
        public IMedia CreateMedia(string name, int parentId, string mediaTypeAlias, int userId = 0)
        {
            var mediaType = GetMediaType(mediaTypeAlias);
            var media = new Models.Media(name, parentId, mediaType);

            var parent = GetById(media.ParentId);
            media.Path = string.Concat(parent.IfNotNull(x => x.Path, media.ParentId.ToString()), ",", media.Id);
            CreateMedia(media, null, parentId, false, userId, false);
            return media;
        }

        /// <summary>
        /// Creates an <see cref="IMedia"/> object using the alias of the <see cref="IMediaType"/>
        /// that this Media should based on.
        /// </summary>
        /// <remarks>
        /// Note that using this method will simply return a new IMedia without any identity
        /// as it has not yet been persisted. It is intended as a shortcut to creating new media objects
        /// that does not invoke a save operation against the database.
        /// </remarks>
        /// <param name="name">Name of the Media object</param>
        /// <param name="parent">Parent <see cref="IMedia"/> for the new Media item</param>
        /// <param name="mediaTypeAlias">Alias of the <see cref="IMediaType"/></param>
        /// <param name="userId">Optional id of the user creating the media item</param>
        /// <returns><see cref="IMedia"/></returns>
        public IMedia CreateMedia(string name, IMedia parent, string mediaTypeAlias, int userId = 0)
        {
            if (parent == null) throw new ArgumentNullException("parent");

            var mediaType = GetMediaType(mediaTypeAlias);
            var media = new Models.Media(name, parent, mediaType);
            media.Path = string.Concat(parent.Path, ",", media.Id);

            CreateMedia(media, null, parent.Id, false, userId, false);
            return media;
        }

        /// <summary>
        /// Creates an <see cref="IMedia"/> object using the alias of the <see cref="IMediaType"/>
        /// that this Media should based on.
        /// </summary>
        /// <remarks>
        /// This method returns an <see cref="IMedia"/> object that has been persisted to the database
        /// and therefor has an identity.
        /// </remarks>
        /// <param name="name">Name of the Media object</param>
        /// <param name="parentId">Id of Parent for the new Media item</param>
        /// <param name="mediaTypeAlias">Alias of the <see cref="IMediaType"/></param>
        /// <param name="userId">Optional id of the user creating the media item</param>
        /// <returns><see cref="IMedia"/></returns>
        public IMedia CreateMediaWithIdentity(string name, int parentId, string mediaTypeAlias, int userId = 0)
        {
            var mediaType = GetMediaType(mediaTypeAlias);
            var media = new Models.Media(name, parentId, mediaType);
            CreateMedia(media, null, parentId, false, userId, true);
            return media;
        }

        /// <summary>
        /// Creates an <see cref="IMedia"/> object using the alias of the <see cref="IMediaType"/>
        /// that this Media should based on.
        /// </summary>
        /// <remarks>
        /// This method returns an <see cref="IMedia"/> object that has been persisted to the database
        /// and therefor has an identity.
        /// </remarks>
        /// <param name="name">Name of the Media object</param>
        /// <param name="parent">Parent <see cref="IMedia"/> for the new Media item</param>
        /// <param name="mediaTypeAlias">Alias of the <see cref="IMediaType"/></param>
        /// <param name="userId">Optional id of the user creating the media item</param>
        /// <returns><see cref="IMedia"/></returns>
        public IMedia CreateMediaWithIdentity(string name, IMedia parent, string mediaTypeAlias, int userId = 0)
        {
            if (parent == null) throw new ArgumentNullException("parent");

            var mediaType = GetMediaType(mediaTypeAlias);
            var media = new Models.Media(name, parent, mediaType);
            CreateMedia(media, parent, parent.Id, true, userId, true);
            return media;
        }

        private void CreateMedia(Models.Media media, IMedia parent, int parentId, bool withParent, int userId, bool withIdentity)
        {
            // NOTE: I really hate the notion of these Creating/Created events - they are so inconsistent, I've only just found
            // out that in these 'WithIdentity' methods, the Saving/Saved events were not fired, wtf. Anyways, they're added now.
            var newArgs = withParent
                ? new NewEventArgs<IMedia>(media, media.ContentType.Alias, parent)
                : new NewEventArgs<IMedia>(media, media.ContentType.Alias, parentId);
            // ReSharper disable once CSharpWarnings::CS0618
            if (Creating.IsRaisedEventCancelled(newArgs, this))
            {
                media.WasCancelled = true;
                return;
            }

            media.CreatorId = userId;

            if (withIdentity)
            {
                if (Saving.IsRaisedEventCancelled(new SaveEventArgs<IMedia>(media), this))
                {
                    media.WasCancelled = true;
                    return;
                }

                WithWriteLocked(repository => repository.AddOrUpdate(media));

                Saved.RaiseEvent(new SaveEventArgs<IMedia>(media, false), this);
                TreeChanged.RaiseEvent(new TreeChange<IMedia>(media, TreeChangeTypes.RefreshNode).ToEventArgs(), this);
            }

            Created.RaiseEvent(new NewEventArgs<IMedia>(media, false, media.ContentType.Alias, parent), this);

            var msg = withIdentity
                ? "Media '{0}' was created with Id {1}"
                : "Media '{0}' was created";
            Audit(AuditType.New, string.Format(msg, media.Name, media.Id), media.CreatorId, media.Id);
        }

        #endregion

        #region Get, Has, Is

        /// <summary>
        /// Gets an <see cref="IMedia"/> object by Id
        /// </summary>
        /// <param name="id">Id of the Content to retrieve</param>
        /// <returns><see cref="IMedia"/></returns>
        public IMedia GetById(int id)
        {
            return WithReadLocked(repository => repository.Get(id));
        }

        /// <summary>
        /// Gets an <see cref="IMedia"/> object by Id
        /// </summary>
        /// <param name="ids">Ids of the Media to retrieve</param>
        /// <returns><see cref="IMedia"/></returns>
        public IEnumerable<IMedia> GetByIds(IEnumerable<int> ids)
        {
            var idsA = ids.ToArray();
            return idsA.Length == 0
                ? Enumerable.Empty<IMedia>()
                : WithReadLocked(repository => repository.GetAll(idsA));
        }

        /// <summary>
        /// Gets an <see cref="IMedia"/> object by its 'UniqueId'
        /// </summary>
        /// <param name="key">Guid key of the Media to retrieve</param>
        /// <returns><see cref="IMedia"/></returns>
        public IMedia GetById(Guid key)
        {
            var query = Query<IMedia>.Builder.Where(x => x.Key == key);
            return WithReadLocked(repository => repository.GetByQuery(query).SingleOrDefault());
        }

        /// <summary>
        /// Gets a collection of <see cref="IMedia"/> objects by the Id of the <see cref="IContentType"/>
        /// </summary>
        /// <param name="id">Id of the <see cref="IMediaType"/></param>
        /// <returns>An Enumerable list of <see cref="IMedia"/> objects</returns>
        public IEnumerable<IMedia> GetMediaOfMediaType(int id)
        {
            var query = Query<IMedia>.Builder.Where(x => x.ContentTypeId == id);
            return WithReadLocked(repository => repository.GetByQuery(query));
        }

        /// <summary>
        /// Gets a collection of <see cref="IMedia"/> objects by Level
        /// </summary>
        /// <param name="level">The level to retrieve Media from</param>
        /// <returns>An Enumerable list of <see cref="IMedia"/> objects</returns>
        public IEnumerable<IMedia> GetByLevel(int level)
        {
            var query = Query<IMedia>.Builder.Where(x => x.Level == level && x.Trashed == false);
            return WithReadLocked(repository => repository.GetByQuery(query));
        }

        /// <summary>
        /// Gets a specific version of an <see cref="IMedia"/> item.
        /// </summary>
        /// <param name="versionId">Id of the version to retrieve</param>
        /// <returns>An <see cref="IMedia"/> item</returns>
        public IMedia GetByVersion(Guid versionId)
        {
            return WithReadLocked(repository => repository.GetByVersion(versionId));
        }

        /// <summary>
        /// Gets a collection of an <see cref="IMedia"/> objects versions by Id
        /// </summary>
        /// <param name="id"></param>
        /// <returns>An Enumerable list of <see cref="IMedia"/> objects</returns>
        public IEnumerable<IMedia> GetVersions(int id)
        {
            return WithReadLocked(repository => repository.GetAllVersions(id));
        }

        /// <summary>
        /// Gets a collection of <see cref="IMedia"/> objects, which are ancestors of the current media.
        /// </summary>
        /// <param name="id">Id of the <see cref="IMedia"/> to retrieve ancestors for</param>
        /// <returns>An Enumerable list of <see cref="IMedia"/> objects</returns>
        public IEnumerable<IMedia> GetAncestors(int id)
        {
            var media = GetById(id);
            return GetAncestors(media);
        }

        /// <summary>
        /// Gets a collection of <see cref="IMedia"/> objects, which are ancestors of the current media.
        /// </summary>
        /// <param name="media"><see cref="IMedia"/> to retrieve ancestors for</param>
        /// <returns>An Enumerable list of <see cref="IMedia"/> objects</returns>
        public IEnumerable<IMedia> GetAncestors(IMedia media)
        {
            var rootId = Constants.System.Root.ToInvariantString();
            var ids = media.Path.Split(',')
                .Where(x => x != rootId && x != media.Id.ToString(CultureInfo.InvariantCulture)).Select(int.Parse).ToArray();
            if (ids.Any() == false)
                return new List<IMedia>();

            return WithReadLocked(repository => repository.GetAll(ids));
        }

        /// <summary>
        /// Gets a collection of <see cref="IMedia"/> objects by Parent Id
        /// </summary>
        /// <param name="id">Id of the Parent to retrieve Children from</param>
        /// <returns>An Enumerable list of <see cref="IMedia"/> objects</returns>
        public IEnumerable<IMedia> GetChildren(int id)
        {
            var query = Query<IMedia>.Builder.Where(x => x.ParentId == id);
            return WithReadLocked(repository => repository.GetByQuery(query).OrderBy(x => x.SortOrder));
        }

        [Obsolete("Use the overload with 'long' parameter types instead")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public IEnumerable<IMedia> GetPagedChildren(int id, int pageIndex, int pageSize, out int totalChildren, string orderBy, Direction orderDirection, string filter = "")
        {
            long totalChildren2;
            var ret = GetPagedChildren(id, Convert.ToInt64(pageIndex), pageSize, out totalChildren2, orderBy, orderDirection, true, filter);
            totalChildren = Convert.ToInt32(totalChildren2);
            return ret;
        }

        /// <summary>
        /// Gets a collection of <see cref="IMedia"/> objects by Parent Id
        /// </summary>
        /// <param name="id">Id of the Parent to retrieve Children from</param>
        /// <param name="pageIndex">Page index (zero based)</param>
        /// <param name="pageSize">Page size</param>
        /// <param name="totalChildren">Total records query would return without paging</param>
        /// <param name="orderBy">Field to order by</param>
        /// <param name="orderDirection">Direction to order by</param>
        /// <param name="filter">Search text filter</param>
        /// <returns>An Enumerable list of <see cref="IMedia"/> objects</returns>
        public IEnumerable<IMedia> GetPagedChildren(int id, long pageIndex, int pageSize, out long totalChildren, string orderBy, Direction orderDirection, string filter = "")
        {
            return GetPagedChildren(id, pageIndex, pageSize, out totalChildren, orderBy, orderDirection, true, filter);
        }

        /// <summary>
        /// Gets a collection of <see cref="IMedia"/> objects by Parent Id
        /// </summary>
        /// <param name="id">Id of the Parent to retrieve Children from</param>
        /// <param name="pageIndex">Page index (zero based)</param>
        /// <param name="pageSize">Page size</param>
        /// <param name="totalChildren">Total records query would return without paging</param>
        /// <param name="orderBy">Field to order by</param>
        /// <param name="orderDirection">Direction to order by</param>
        /// <param name="orderBySystemField">Flag to indicate when ordering by system field</param>
        /// <param name="filter">Search text filter</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IMedia> GetPagedChildren(int id, long pageIndex, int pageSize, out long totalChildren,
            string orderBy, Direction orderDirection, bool orderBySystemField, string filter)
        {
            Mandate.ParameterCondition(pageIndex >= 0, "pageIndex");
            Mandate.ParameterCondition(pageSize > 0, "pageSize");

            var query = Query<IMedia>.Builder;
            query.Where(x => x.ParentId == id);

            IEnumerable<IMedia> ret = null;
            long totalChildren2 = 0;
            WithReadLocked(repository =>
            {
                ret = repository.GetPagedResultsByQuery(query, pageIndex, pageSize, out totalChildren2, orderBy, orderDirection, orderBySystemField, filter);
            });
            totalChildren = totalChildren2;
            return ret;
        }

        [Obsolete("Use the overload with 'long' parameter types instead")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public IEnumerable<IMedia> GetPagedDescendants(int id, int pageIndex, int pageSize, out int totalChildren, string orderBy = "Path", Direction orderDirection = Direction.Ascending, string filter = "")
        {
            long total;
            var result = GetPagedDescendants(id, Convert.ToInt64(pageIndex), pageSize, out total, orderBy, orderDirection, true, filter);
            totalChildren = Convert.ToInt32(total);
            return result;
        }

        /// <summary>
        /// Gets a collection of <see cref="IMedia"/> objects by Parent Id
        /// </summary>
        /// <param name="id">Id of the Parent to retrieve Descendants from</param>
        /// <param name="pageIndex">Page number</param>
        /// <param name="pageSize">Page size</param>
        /// <param name="totalChildren">Total records query would return without paging</param>
        /// <param name="orderBy">Field to order by</param>
        /// <param name="orderDirection">Direction to order by</param>
        /// <param name="filter">Search text filter</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IMedia> GetPagedDescendants(int id, long pageIndex, int pageSize, out long totalChildren, string orderBy = "Path", Direction orderDirection = Direction.Ascending, string filter = "")
        {
            return GetPagedDescendants(id, pageIndex, pageSize, out totalChildren, orderBy, orderDirection, true, filter);
        }

        /// <summary>
        /// Gets a collection of <see cref="IMedia"/> objects by Parent Id
        /// </summary>
        /// <param name="id">Id of the Parent to retrieve Descendants from</param>
        /// <param name="pageIndex">Page number</param>
        /// <param name="pageSize">Page size</param>
        /// <param name="totalChildren">Total records query would return without paging</param>
        /// <param name="orderBy">Field to order by</param>
        /// <param name="orderDirection">Direction to order by</param>
        /// <param name="orderBySystemField">A value indicating whether the field to order by is a system field, or a user field.</param>
        /// <param name="filter">Search text filter</param>
        /// <returns>An Enumerable list of <see cref="IMedia"/> objects</returns>
        public IEnumerable<IMedia> GetPagedDescendants(int id, long pageIndex, int pageSize, out long totalChildren, string orderBy, Direction orderDirection, bool orderBySystemField, string filter)
        {
            Mandate.ParameterCondition(pageIndex >= 0, "pageIndex");
            Mandate.ParameterCondition(pageSize > 0, "pageSize");

            var query = Query<IMedia>.Builder;
            //if the id is -1, then just get all
            if (id != Constants.System.Root)
                query.Where(x => x.Path.SqlContains(string.Format(",{0},", id), TextColumnType.NVarchar));
            
            IEnumerable<IMedia> ret = null;
            long totalChildren2 = 0;
            WithReadLocked(repository =>
            {
                ret = repository.GetPagedResultsByQuery(query, pageIndex, pageSize, out totalChildren2, orderBy, orderDirection, orderBySystemField, filter);
            });
            totalChildren = totalChildren2;
            return ret;
        }

        /// <summary>
        /// Gets descendants of a <see cref="IMedia"/> object by its Id
        /// </summary>
        /// <param name="id">Id of the Parent to retrieve descendants from</param>
        /// <returns>An Enumerable flat list of <see cref="IMedia"/> objects</returns>
        public IEnumerable<IMedia> GetDescendants(int id)
        {
            var media = GetById(id);
            return media == null ? Enumerable.Empty<IMedia>() : GetDescendants(media);
        }

        /// <summary>
        /// Gets descendants of a <see cref="IMedia"/> object by its Id
        /// </summary>
        /// <param name="media">The Parent <see cref="IMedia"/> object to retrieve descendants from</param>
        /// <returns>An Enumerable flat list of <see cref="IMedia"/> objects</returns>
        public IEnumerable<IMedia> GetDescendants(IMedia media)
        {
            var pathMatch = media.Path + ",";
            var query = Query<IMedia>.Builder.Where(x => x.Id != media.Id && x.Path.StartsWith(pathMatch));
            return WithReadLocked(repository => repository.GetByQuery(query));
        }

        /// <summary>
        /// Gets the parent of the current media as an <see cref="IMedia"/> item.
        /// </summary>
        /// <param name="id">Id of the <see cref="IMedia"/> to retrieve the parent from</param>
        /// <returns>Parent <see cref="IMedia"/> object</returns>
        public IMedia GetParent(int id)
        {
            var media = GetById(id);
            return GetParent(media);
        }

        /// <summary>
        /// Gets the parent of the current media as an <see cref="IMedia"/> item.
        /// </summary>
        /// <param name="media"><see cref="IMedia"/> to retrieve the parent from</param>
        /// <returns>Parent <see cref="IMedia"/> object</returns>
        public IMedia GetParent(IMedia media)
        {
            if (media.ParentId == Constants.System.Root || media.ParentId == Constants.System.RecycleBinMedia)
                return null;

            return GetById(media.ParentId);
        }

        /// <summary>
        /// Gets a collection of <see cref="IMedia"/> objects, which reside at the first level / root
        /// </summary>
        /// <returns>An Enumerable list of <see cref="IMedia"/> objects</returns>
        public IEnumerable<IMedia> GetRootMedia()
        {
            var query = Query<IMedia>.Builder.Where(x => x.ParentId == -1);
            return WithReadLocked(repository => repository.GetByQuery(query));
        }

        /// <summary>
        /// Gets a collection of an <see cref="IMedia"/> objects, which resides in the Recycle Bin
        /// </summary>
        /// <returns>An Enumerable list of <see cref="IMedia"/> objects</returns>
        public IEnumerable<IMedia> GetMediaInRecycleBin()
        {
            var query = Query<IMedia>.Builder.Where(x => x.Path.Contains(Constants.System.RecycleBinMedia.ToInvariantString()));
            return WithReadLocked(repository => repository.GetByQuery(query));
        }

        /// <summary>
        /// Gets an <see cref="IMedia"/> object from the path stored in the 'umbracoFile' property.
        /// </summary>
        /// <param name="mediaPath">Path of the media item to retrieve (for example: /media/1024/koala_403x328.jpg)</param>
        /// <returns><see cref="IMedia"/></returns>
        public IMedia GetMediaByPath(string mediaPath)
        {
            var umbracoFileValue = mediaPath;
            const string pattern = ".*[_][0-9]+[x][0-9]+[.].*";
            var isResized = Regex.IsMatch(mediaPath, pattern);

            // If the image has been resized we strip the "_403x328" of the original "/media/1024/koala_403x328.jpg" url.
            if (isResized)
            {
                var underscoreIndex = mediaPath.LastIndexOf('_');
                var dotIndex = mediaPath.LastIndexOf('.');
                umbracoFileValue = string.Concat(mediaPath.Substring(0, underscoreIndex), mediaPath.Substring(dotIndex));
            }

            Func<string, Sql> createSql = url => new Sql().Select("*")
                                                  .From<PropertyDataDto>()
                                                  .InnerJoin<PropertyTypeDto>()
                                                  .On<PropertyDataDto, PropertyTypeDto>(left => left.PropertyTypeId, right => right.Id)
                                                  .Where<PropertyTypeDto>(x => x.Alias == "umbracoFile")
                                                  .Where<PropertyDataDto>(x => x.VarChar == url);

            var sql = createSql(umbracoFileValue);

            return WithReadLocked(repository =>
            {
                var database = repository.UnitOfWork.Database;

                var propertyDataDto = database.Fetch<PropertyDataDto, PropertyTypeDto>(sql).FirstOrDefault();

                // If the stripped-down url returns null, we try again with the original url. 
                // Previously, the function would fail on e.g. "my_x_image.jpg"
                if (propertyDataDto == null)
                {
                    sql = createSql(mediaPath);
                    propertyDataDto = database.Fetch<PropertyDataDto, PropertyTypeDto>(sql).FirstOrDefault();
                }

                return propertyDataDto == null ? null : GetById(propertyDataDto.NodeId);
            });
        }

        /// <summary>
        /// Checks whether an <see cref="IMedia"/> item has any children
        /// </summary>
        /// <param name="id">Id of the <see cref="IMedia"/></param>
        /// <returns>True if the media has any children otherwise False</returns>
        public bool HasChildren(int id)
        {
            var query = Query<IMedia>.Builder.Where(x => x.ParentId == id);
            return WithReadLocked(repository => repository.Count(query) > 0);
        }

        #endregion

        #region Save

        /// <summary>
        /// Saves a single <see cref="IMedia"/> object
        /// </summary>
        /// <param name="media">The <see cref="IMedia"/> to save</param>
        /// <param name="userId">Id of the User saving the Media</param>
        /// <param name="raiseEvents">Optional boolean indicating whether or not to raise events.</param>
        public void Save(IMedia media, int userId = 0, bool raiseEvents = true)
        {
            ((IMediaServiceOperations)this).Save (media, userId, raiseEvents);
        }
        
        /// <summary>
        /// Saves a single <see cref="IMedia"/> object
        /// </summary>
        /// <param name="media">The <see cref="IMedia"/> to save</param>
        /// <param name="userId">Id of the User saving the Media</param>
        /// <param name="raiseEvents">Optional boolean indicating whether or not to raise events.</param>
        Attempt<OperationStatus> IMediaServiceOperations.Save(IMedia media, int userId, bool raiseEvents)
        {
            var evtMsgs = EventMessagesFactory.Get();

            if (raiseEvents && Saving.IsRaisedEventCancelled(new SaveEventArgs<IMedia>(media, evtMsgs), this))
                return OperationStatus.Cancelled(evtMsgs);
            var isNew = media.IsNewEntity();

            WithWriteLocked(repository =>
            {
                media.CreatorId = userId;
                repository.AddOrUpdate(media);
            });

            if (raiseEvents)
                Saved.RaiseEvent(new SaveEventArgs<IMedia>(media, false, evtMsgs), this);
            var changeType = isNew ? TreeChangeTypes.RefreshBranch : TreeChangeTypes.RefreshNode;
            using (ChangeSet.WithAmbient)
                TreeChanged.RaiseEvent(new TreeChange<IMedia>(media, changeType).ToEventArgs(), this);
            Audit(AuditType.Save, "Save Media performed by user", userId, media.Id);
            return OperationStatus.Success(evtMsgs);
        }

        /// <summary>
        /// Saves a collection of <see cref="IMedia"/> objects
        /// </summary>
        /// <param name="medias">Collection of <see cref="IMedia"/> to save</param>
        /// <param name="userId">Id of the User saving the Media</param>
        /// <param name="raiseEvents">Optional boolean indicating whether or not to raise events.</param>
        public void Save(IEnumerable<IMedia> medias, int userId = 0, bool raiseEvents = true)
        {
            ((IMediaServiceOperations)this).Save(medias, userId, raiseEvents);
        }
        
        /// <summary>
        /// Saves a collection of <see cref="IMedia"/> objects
        /// </summary>
        /// <param name="medias">Collection of <see cref="IMedia"/> to save</param>
        /// <param name="userId">Id of the User saving the Media</param>
        /// <param name="raiseEvents">Optional boolean indicating whether or not to raise events.</param>
        Attempt<OperationStatus> IMediaServiceOperations.Save(IEnumerable<IMedia> medias, int userId, bool raiseEvents)
        {
            var mediasA = medias.ToArray();

            var evtMsgs = EventMessagesFactory.Get();
            if (raiseEvents && Saving.IsRaisedEventCancelled(new SaveEventArgs<IMedia>(mediasA, evtMsgs), this))
                return OperationStatus.Cancelled(evtMsgs);

            var treeChanges = mediasA.Select(x => new TreeChange<IMedia>(x,
                x.IsNewEntity() ? TreeChangeTypes.RefreshBranch : TreeChangeTypes.RefreshNode));

            WithWriteLocked(repository =>
            {
                foreach (var media in mediasA)
                {
                    media.CreatorId = userId;
                    repository.AddOrUpdate(media);
                }
            });

            if (raiseEvents)
                Saved.RaiseEvent(new SaveEventArgs<IMedia>(mediasA, false, evtMsgs), this);
            using (ChangeSet.WithAmbient)
                TreeChanged.RaiseEvent(treeChanges.ToEventArgs(), this);
            Audit(AuditType.Save, "Save Media items performed by user", userId, -1);
            return OperationStatus.Success(evtMsgs);
        }

        #endregion

        #region Delete

        /// <summary>
        /// Permanently deletes an <see cref="IMedia"/> object as well as all of its Children.
        /// </summary>
        /// <remarks>
        /// Please note that this method will completely remove the Media from the database,
        /// as well as associated media files from the file system.
        /// </remarks>
        /// <param name="media">The <see cref="IMedia"/> to delete</param>
        /// <param name="userId">Id of the User deleting the Media</param>
        public void Delete(IMedia media, int userId = 0)
        {
            ((IMediaServiceOperations) this).Delete(media, userId);
        }
        
        /// <summary>
        /// Permanently deletes an <see cref="IMedia"/> object as well as all of its Children.
        /// </summary>
        /// <remarks>
        /// Please note that this method will completely remove the Media from the database,
        /// as well as associated media files from the file system.
        /// </remarks>
        /// <param name="media">The <see cref="IMedia"/> to delete</param>
        /// <param name="userId">Id of the User deleting the Media</param>
        Attempt<OperationStatus> IMediaServiceOperations.Delete(IMedia media, int userId)
        {
            var evtMsgs = EventMessagesFactory.Get();

            if (Deleting.IsRaisedEventCancelled(new DeleteEventArgs<IMedia>(media, evtMsgs), this))
                return OperationStatus.Cancelled(evtMsgs);

            using (ChangeSet.WithAmbient)
            {
                WithWriteLocked(repository => DeleteLocked(media, repository, evtMsgs));
                TreeChanged.RaiseEvent(new TreeChange<IMedia>(media, TreeChangeTypes.Remove).ToEventArgs(), this);
            }

            Audit(AuditType.Delete, "Delete Media performed by user", userId, media.Id);
            return OperationStatus.Success(evtMsgs);
        }

        private void DeleteLocked(IMedia media, IMediaRepository repository, EventMessages evtMsgs)
        {
            // then recursively delete descendants, bottom-up
            // just repository.Delete + an event
            var stack = new Stack<IMedia>();
            stack.Push(media);
            var level = 1;
            while (stack.Count > 0)
            {
                var c = stack.Peek();
                IMedia[] cc;
                if (c.Level == level)
                    while ((cc = c.Children().ToArray()).Length > 0)
                    {
                        foreach (var ci in cc)
                            stack.Push(ci);
                        c = cc[cc.Length - 1];
                    }
                c = stack.Pop();
                level = c.Level;

                repository.Delete(c);
                var args = new DeleteEventArgs<IMedia>(c, false, evtMsgs); // raise event & get flagged files
                Deleted.RaiseEvent(args, this);
                IOHelper.DeleteFiles(args.MediaFilesToDelete, // remove flagged files
                    (file, e) => Logger.Error<MemberService>("An error occurred while deleting file attached to nodes: " + file, e));
            }
        }

        //TODO:
        // both DeleteVersions methods below have an issue. Sort of. They do NOT take care of files the way
        // Delete does - for a good reason: the file may be referenced by other, non-deleted, versions. BUT,
        // if that's not the case, then the file will never be deleted, because when we delete the media,
        // the version referencing the file will not be there anymore. SO, we can leak files.

        /// <summary>
        /// Permanently deletes versions from an <see cref="IMedia"/> object prior to a specific date.
        /// This method will never delete the latest version of a media item.
        /// </summary>
        /// <param name="id">Id of the <see cref="IMedia"/> object to delete versions from</param>
        /// <param name="versionDate">Latest version date</param>
        /// <param name="userId">Optional Id of the User deleting versions of a Media object</param>
        public void DeleteVersions(int id, DateTime versionDate, int userId = 0)
        {
            if (DeletingVersions.IsRaisedEventCancelled(new DeleteRevisionsEventArgs(id, dateToRetain: versionDate), this))
                return;

            WithWriteLocked(repository => repository.DeleteVersions(id, versionDate));

            DeletedVersions.RaiseEvent(new DeleteRevisionsEventArgs(id, false, dateToRetain: versionDate), this);
            Audit(AuditType.Delete, "Delete Media by version date performed by user", userId, Constants.System.Root);
        }

        /// <summary>
        /// Permanently deletes specific version(s) from an <see cref="IMedia"/> object.
        /// This method will never delete the latest version of a media item.
        /// </summary>
        /// <param name="id">Id of the <see cref="IMedia"/> object to delete a version from</param>
        /// <param name="versionId">Id of the version to delete</param>
        /// <param name="deletePriorVersions">Boolean indicating whether to delete versions prior to the versionId</param>
        /// <param name="userId">Optional Id of the User deleting versions of a Media object</param>
        public void DeleteVersion(int id, Guid versionId, bool deletePriorVersions, int userId = 0)
        {
            if (DeletingVersions.IsRaisedEventCancelled(new DeleteRevisionsEventArgs(id, /*specificVersion:*/ versionId), this))
                return;

            WithWriteLocked(repository =>
            {
                if (deletePriorVersions)
                {
                    //var media = repository.GetByVersion(versionId);
                    //repository.DeleteVersions(id, media.UpdateDate);

                    var media = GetByVersion(versionId);
                    DeleteVersions(id, media.UpdateDate, userId);
                }

                repository.DeleteVersion(versionId);
            });

            DeletedVersions.RaiseEvent(new DeleteRevisionsEventArgs(id, false, /*specificVersion:*/ versionId), this);
            Audit(AuditType.Delete, "Delete Media by version performed by user", userId, -1);
        }

        #endregion

        #region Move, RecycleBin

        /// <summary>
        /// Deletes an <see cref="IMedia"/> object by moving it to the Recycle Bin
        /// </summary>
        /// <param name="media">The <see cref="IMedia"/> to delete</param>
        /// <param name="userId">Id of the User deleting the Media</param>
        public void MoveToRecycleBin(IMedia media, int userId = 0)
        {
            ((IMediaServiceOperations) this).MoveToRecycleBin(media, userId);
        }

        /// <summary>
        /// Deletes an <see cref="IMedia"/> object by moving it to the Recycle Bin
        /// </summary>
        /// <param name="media">The <see cref="IMedia"/> to delete</param>
        /// <param name="userId">Id of the User deleting the Media</param>
        Attempt<OperationStatus> IMediaServiceOperations.MoveToRecycleBin(IMedia media, int userId)
        {
            var moves = new List<Tuple<IMedia, string>>();
            var evtMsgs = EventMessagesFactory.Get();
            var returnAttempt = false;
            var attempt = default(Attempt<OperationStatus>);

            using (ChangeSet.WithAmbient)
            {
                WithWriteLocked(repository =>
                {
                    var originalPath = media.Path;

                    if (Trashing.IsRaisedEventCancelled(new MoveEventArgs<IMedia>(
                        new MoveEventInfo<IMedia>(media, originalPath, Constants.System.RecycleBinMedia)), this))
                    {
                        attempt = OperationStatus.Cancelled(evtMsgs);
                        returnAttempt = true;
                        return;
                    }

                    PerformMoveLocked(media, Constants.System.RecycleBinMedia, null, userId, moves, true, repository);

                    TreeChanged.RaiseEvent(new TreeChange<IMedia>(media, TreeChangeTypes.RefreshBranch).ToEventArgs(), this);
                });
            }

            if (returnAttempt)
                return attempt;

            var moveInfo = moves
                .Select(x => new MoveEventInfo<IMedia>(x.Item1, x.Item2, x.Item1.ParentId))
                .ToArray();
            Trashed.RaiseEvent(new MoveEventArgs<IMedia>(false, evtMsgs, moveInfo), this);
            Audit(AuditType.Move, "Move Media to Recycle Bin performed by user", userId, media.Id);
            
            return OperationStatus.Success(evtMsgs);
        }

        /// <summary>
        /// Moves an <see cref="IMedia"/> object to a new location
        /// </summary>
        /// <param name="media">The <see cref="IMedia"/> to move</param>
        /// <param name="parentId">Id of the Media's new Parent</param>
        /// <param name="userId">Id of the User moving the Media</param>
        public void Move(IMedia media, int parentId, int userId = 0)
        {
            // if moving to the recycle bin then use the proper method
            if (parentId == Constants.System.RecycleBinMedia)
            {
                MoveToRecycleBin(media, userId);
                return;
            }

            var moves = new List<Tuple<IMedia, string>>();

            using (ChangeSet.WithAmbient)
            {
                WithWriteLocked(repository =>
                {
                    var parent = parentId == Constants.System.Root ? null : GetById(parentId);
                    if (parentId != Constants.System.Root && (parent == null || parent.Trashed))
                        throw new InvalidOperationException("Parent does not exist or is trashed.");

                    if (Moving.IsRaisedEventCancelled(new MoveEventArgs<IMedia>(
                            new MoveEventInfo<IMedia>(media, media.Path, parentId)), this))
                        return;

                    // if media was trashed, and since we're not moving to the recycle bin,
                    // indicate that the trashed status should be changed to false, else just
                    // leave it unchanged
                    var trashed = media.Trashed ? false : (bool?)null;

                    PerformMoveLocked(media, parentId, parent, userId, moves, trashed, repository);

                    TreeChanged.RaiseEvent(new TreeChange<IMedia>(media, TreeChangeTypes.RefreshBranch).ToEventArgs(), this);
                });
            }

            var moveInfo = moves //changes
                .Select(x => new MoveEventInfo<IMedia>(x.Item1, x.Item2, x.Item1.ParentId))
                .ToArray();
            Moved.RaiseEvent(new MoveEventArgs<IMedia>(false, moveInfo), this);
            Audit(AuditType.Move, "Move Media performed by user", userId, media.Id);
        }

        // MUST be called from within WriteLock
        // trash indicates whether we are trashing, un-trashing, or not changing anything
        private void PerformMoveLocked(IMedia media, int parentId, IMedia parent, int userId,
            ICollection<Tuple<IMedia, string>> moves,
            bool? trash, IMediaRepository repository)
        {
            media.ParentId = parentId;

            // get the level delta (old pos to new pos)
            var levelDelta = parent == null
                ? 1 - media.Level + (parentId == Constants.System.RecycleBinMedia ? 1 : 0)
                : parent.Level + 1 - media.Level;

            var paths = new Dictionary<int, string>();

            moves.Add(Tuple.Create(media, media.Path)); // capture original path

            // these will be updated by the repo because we changed parentId
            //media.Path = (parent == null ? "-1" : parent.Path) + "," + media.Id;
            //media.SortOrder = ((MediaRepository) repository).NextChildSortOrder(parentId);
            //media.Level += levelDelta;
            PerformMoveMedia(repository, media, userId, trash);

            // BUT media.Path will be updated only when the UOW commits, and
            //  because we want it now, we have to calculate it by ourselves
            //paths[media.Id] = media.Path;
            paths[media.Id] = (parent == null ? (parentId == Constants.System.RecycleBinMedia ? "-1,-21" : "-1") : parent.Path) + "," + media.Id;

            var descendants = GetDescendants(media);
            foreach (var descendant in descendants)
            {
                moves.Add(Tuple.Create(descendant, descendant.Path)); // capture original path

                // update path and level since we do not update parentId
                descendant.Path = paths[descendant.Id] = paths[descendant.ParentId] + "," + descendant.Id;
                descendant.Level += levelDelta;
                PerformMoveMedia(repository, descendant, userId, trash);
            }
        }

        private static void PerformMoveMedia(IMediaRepository repository, IMedia media, int userId,
            bool? trash)
        {
            if (trash.HasValue) ((ContentBase) media).Trashed = trash.Value;
            repository.AddOrUpdate(media);
        }

        /// <summary>
        /// Empties the Recycle Bin by deleting all <see cref="IMedia"/> that resides in the bin
        /// </summary>
        public void EmptyRecycleBin()
        {
            var nodeObjectType = new Guid(Constants.ObjectTypes.Media);
            var deleted = new List<IMedia>();
            var evtMsgs = EventMessagesFactory.Get();

            using (ChangeSet.WithAmbient)
            {
                WithWriteLocked(repository =>
                {
                    // no idea what those events are for, keep a simplified version
                    if (EmptyingRecycleBin.IsRaisedEventCancelled(new RecycleBinEventArgs(nodeObjectType), this))
                        return;

                    // emptying the recycle bin means deleting whetever is in there - do it properly!
                    var query = Query<IMedia>.Builder.Where(x => x.ParentId == Constants.System.RecycleBinMedia);
                    var medias = repository.GetByQuery(query).ToArray();
                    foreach (var media in medias)
                    {
                        DeleteLocked(media, repository, evtMsgs);
                        deleted.Add(media);
                    }
                });

                EmptiedRecycleBin.RaiseEvent(new RecycleBinEventArgs(nodeObjectType, true), this);
                TreeChanged.RaiseEvent(deleted.Select(x => new TreeChange<IMedia>(x, TreeChangeTypes.Remove)).ToEventArgs(), this);
            }

            Audit(AuditType.Delete, "Empty Media Recycle Bin performed by user", 0, Constants.System.RecycleBinMedia);
        }

        #endregion

        #region Others

        /// <summary>
        /// Sorts a collection of <see cref="IMedia"/> objects by updating the SortOrder according
        /// to the ordering of items in the passed in <see cref="IEnumerable{T}"/>.
        /// </summary>
        /// <param name="items"></param>
        /// <param name="userId"></param>
        /// <param name="raiseEvents"></param>
        /// <returns>True if sorting succeeded, otherwise False</returns>
        public bool Sort(IEnumerable<IMedia> items, int userId = 0, bool raiseEvents = true)
        {
            var itemsA = items.ToArray();
            if (itemsA.Length == 0) return true;

            //TODO:
            // firing Saving for all the items, but we're not going to save those that are already
            // correctly ordered, so we're not going to fire Saved for all the items, and that's not
            // really consistent - but the only way to be consistent would be to first check which
            // items we're going to save, then trigger the events... within the UOW transaction...
            // which is not something we want to do, so what?
            if (raiseEvents && Saving.IsRaisedEventCancelled(new SaveEventArgs<IMedia>(itemsA), this))
                return false;

            var saved = new List<IMedia>();

            using (ChangeSet.WithAmbient)
            {
                WithWriteLocked(repository =>
                {
                    var sortOrder = 0;
                    foreach (var media in itemsA)
                    {
                        // if the current sort order equals that of the media we don't
                        // need to update it, so just increment the sort order and continue.
                        if (media.SortOrder == sortOrder)
                        {
                            sortOrder++;
                            continue;
                        }

                        // else update
                        media.SortOrder = sortOrder++;

                        // save
                        saved.Add(media);
                        repository.AddOrUpdate(media);
                    }
                });

                if (raiseEvents)
                    Saved.RaiseEvent(new SaveEventArgs<IMedia>(itemsA, false), this);
                TreeChanged.RaiseEvent(saved.Select(x => new TreeChange<IMedia>(x, TreeChangeTypes.RefreshNode)).ToEventArgs(), this);
            }

            Audit(AuditType.Sort, "Sorting Media performed by user", userId, 0);
            return true;
        }

        #endregion

        #region Private Methods

        private void Audit(AuditType type, string message, int userId, int objectId)
        {
            var uow = UowProvider.GetUnitOfWork();
            using (var auditRepo = RepositoryFactory.CreateAuditRepository(uow))
            {
                auditRepo.AddOrUpdate(new AuditItem(objectId, message, type, userId));
                uow.Commit();
            }
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Occurs before Delete
        /// </summary>		
        public static event TypedEventHandler<IMediaService, DeleteRevisionsEventArgs> DeletingVersions;

        /// <summary>
        /// Occurs after Delete
        /// </summary>
        public static event TypedEventHandler<IMediaService, DeleteRevisionsEventArgs> DeletedVersions;

        /// <summary>
        /// Occurs before Delete
        /// </summary>
        public static event TypedEventHandler<IMediaService, DeleteEventArgs<IMedia>> Deleting;

        /// <summary>
        /// Occurs after Delete
        /// </summary>
        public static event TypedEventHandler<IMediaService, DeleteEventArgs<IMedia>> Deleted;

        /// <summary>
        /// Occurs before Save
        /// </summary>
        public static event TypedEventHandler<IMediaService, SaveEventArgs<IMedia>> Saving;

        /// <summary>
        /// Occurs after Save
        /// </summary>
        public static event TypedEventHandler<IMediaService, SaveEventArgs<IMedia>> Saved;

        /// <summary>
        /// Occurs before Create
        /// </summary>
        [Obsolete("Use the Created event instead, the Creating and Created events both offer the same functionality, Creating event has been deprecated.")]
        public static event TypedEventHandler<IMediaService, NewEventArgs<IMedia>> Creating;

        /// <summary>
        /// Occurs after Create
        /// </summary>
        /// <remarks>
        /// Please note that the Media object has been created, but not saved
        /// so it does not have an identity yet (meaning no Id has been set).
        /// </remarks>
        public static event TypedEventHandler<IMediaService, NewEventArgs<IMedia>> Created;

        /// <summary>
        /// Occurs before Media is moved to Recycle Bin
        /// </summary>
        public static event TypedEventHandler<IMediaService, MoveEventArgs<IMedia>> Trashing;

        /// <summary>
        /// Occurs after Media is moved to Recycle Bin
        /// </summary>
        public static event TypedEventHandler<IMediaService, MoveEventArgs<IMedia>> Trashed;

        /// <summary>
        /// Occurs before Move
        /// </summary>
        public static event TypedEventHandler<IMediaService, MoveEventArgs<IMedia>> Moving;

        /// <summary>
        /// Occurs after Move
        /// </summary>
        public static event TypedEventHandler<IMediaService, MoveEventArgs<IMedia>> Moved;

        /// <summary>
        /// Occurs before the Recycle Bin is emptied
        /// </summary>
        public static event TypedEventHandler<IMediaService, RecycleBinEventArgs> EmptyingRecycleBin;

        /// <summary>
        /// Occurs after the Recycle Bin has been Emptied
        /// </summary>
        public static event TypedEventHandler<IMediaService, RecycleBinEventArgs> EmptiedRecycleBin;

        /// <summary>
        /// Occurs after change.
        /// </summary>
        internal static event TypedEventHandler<IMediaService, TreeChange<IMedia>.EventArgs> TreeChanged;

        #endregion

        #region Content Types

        /// <summary>
        /// Deletes all media of specified type. All children of deleted media is moved to Recycle Bin.
        /// </summary>
        /// <remarks>This needs extra care and attention as its potentially a dangerous and extensive operation</remarks>
        /// <param name="mediaTypeId">Id of the <see cref="IMediaType"/></param>
        /// <param name="userId">Optional id of the user deleting the media</param>
        public void DeleteMediaOfType(int mediaTypeId, int userId = 0)
        {
            var changes = new List<TreeChange<IMedia>>();
            var moves = new List<Tuple<IMedia, string>>();
            var evtMsgs = EventMessagesFactory.Get();

            using (ChangeSet.WithAmbient)
            {
                WithWriteLocked(repository =>
                {
                    var query = Query<IMedia>.Builder.Where(x => x.ContentTypeId == mediaTypeId);
                    var medias = repository.GetByQuery(query).ToArray();

                    if (Deleting.IsRaisedEventCancelled(new DeleteEventArgs<IMedia>(medias, evtMsgs), this))
                        return;

                    // order by level, descending, so deepest first - that way, we cannot move
                    // a media of the deleted type, to the recycle bin (and then delete it...)
                    foreach (var media in medias.OrderByDescending(x => x.Level))
                    {
                        // if current media has children, move them to trash
                        var m = media;
                        var childQuery = Query<IMedia>.Builder.Where(x => x.Path.StartsWith(m.Path));
                        var children = repository.GetByQuery(childQuery);
                        foreach (var child in children.Where(x => x.ContentTypeId != mediaTypeId))
                        {
                            // see MoveToRecycleBin
                            PerformMoveLocked(child, Constants.System.RecycleBinMedia, null, userId, moves, true, repository);
                            changes.Add(new TreeChange<IMedia>(media, TreeChangeTypes.RefreshBranch));
                        }

                        // delete media
                        // triggers the deleted event (and handles the files)
                        DeleteLocked(media, repository, evtMsgs);
                        changes.Add(new TreeChange<IMedia>(media, TreeChangeTypes.Remove));
                    }
                });

                var moveInfos = moves
                    .Select(x => new MoveEventInfo<IMedia>(x.Item1, x.Item2, x.Item1.ParentId))
                    .ToArray();
                if (moveInfos.Length > 0)
                    Trashed.RaiseEvent(new MoveEventArgs<IMedia>(false, moveInfos), this);
                TreeChanged.RaiseEvent(changes.ToEventArgs(), this);
            }

            Audit(AuditType.Delete,
                string.Format("Delete Media of Type {0} performed by user", mediaTypeId),
                userId, Constants.System.Root);
        }

        private IMediaType GetMediaType(string mediaTypeAlias)
        {
            var mediaType = MediaTypeService.Get(mediaTypeAlias);
            if (mediaType == null)
                throw new Exception(string.Format("No MediaType matching alias: \"{0}\".", mediaTypeAlias));
            return mediaType;
        }

        #endregion
    }
}
