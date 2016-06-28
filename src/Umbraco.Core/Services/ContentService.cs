using System;
using System.Collections.Generic;
using System.Data;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using Umbraco.Core.Cache;
using Umbraco.Core.Configuration;
using Umbraco.Core.Events;
using Umbraco.Core.IO;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Models.Membership;
using Umbraco.Core.Persistence;
using Umbraco.Core.Persistence.DatabaseModelDefinitions;
using Umbraco.Core.Persistence.Querying;
using Umbraco.Core.Persistence.Repositories;
using Umbraco.Core.Persistence.SqlSyntax;
using Umbraco.Core.Persistence.UnitOfWork;
using Umbraco.Core.Publishing;

namespace Umbraco.Core.Services
{
    /// <summary>
    /// Represents the Content Service, which is an easy access to operations involving <see cref="IContent"/>
    /// </summary>
    public class ContentService : RepositoryService, IContentService, IContentServiceOperations
    {
        private IContentTypeService _contentTypeService;

        #region Constructors

        public ContentService(
            IDatabaseUnitOfWorkProvider provider,
            RepositoryFactory repositoryFactory,
            ILogger logger,
            IEventMessagesFactory eventMessagesFactory,
            IDataTypeService dataTypeService,
            IUserService userService)
            : base(provider, repositoryFactory, logger, eventMessagesFactory)
        {
            // though... these are not used?
            Mandate.ParameterNotNull(dataTypeService, "dataTypeService");
            Mandate.ParameterNotNull(userService, "userService");

            _lrepo = new LockingRepository<ContentRepository>(UowProvider,
                uow => RepositoryFactory.CreateContentRepository(uow) as ContentRepository,
                LockingRepositoryLockIds, LockingRepositoryLockIds);
        }

        internal IContentTypeService ContentTypeService
        {
            get
            {
                if (_contentTypeService == null)
                    throw new InvalidOperationException("ContentService.ContentTypeService has not been initialized.");
                return _contentTypeService;
            }
            set { _contentTypeService = value; }
        }

        #endregion

        #region Locking

        // constant
        private static readonly int[] LockingRepositoryLockIds = { Constants.System.ContentTreeLock };

        private readonly LockingRepository<ContentRepository> _lrepo;

        internal void WithReadLocked(Action<ContentRepository> action, bool autoCommit = true)
        {
            _lrepo.WithReadLocked(xr => action(xr.Repository), autoCommit);
        }

        internal TResult WithReadLocked<TResult>(Func<ContentRepository, TResult> func, bool autoCommit = true)
        {
            return _lrepo.WithReadLocked(xr => func(xr.Repository), autoCommit);
        }

        internal void WithWriteLocked(Action<ContentRepository> action, bool autoCommit = true)
        {
            _lrepo.WithWriteLocked(xr => action(xr.Repository), autoCommit);
        }

        #endregion

        #region Count

        public int CountPublished(string contentTypeAlias = null)
        {
            return _lrepo.WithReadLocked(xr => xr.Repository.CountPublished(contentTypeAlias));
        }

        public int Count(string contentTypeAlias = null)
        {
            return _lrepo.WithReadLocked(xr => xr.Repository.Count(contentTypeAlias));
        }

        public int CountChildren(int parentId, string contentTypeAlias = null)
        {
            return _lrepo.WithReadLocked(xr => xr.Repository.CountChildren(parentId, contentTypeAlias));
        }

        public int CountDescendants(int parentId, string contentTypeAlias = null)
        {
            return _lrepo.WithReadLocked(xr => xr.Repository.CountDescendants(parentId, contentTypeAlias));
        }

        #endregion

        #region Permissions

        /// <summary>
        /// Used to bulk update the permissions set for a content item. This will replace all permissions
        /// assigned to an entity with a list of user id & permission pairs.
        /// </summary>
        /// <param name="permissionSet"></param>
        public void ReplaceContentPermissions(EntityPermissionSet permissionSet)
        {
            _lrepo.WithWriteLocked(xr => xr.Repository.ReplaceContentPermissions(permissionSet));
        }

        /// <summary>
        /// Assigns a single permission to the current content item for the specified user ids
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="permission"></param>
        /// <param name="userIds"></param>
        public void AssignContentPermission(IContent entity, char permission, IEnumerable<int> userIds)
        {
            _lrepo.WithWriteLocked(xr => xr.Repository.AssignEntityPermission(entity, permission, userIds));
        }

        /// <summary>
        /// Gets the list of permissions for the content item
        /// </summary>
        /// <param name="content"></param>
        /// <returns></returns>
        public IEnumerable<EntityPermission> GetPermissionsForEntity(IContent content)
        {
            return _lrepo.WithWriteLocked(xr => xr.Repository.GetPermissionsForEntity(content.Id));
        }

        #endregion

        #region Create

        // NOTE so to create content without a parent, use the parentId value -1 but don't try a null parent
        // this all makes little sense and should be reorg in v8

        /// <summary>
        /// Creates an <see cref="IContent"/> object using the alias of the <see cref="IContentType"/>
        /// that this Content should based on.
        /// </summary>
        /// <remarks>
        /// Note that using this method will simply return a new IContent without any identity
        /// as it has not yet been persisted. It is intended as a shortcut to creating new content objects
        /// that does not invoke a save operation against the database.
        /// </remarks>
        /// <param name="name">Name of the Content object</param>
        /// <param name="parentId">Id of Parent for the new Content</param>
        /// <param name="contentTypeAlias">Alias of the <see cref="IContentType"/></param>
        /// <param name="userId">Optional id of the user creating the content</param>
        /// <returns><see cref="IContent"/></returns>
        public IContent CreateContent(string name, int parentId, string contentTypeAlias, int userId = 0)
        {
            var contentType = GetContentType(contentTypeAlias);
            var content = new Content(name, parentId, contentType);

            var parent = GetById(content.ParentId);
            content.Path = string.Concat(parent.IfNotNull(x => x.Path, content.ParentId.ToString()), ",", content.Id);

            CreateContent(content, null, parentId, false, userId, false);
            return content;
        }

        /// <summary>
        /// Creates an <see cref="IContent"/> object using the alias of the <see cref="IContentType"/>
        /// that this Content should based on.
        /// </summary>
        /// <remarks>
        /// Note that using this method will simply return a new IContent without any identity
        /// as it has not yet been persisted. It is intended as a shortcut to creating new content objects
        /// that does not invoke a save operation against the database.
        /// </remarks>
        /// <param name="name">Name of the Content object</param>
        /// <param name="parent">Parent <see cref="IContent"/> object for the new Content</param>
        /// <param name="contentTypeAlias">Alias of the <see cref="IContentType"/></param>
        /// <param name="userId">Optional id of the user creating the content</param>
        /// <returns><see cref="IContent"/></returns>
        public IContent CreateContent(string name, IContent parent, string contentTypeAlias, int userId = 0)
        {
            if (parent == null) throw new ArgumentNullException("parent");

            var contentType = GetContentType(contentTypeAlias);
            var content = new Content(name, parent, contentType);
            content.Path = string.Concat(parent.Path, ",", content.Id);
            CreateContent(content, parent, parent.Id, true, userId, false);
            return content;
        }

        /// <summary>
        /// Creates and saves an <see cref="IContent"/> object using the alias of the <see cref="IContentType"/>
        /// that this Content should based on.
        /// </summary>
        /// <remarks>
        /// This method returns an <see cref="IContent"/> object that has been persisted to the database
        /// and therefor has an identity.
        /// </remarks>
        /// <param name="name">Name of the Content object</param>
        /// <param name="parentId">Id of Parent for the new Content</param>
        /// <param name="contentTypeAlias">Alias of the <see cref="IContentType"/></param>
        /// <param name="userId">Optional id of the user creating the content</param>
        /// <returns><see cref="IContent"/></returns>
        public IContent CreateContentWithIdentity(string name, int parentId, string contentTypeAlias, int userId = 0)
        {
            var contentType = GetContentType(contentTypeAlias);
            var content = new Content(name, parentId, contentType);
            CreateContent(content, null, parentId, false, userId, true);
            return content;
        }

        /// <summary>
        /// Creates and saves an <see cref="IContent"/> object using the alias of the <see cref="IContentType"/>
        /// that this Content should based on.
        /// </summary>
        /// <remarks>
        /// This method returns an <see cref="IContent"/> object that has been persisted to the database
        /// and therefor has an identity.
        /// </remarks>
        /// <param name="name">Name of the Content object</param>
        /// <param name="parent">Parent <see cref="IContent"/> object for the new Content</param>
        /// <param name="contentTypeAlias">Alias of the <see cref="IContentType"/></param>
        /// <param name="userId">Optional id of the user creating the content</param>
        /// <returns><see cref="IContent"/></returns>
        public IContent CreateContentWithIdentity(string name, IContent parent, string contentTypeAlias, int userId = 0)
        {
            if (parent == null) throw new ArgumentNullException("parent");

            var contentType = GetContentType(contentTypeAlias);
            var content = new Content(name, parent, contentType);
            CreateContent(content, parent, parent.Id, true, userId, true);
            return content;
        }

        private void CreateContent(Content content, IContent parent, int parentId, bool withParent, int userId, bool withIdentity)
        {
            // NOTE: I really hate the notion of these Creating/Created events - they are so inconsistent, I've only just found
            // out that in these 'WithIdentity' methods, the Saving/Saved events were not fired, wtf. Anyways, they're added now.
            var newArgs = withParent
                ? new NewEventArgs<IContent>(content, content.ContentType.Alias, parent)
                : new NewEventArgs<IContent>(content, content.ContentType.Alias, parentId);
            // ReSharper disable once CSharpWarnings::CS0618
            if (Creating.IsRaisedEventCancelled(newArgs, this))
            {
                content.WasCancelled = true;
                return;
            }

            content.CreatorId = userId;
            content.WriterId = userId;

            if (withIdentity)
            {
                if (Saving.IsRaisedEventCancelled(new SaveEventArgs<IContent>(content), this))
                {
                    content.WasCancelled = true;
                    return;
                }

                _lrepo.WithWriteLocked(xr => xr.Repository.AddOrUpdate(content));

                Saved.RaiseEvent(new SaveEventArgs<IContent>(content, false), this);
                TreeChanged.RaiseEvent(new TreeChange<IContent>(content, TreeChangeTypes.RefreshNode).ToEventArgs(), this);
            }

            Created.RaiseEvent(new NewEventArgs<IContent>(content, false, content.ContentType.Alias, parent), this);

            var msg = withIdentity
                ? "Content '{0}' was created with Id {1}"
                : "Content '{0}' was created";
            Audit(AuditType.New, string.Format(msg, content.Name, content.Id), content.CreatorId, content.Id);
        }

        #endregion

        #region Get, Has, Is

        /// <summary>
        /// Gets an <see cref="IContent"/> object by Id
        /// </summary>
        /// <param name="id">Id of the Content to retrieve</param>
        /// <returns><see cref="IContent"/></returns>
        public IContent GetById(int id)
        {
            return _lrepo.WithReadLocked(xr => xr.Repository.Get(id));
        }

        /// <summary>
        /// Gets an <see cref="IContent"/> object by Id
        /// </summary>
        /// <param name="ids">Ids of the Content to retrieve</param>
        /// <returns><see cref="IContent"/></returns>
        public IEnumerable<IContent> GetByIds(IEnumerable<int> ids)
        {
            var idsA = ids.ToArray();
            return idsA.Length == 0
                ? Enumerable.Empty<IContent>()
                : _lrepo.WithReadLocked(xr => xr.Repository.GetAll(idsA));
        }

        /// <summary>
        /// Gets an <see cref="IContent"/> object by its 'UniqueId'
        /// </summary>
        /// <param name="key">Guid key of the Content to retrieve</param>
        /// <returns><see cref="IContent"/></returns>
        public IContent GetById(Guid key)
        {
            var query = Query<IContent>.Builder.Where(x => x.Key == key);
            return _lrepo.WithReadLocked(xr => xr.Repository.GetByQuery(query).SingleOrDefault());
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects by the Id of the <see cref="IContentType"/>
        /// </summary>
        /// <param name="id">Id of the <see cref="IContentType"/></param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetContentOfContentType(int id)
        {
            var query = Query<IContent>.Builder.Where(x => x.ContentTypeId == id);
            return _lrepo.WithReadLocked(xr => xr.Repository.GetByQuery(query));
        }

        internal IEnumerable<IContent> GetPublishedContentOfContentType(int id)
        {
            var query = Query<IContent>.Builder.Where(x => x.ContentTypeId == id);
            return _lrepo.WithReadLocked(xr => xr.Repository.GetByPublishedVersion(query));
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects by Level
        /// </summary>
        /// <param name="level">The level to retrieve Content from</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetByLevel(int level)
        {
            var query = Query<IContent>.Builder.Where(x => x.Level == level && x.Trashed == false);
            return _lrepo.WithReadLocked(xr => xr.Repository.GetByQuery(query));
        }

        /// <summary>
        /// Gets a specific version of an <see cref="IContent"/> item.
        /// </summary>
        /// <param name="versionId">Id of the version to retrieve</param>
        /// <returns>An <see cref="IContent"/> item</returns>
        public IContent GetByVersion(Guid versionId)
        {
            return _lrepo.WithReadLocked(xr => xr.Repository.GetByVersion(versionId));
        }


        /// <summary>
        /// Gets a collection of an <see cref="IContent"/> objects versions by Id
        /// </summary>
        /// <param name="id"></param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetVersions(int id)
        {
            return _lrepo.WithReadLocked(xr => xr.Repository.GetAllVersions(id));
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects, which are ancestors of the current content.
        /// </summary>
        /// <param name="id">Id of the <see cref="IContent"/> to retrieve ancestors for</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetAncestors(int id)
        {
            var content = GetById(id);
            return GetAncestors(content);
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects, which are ancestors of the current content.
        /// </summary>
        /// <param name="content"><see cref="IContent"/> to retrieve ancestors for</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetAncestors(IContent content)
        {
            // null check otherwise we get exceptions
            if (content.Path.IsNullOrWhiteSpace()) return Enumerable.Empty<IContent>();
            
            var rootId = Constants.System.Root.ToInvariantString();
            var ids = content.Path.Split(',')
                .Where(x => x != rootId && x != content.Id.ToString(CultureInfo.InvariantCulture)).Select(int.Parse).ToArray();
            if (ids.Any() == false)
                return new List<IContent>();

            return _lrepo.WithReadLocked(xr => xr.Repository.GetAll(ids));
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects by Parent Id
        /// </summary>
        /// <param name="id">Id of the Parent to retrieve Children from</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetChildren(int id)
        {
            var query = Query<IContent>.Builder.Where(x => x.ParentId == id);
            return _lrepo.WithReadLocked(xr => xr.Repository.GetByQuery(query).OrderBy(x => x.SortOrder));
        }

        /// <summary>
        /// Gets a collection of published <see cref="IContent"/> objects by Parent Id
        /// </summary>
        /// <param name="id">Id of the Parent to retrieve Children from</param>
        /// <returns>An Enumerable list of published <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetPublishedChildren(int id)
        {
            var query = Query<IContent>.Builder.Where(x => x.ParentId == id && x.Published);
            return _lrepo.WithReadLocked(xr => xr.Repository.GetByQuery(query).OrderBy(x => x.SortOrder));
        }

        [Obsolete("Use the overload with 'long' parameter types instead")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public IEnumerable<IContent> GetPagedChildren(int id, int pageIndex, int pageSize, out int totalChildren,
            string orderBy, Direction orderDirection, string filter = "")
        {
            long totalChildren2;
            var ret = GetPagedChildren(id, Convert.ToInt64(pageIndex), pageSize, out totalChildren2, orderBy, orderDirection, true, filter);
            totalChildren = Convert.ToInt32(totalChildren2);
            return ret;
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects by Parent Id
        /// </summary>
        /// <param name="id">Id of the Parent to retrieve Children from</param>
        /// <param name="pageIndex">Page index (zero based)</param>
        /// <param name="pageSize">Page size</param>
        /// <param name="totalChildren">Total records query would return without paging</param>
        /// <param name="orderBy">Field to order by</param>
        /// <param name="orderDirection">Direction to order by</param>
        /// <param name="filter">Search text filter</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetPagedChildren(int id, long pageIndex, int pageSize, out long totalChildren,
            string orderBy, Direction orderDirection, string filter = "")
        {
            return GetPagedChildren(id, pageIndex, pageSize, out totalChildren, orderBy, orderDirection, true, filter);
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects by Parent Id
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
        public IEnumerable<IContent> GetPagedChildren(int id, long pageIndex, int pageSize, out long totalChildren,
            string orderBy, Direction orderDirection, bool orderBySystemField, string filter)
        {
            Mandate.ParameterCondition(pageIndex >= 0, "pageIndex");
            Mandate.ParameterCondition(pageSize > 0, "pageSize");

            var query = Query<IContent>.Builder;
            //if the id is System Root, then just get all
            if (id != Constants.System.Root)
                query.Where(x => x.ParentId == id);

            IEnumerable<IContent> ret = null;
            long totalChildren2 = 0;
            _lrepo.WithReadLocked(xr =>
            {
                ret = xr.Repository.GetPagedResultsByQuery(query, pageIndex, pageSize, out totalChildren2, orderBy, orderDirection, orderBySystemField, filter);
            });
            totalChildren = totalChildren2;
            return ret;
        }

        [Obsolete("Use the overload with 'long' parameter types instead")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public IEnumerable<IContent> GetPagedDescendants(int id, int pageIndex, int pageSize, out int totalChildren, string orderBy = "Path", Direction orderDirection = Direction.Ascending, string filter = "")
        {
            long total;
            var result = GetPagedDescendants(id, Convert.ToInt64(pageIndex), pageSize, out total, orderBy, orderDirection, true, filter);
            totalChildren = Convert.ToInt32(total);
            return result;
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects by Parent Id
        /// </summary>
        /// <param name="id">Id of the Parent to retrieve Descendants from</param>
        /// <param name="pageIndex">Page number</param>
        /// <param name="pageSize">Page size</param>
        /// <param name="totalChildren">Total records query would return without paging</param>
        /// <param name="orderBy">Field to order by</param>
        /// <param name="orderDirection">Direction to order by</param>
        /// <param name="filter">Search text filter</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns> 
        public IEnumerable<IContent> GetPagedDescendants(int id, long pageIndex, int pageSize, out long totalChildren, string orderBy = "Path", Direction orderDirection = Direction.Ascending, string filter = "")
        {
            return GetPagedDescendants(id, pageIndex, pageSize, out totalChildren, orderBy, orderDirection, true, filter);
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects by Parent Id
        /// </summary>
        /// <param name="id">Id of the Parent to retrieve Descendants from</param>
        /// <param name="pageIndex">Page number</param>
        /// <param name="pageSize">Page size</param>
        /// <param name="totalChildren">Total records query would return without paging</param>
        /// <param name="orderBy">Field to order by</param>
        /// <param name="orderDirection">Direction to order by</param>
        /// <param name="orderBySystemField">Flag to indicate when ordering by system field</param>
        /// <param name="filter">Search text filter</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>        
        public IEnumerable<IContent> GetPagedDescendants(int id, long pageIndex, int pageSize, out long totalChildren, string orderBy, Direction orderDirection, bool orderBySystemField, string filter)
        {
            long totalChildren2;
            var ret = GetPagedDescendants(id, Convert.ToInt64(pageIndex), pageSize, out totalChildren2, orderBy, orderDirection, filter);
            totalChildren = Convert.ToInt32(totalChildren2);
            return ret;
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects by its name or partial name
        /// </summary>
        /// <param name="parentId">Id of the Parent to retrieve Children from</param>
        /// <param name="name">Full or partial name of the children</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetChildrenByName(int parentId, string name)
        {
            var query = Query<IContent>.Builder.Where(x => x.ParentId == parentId && x.Name.Contains(name));
            return _lrepo.WithReadLocked(xr => xr.Repository.GetByQuery(query));
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects by Parent Id
        /// </summary>
        /// <param name="id">Id of the Parent to retrieve Descendants from</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetDescendants(int id)
        {
            var content = GetById(id);
            return content == null ? Enumerable.Empty<IContent>() : GetDescendants(content);
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects by Parent Id
        /// </summary>
        /// <param name="content"><see cref="IContent"/> item to retrieve Descendants from</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetDescendants(IContent content)
        {
            var pathMatch = content.Path + ",";
            var query = Query<IContent>.Builder.Where(x => x.Id != content.Id && x.Path.StartsWith(pathMatch));
            return _lrepo.WithReadLocked(xr => xr.Repository.GetByQuery(query));
        }

        /// <summary>
        /// Gets the parent of the current content as an <see cref="IContent"/> item.
        /// </summary>
        /// <param name="id">Id of the <see cref="IContent"/> to retrieve the parent from</param>
        /// <returns>Parent <see cref="IContent"/> object</returns>
        public IContent GetParent(int id)
        {
            var content = GetById(id);
            return GetParent(content);
        }

        /// <summary>
        /// Gets the parent of the current content as an <see cref="IContent"/> item.
        /// </summary>
        /// <param name="content"><see cref="IContent"/> to retrieve the parent from</param>
        /// <returns>Parent <see cref="IContent"/> object</returns>
        public IContent GetParent(IContent content)
        {
            if (content.ParentId == Constants.System.Root || content.ParentId == Constants.System.RecycleBinContent)
                return null;

            return GetById(content.ParentId);
        }

        /// <summary>
        /// Gets the published version of an <see cref="IContent"/> item
        /// </summary>
        /// <param name="id">Id of the <see cref="IContent"/> to retrieve version from</param>
        /// <returns>An <see cref="IContent"/> item</returns>
        public IContent GetPublishedVersion(int id)
        {
            var version = GetVersions(id);
            return version.FirstOrDefault(x => x.Published);
        }

        /// <summary>
        /// Gets the published version of a <see cref="IContent"/> item.
        /// </summary>
        /// <param name="content">The content item.</param>
        /// <returns>The published version, if any; otherwise, null.</returns>
        public IContent GetPublishedVersion(IContent content)
        {
            if (content.Published) return content;
            return content.HasPublishedVersion
                ? GetByVersion(content.PublishedVersionGuid)
                : null;
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects, which reside at the first level / root
        /// </summary>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetRootContent()
        {
            var query = Query<IContent>.Builder.Where(x => x.ParentId == Constants.System.Root);
            return _lrepo.WithReadLocked(xr => xr.Repository.GetByQuery(query));
        }

        /// <summary>
        /// Gets all published content items
        /// </summary>
        /// <returns></returns>
        internal IEnumerable<IContent> GetAllPublished()
        {
            var query = Query<IContent>.Builder.Where(x => x.Trashed == false);
            return _lrepo.WithReadLocked(xr => xr.Repository.GetByPublishedVersion(query));
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects, which has an expiration date less than or equal to today.
        /// </summary>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetContentForExpiration()
        {
            var query = Query<IContent>.Builder.Where(x => x.Published && x.ExpireDate <= DateTime.Now);
            return _lrepo.WithReadLocked(xr => xr.Repository.GetByQuery(query));
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects, which has a release date less than or equal to today.
        /// </summary>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetContentForRelease()
        {
            var query = Query<IContent>.Builder.Where(x => x.Published == false && x.ReleaseDate <= DateTime.Now);
            return _lrepo.WithReadLocked(xr => xr.Repository.GetByQuery(query));
        }

        /// <summary>
        /// Gets a collection of an <see cref="IContent"/> objects, which resides in the Recycle Bin
        /// </summary>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetContentInRecycleBin()
        {
            var query = Query<IContent>.Builder.Where(x => x.Path.Contains(Constants.System.RecycleBinContent.ToInvariantString()));
            return _lrepo.WithReadLocked(xr => xr.Repository.GetByQuery(query));
        }

        /// <summary>
        /// Checks whether an <see cref="IContent"/> item has any children
        /// </summary>
        /// <param name="id">Id of the <see cref="IContent"/></param>
        /// <returns>True if the content has any children otherwise False</returns>
        public bool HasChildren(int id)
        {
            return CountChildren(id) > 0;
        }

        /// <summary>
        /// Checks whether an <see cref="IContent"/> item has any published versions
        /// </summary>
        /// <param name="id">Id of the <see cref="IContent"/></param>
        /// <returns>True if the content has any published version otherwise False</returns>
        public bool HasPublishedVersion(int id)
        {
            var query = Query<IContent>.Builder.Where(x => x.Published && x.Id == id && x.Trashed == false);
            return _lrepo.WithReadLocked(xr => xr.Repository.Count(query) > 0);
        }

        /// <summary>
        /// Checks if the passed in <see cref="IContent"/> can be published based on the anscestors publish state.
        /// </summary>
        /// <param name="content"><see cref="IContent"/> to check if anscestors are published</param>
        /// <returns>True if the Content can be published, otherwise False</returns>
        public bool IsPublishable(IContent content)
        {
            var parent = GetById(content.ParentId);
            return IsPathPublished(parent);
        }

        /// <summary>
        /// Gets a value indicating whether a specified content is path-published, ie whether it is published
        /// and all its ancestors are published too and none are trashed.
        /// </summary>
        /// <param name="content">The content.</param>
        /// <returns>true if the content is path-published; otherwise, false.</returns>
        public bool IsPathPublished(IContent content)
        {
            return _lrepo.WithReadLocked(xr => xr.Repository.IsPathPublished(content));
        }

        #endregion

        #region Save, Publish, Unpublish

        /// <summary>
        /// This will rebuild the xml structures for content in the database. 
        /// </summary>
        /// <param name="userId">This is not used for anything</param>
        /// <returns>True if publishing succeeded, otherwise False</returns>
        /// <remarks>
        /// This is used for when a document type alias or a document type property is changed, the xml will need to 
        /// be regenerated.
        /// </remarks>
        [Obsolete("See IPublishedCachesService implementations.", false)]
        public bool RePublishAll(int userId = 0)
        {
            throw new NotImplementedException("Obsolete.");
        }

        /// <summary>
        /// This will rebuild the xml structures for content in the database. 
        /// </summary>
        /// <param name="contentTypeIds">
        /// If specified will only rebuild the xml for the content type's specified, otherwise will update the structure
        /// for all published content.
        /// </param>
        [Obsolete("See IPublishedCachesService implementations.", false)]
        internal void RePublishAll(params int[] contentTypeIds)
        {
            throw new NotImplementedException("Obsolete.");
        }

        /// <summary>
        /// Saves a single <see cref="IContent"/> object
        /// </summary>
        /// <param name="content">The <see cref="IContent"/> to save</param>
        /// <param name="userId">Optional Id of the User saving the Content</param>
        /// <param name="raiseEvents">Optional boolean indicating whether or not to raise events.</param>
        public void Save(IContent content, int userId = 0, bool raiseEvents = true)
        {
            ((IContentServiceOperations) this).Save(content, userId, raiseEvents);
        }

        /// <summary>
        /// Saves a single <see cref="IContent"/> object
        /// </summary>
        /// <param name="content">The <see cref="IContent"/> to save</param>
        /// <param name="userId">Optional Id of the User saving the Content</param>
        /// <param name="raiseEvents">Optional boolean indicating whether or not to raise events.</param>
        Attempt<OperationStatus> IContentServiceOperations.Save(IContent content, int userId, bool raiseEvents)
        {
            var evtMsgs = EventMessagesFactory.Get();
            
            if (raiseEvents && Saving.IsRaisedEventCancelled(new SaveEventArgs<IContent>(content, evtMsgs), this))
                return OperationStatus.Cancelled(evtMsgs);

            var isNew = content.IsNewEntity();

            _lrepo.WithWriteLocked(xr =>
            {
                if (content.HasIdentity == false)
                    content.CreatorId = userId;
                content.WriterId = userId;

                // saving the Published version => indicate we are .Saving
                // saving the Unpublished version => remains .Unpublished
                if (content.Published)
                    content.ChangePublishedState(PublishedState.Saving);

                xr.Repository.AddOrUpdate(content);
            });

            if (raiseEvents)
                Saved.RaiseEvent(new SaveEventArgs<IContent>(content, false, evtMsgs), this);
            var changeType = isNew ? TreeChangeTypes.RefreshBranch : TreeChangeTypes.RefreshNode;
            using (ChangeSet.WithAmbient)
                TreeChanged.RaiseEvent(new TreeChange<IContent>(content, changeType).ToEventArgs(), this);
            Audit(AuditType.Save, "Save Content performed by user", userId, content.Id);
            return OperationStatus.Success(evtMsgs);
        }

        /// <summary>
        /// Saves a collection of <see cref="IContent"/> objects.
        /// </summary>
        /// <remarks>
        /// If the collection of content contains new objects that references eachother by Id or ParentId,
        /// then use the overload Save method with a collection of Lazy <see cref="IContent"/>.
        /// </remarks>
        /// <param name="contents">Collection of <see cref="IContent"/> to save</param>
        /// <param name="userId">Optional Id of the User saving the Content</param>
        /// <param name="raiseEvents">Optional boolean indicating whether or not to raise events.</param>
        public void Save(IEnumerable<IContent> contents, int userId = 0, bool raiseEvents = true)
        {
            ((IContentServiceOperations) this).Save(contents, userId, raiseEvents);
        }
        
        /// <summary>
        /// Saves a collection of <see cref="IContent"/> objects.
        /// </summary>
        /// <remarks>
        /// If the collection of content contains new objects that references eachother by Id or ParentId,
        /// then use the overload Save method with a collection of Lazy <see cref="IContent"/>.
        /// </remarks>
        /// <param name="contents">Collection of <see cref="IContent"/> to save</param>
        /// <param name="userId">Optional Id of the User saving the Content</param>
        /// <param name="raiseEvents">Optional boolean indicating whether or not to raise events.</param>
        Attempt<OperationStatus> IContentServiceOperations.Save(IEnumerable<IContent> contents, int userId, bool raiseEvents)
        {
            var evtMsgs = EventMessagesFactory.Get();
            var contentsA = contents.ToArray();

            if (raiseEvents && Saving.IsRaisedEventCancelled(new SaveEventArgs<IContent>(contentsA, evtMsgs), this))
                return OperationStatus.Cancelled(evtMsgs);

            var treeChanges = contentsA.Select(x => new TreeChange<IContent>(x,
                x.IsNewEntity() ? TreeChangeTypes.RefreshBranch : TreeChangeTypes.RefreshNode));

            _lrepo.WithWriteLocked(xr =>
            {
                foreach (var content in contentsA)
                {
                    if (content.HasIdentity == false)
                        content.CreatorId = userId;
                    content.WriterId = userId;

                    // saving the Published version => indicate we are .Saving
                    // saving the Unpublished version => remains .Unpublished
                    if (content.Published)
                        content.ChangePublishedState(PublishedState.Saving);

                    xr.Repository.AddOrUpdate(content);
                }
            });

            if (raiseEvents)
                Saved.RaiseEvent(new SaveEventArgs<IContent>(contentsA, false, evtMsgs), this);
            using (ChangeSet.WithAmbient)
                TreeChanged.RaiseEvent(treeChanges.ToEventArgs(), this);
            Audit(AuditType.Save, "Bulk Save content performed by user", userId == -1 ? 0 : userId, Constants.System.Root);
            return OperationStatus.Success(evtMsgs);
        }

        /// <summary>
        /// Saves and Publishes a single <see cref="IContent"/> object
        /// </summary>
        /// <param name="content">The <see cref="IContent"/> to save and publish</param>
        /// <param name="userId">Optional Id of the User issueing the publishing</param>
        /// <param name="raiseEvents">Optional boolean indicating whether or not to raise save events.</param>
        /// <returns>True if publishing succeeded, otherwise False</returns>
        [Obsolete("Use SaveAndPublishWithStatus instead, that method will provide more detailed information on the outcome")]
        public bool SaveAndPublish(IContent content, int userId = 0, bool raiseEvents = true)
        {
            var result = SaveAndPublishDo(content, userId, raiseEvents);
            return result.Success;
        }

        /// <summary>
        /// Saves and Publishes a single <see cref="IContent"/> object
        /// </summary>
        /// <param name="content">The <see cref="IContent"/> to save and publish</param>
        /// <param name="userId">Optional Id of the User issueing the publishing</param>
        /// <param name="raiseEvents">Optional boolean indicating whether or not to raise save events.</param>
        /// <returns>True if publishing succeeded, otherwise False</returns>
        Attempt<PublishStatus> IContentServiceOperations.SaveAndPublish(IContent content, int userId, bool raiseEvents)
        {
            return SaveAndPublishDo(content, userId, raiseEvents);
        }

        /// <summary>
        /// Publishes a single <see cref="IContent"/> object
        /// </summary>
        /// <param name="content">The <see cref="IContent"/> to publish</param>
        /// <param name="userId">Optional Id of the User issueing the publishing</param>
        /// <returns>True if publishing succeeded, otherwise False</returns>
        public bool Publish(IContent content, int userId = 0)
        {
            Logger.Info<ContentService>("Call was made to ContentService.Publish, use PublishWithStatus instead since that method will provide more detailed information on the outcome");
            var result = ((IContentServiceOperations)this).SaveAndPublish(content, userId);
            return result.Success;
        }

        /// <summary>
        /// Publishes a single <see cref="IContent"/> object
        /// </summary>
        /// <param name="content">The <see cref="IContent"/> to publish</param>
        /// <param name="userId">Optional Id of the User issueing the publishing</param>
        /// <returns>True if publishing succeeded, otherwise False</returns>
        Attempt<PublishStatus> IContentServiceOperations.Publish(IContent content, int userId)
        {
            return SaveAndPublishDo(content, userId);
        }

        /// <summary>
        /// Unpublishes a single <see cref="IContent"/> object.
        /// </summary>
        /// <param name="content">The <see cref="IContent"/> to publish.</param>
        /// <param name="userId">Optional unique identifier of the User issueing the unpublishing.</param>
        /// <returns>True if unpublishing succeeded, otherwise False.</returns>
        public bool UnPublish(IContent content, int userId = 0)
        {
            return UnPublishDo(content, userId);
        }

        /// <summary>
        /// Unpublishes a single <see cref="IContent"/> object.
        /// </summary>
        /// <param name="content">The <see cref="IContent"/> to publish.</param>
        /// <param name="userId">Optional unique identifier of the User issueing the unpublishing.</param>
        /// <returns>True if unpublishing succeeded, otherwise False.</returns>
        Attempt<UnPublishStatus> IContentServiceOperations.UnPublish(IContent content, int userId)
        {
            return UnPublishDo(content, userId);
        }

        /// <summary>
        /// Saves and Publishes a single <see cref="IContent"/> object
        /// </summary>
        /// <param name="content">The <see cref="IContent"/> to save and publish</param>
        /// <param name="userId">Optional Id of the User issueing the publishing</param>
        /// <param name="raiseEvents">Optional boolean indicating whether or not to raise save events.</param>
        /// <returns>True if publishing succeeded, otherwise False</returns>
        public Attempt<PublishStatus> SaveAndPublishWithStatus(IContent content, int userId = 0, bool raiseEvents = true)
        {
            return ((IContentServiceOperations) this).SaveAndPublish(content, userId);
        }

        /// <summary>
        /// Publishes a single <see cref="IContent"/> object
        /// </summary>
        /// <param name="content">The <see cref="IContent"/> to publish</param>
        /// <param name="userId">Optional Id of the User issueing the publishing</param>
        /// <returns>True if publishing succeeded, otherwise False</returns>
        public Attempt<PublishStatus> PublishWithStatus(IContent content, int userId = 0)
        {
            return ((IContentServiceOperations) this).Publish(content, userId);
        }

        /// <summary>
        /// Publishes a <see cref="IContent"/> object and all its children
        /// </summary>
        /// <param name="content">The <see cref="IContent"/> to publish along with its children</param>
        /// <param name="userId">Optional Id of the User issueing the publishing</param>
        /// <returns>True if publishing succeeded, otherwise False</returns>
        [Obsolete("Use PublishWithChildrenWithStatus instead, that method will provide more detailed information on the outcome and also allows the includeUnpublished flag")]
        public bool PublishWithChildren(IContent content, int userId = 0)
        {
            var result = ((IContentServiceOperations)this).PublishWithChildren(content, userId, true);

            // this used to just return false only when the parent content failed;
            // otherwise would always return true so we'll do the same thing for the moment
            // though it makes little sense

            var parentResult = result.SingleOrDefault(x => x.Result.ContentItem.Id == content.Id);
            // if it does not exist, then parentResult is default(Attempt<PublishStatus>) and not
            // null because Attempt is a value type, and its default has .Success == false so we
            // can return it
            return parentResult.Success;
        }

        /// <summary>
        /// Publishes a <see cref="IContent"/> object and all its children
        /// </summary>
        /// <param name="content">The <see cref="IContent"/> to publish along with its children</param>
        /// <param name="userId">Optional Id of the User issueing the publishing</param>
        /// <param name="includeUnpublished">set to true if you want to also publish children that are currently unpublished</param>
        /// <returns>True if publishing succeeded, otherwise False</returns>
        public IEnumerable<Attempt<PublishStatus>> PublishWithChildrenWithStatus(IContent content, int userId = 0, bool includeUnpublished = false)
        {
            return ((IContentServiceOperations) this).PublishWithChildren(content, userId, includeUnpublished);
        }

        /// <summary>
        /// Publishes a <see cref="IContent"/> object and all its children
        /// </summary>
        /// <param name="content">The <see cref="IContent"/> to publish along with its children</param>
        /// <param name="userId">Optional Id of the User issueing the publishing</param>
        /// <param name="includeUnpublished">set to true if you want to also publish children that are currently unpublished</param>
        /// <returns>True if publishing succeeded, otherwise False</returns>
        IEnumerable<Attempt<PublishStatus>> IContentServiceOperations.PublishWithChildren(IContent content, int userId, bool includeUnpublished)
        {
            return PublishWithChildrenDo(content, userId, includeUnpublished);
        }

        #endregion

        #region Delete

        /// <summary>
        /// Permanently deletes an <see cref="IContent"/> object as well as all of its Children.
        /// </summary>
        /// <remarks>
        /// This method will also delete associated media files, child content and possibly associated domains.
        /// </remarks>
        /// <remarks>Please note that this method will completely remove the Content from the database</remarks>
        /// <param name="content">The <see cref="IContent"/> to delete</param>
        /// <param name="userId">Optional Id of the User deleting the Content</param>
        public void Delete(IContent content, int userId = 0)
        {
            ((IContentServiceOperations) this).Delete(content, userId);
        }
        
        /// <summary>
        /// Permanently deletes an <see cref="IContent"/> object as well as all of its Children.
        /// </summary>
        /// <remarks>
        /// This method will also delete associated media files, child content and possibly associated domains.
        /// </remarks>
        /// <remarks>Please note that this method will completely remove the Content from the database</remarks>
        /// <param name="content">The <see cref="IContent"/> to delete</param>
        /// <param name="userId">Optional Id of the User deleting the Content</param>
        Attempt<OperationStatus> IContentServiceOperations.Delete(IContent content, int userId)
        {
            var evtMsgs = EventMessagesFactory.Get();
            var returnAttempt = false;
            var attempt = default(Attempt<OperationStatus>);
            
            using (ChangeSet.WithAmbient)
            {
                _lrepo.WithWriteLocked(xr =>
                {
                    if (Deleting.IsRaisedEventCancelled(new DeleteEventArgs<IContent>(content, evtMsgs), this))
                    {
                        attempt = OperationStatus.Cancelled(evtMsgs);
                        returnAttempt = true;
                        return;
                    }

                    // if it's not trashed yet, and published, we should unpublish
                    // but... UnPublishing event makes no sense (not going to cancel?) and no need to save
                    // just raise the event
                    if (content.Trashed == false && content.HasPublishedVersion)
                        UnPublished.RaiseEvent(new PublishEventArgs<IContent>(content, false, false, evtMsgs), this);

                    DeleteLocked(content, xr.Repository, evtMsgs);

                    TreeChanged.RaiseEvent(new TreeChange<IContent>(content, TreeChangeTypes.Remove).ToEventArgs(), this);
                });                
            }

            if (returnAttempt)
                return attempt;

            Audit(AuditType.Delete, "Delete Content performed by user", userId, content.Id);
            return OperationStatus.Success(evtMsgs);
        }

        private void DeleteLocked(IContent content, IContentRepository repository, EventMessages evtMsgs)
        {
            // then recursively delete descendants, bottom-up
            // just repository.Delete + an event
            var stack = new Stack<IContent>();
            stack.Push(content);
            var level = 1;
            while (stack.Count > 0)
            {
                var c = stack.Peek();
                IContent[] cc;
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
                var args = new DeleteEventArgs<IContent>(c, false, evtMsgs); // raise event & get flagged files
                Deleted.RaiseEvent(args, this);
                IOHelper.DeleteFiles(args.MediaFilesToDelete, // remove flagged files
                    (file, e) => Logger.Error<MemberService>("An error occurred while deleting file attached to nodes: " + file, e));
            }
        }

        //TODO:
        // both DeleteVersions methods below have an issue. Sort of. They do NOT take care of files the way
        // Delete does - for a good reason: the file may be referenced by other, non-deleted, versions. BUT,
        // if that's not the case, then the file will never be deleted, because when we delete the content,
        // the version referencing the file will not be there anymore. SO, we can leak files.

        /// <summary>
        /// Permanently deletes versions from an <see cref="IContent"/> object prior to a specific date.
        /// This method will never delete the latest version of a content item.
        /// </summary>
        /// <param name="id">Id of the <see cref="IContent"/> object to delete versions from</param>
        /// <param name="versionDate">Latest version date</param>
        /// <param name="userId">Optional Id of the User deleting versions of a Content object</param>
        public void DeleteVersions(int id, DateTime versionDate, int userId = 0)
        {
            if (DeletingVersions.IsRaisedEventCancelled(new DeleteRevisionsEventArgs(id, dateToRetain: versionDate), this))
                return;

            _lrepo.WithWriteLocked(xr => xr.Repository.DeleteVersions(id, versionDate));

            DeletedVersions.RaiseEvent(new DeleteRevisionsEventArgs(id, false, dateToRetain: versionDate), this);
            Audit(AuditType.Delete, "Delete Content by version date performed by user", userId, Constants.System.Root);
        }

        /// <summary>
        /// Permanently deletes specific version(s) from an <see cref="IContent"/> object.
        /// This method will never delete the latest version of a content item.
        /// </summary>
        /// <param name="id">Id of the <see cref="IContent"/> object to delete a version from</param>
        /// <param name="versionId">Id of the version to delete</param>
        /// <param name="deletePriorVersions">Boolean indicating whether to delete versions prior to the versionId</param>
        /// <param name="userId">Optional Id of the User deleting versions of a Content object</param>
        public void DeleteVersion(int id, Guid versionId, bool deletePriorVersions, int userId = 0)
        {
            using (ChangeSet.WithAmbient)
            {
                _lrepo.WithWriteLocked(xr =>
                {
                    if (DeletingVersions.IsRaisedEventCancelled(new DeleteRevisionsEventArgs(id, /*specificVersion:*/ versionId), this))
                        return;

                    if (deletePriorVersions)
                    {
                        var content = GetByVersion(versionId);
                        DeleteVersions(id, content.UpdateDate, userId);
                    }

                    xr.Repository.DeleteVersion(versionId);
                });
            }

            DeletedVersions.RaiseEvent(new DeleteRevisionsEventArgs(id, false, /*specificVersion:*/ versionId), this);
            Audit(AuditType.Delete, "Delete Content by version performed by user", userId, Constants.System.Root);
        }

        #endregion 

        #region Move, RecycleBin

        /// <summary>
        /// Deletes an <see cref="IContent"/> object by moving it to the Recycle Bin
        /// </summary>
        /// <remarks>Move an item to the Recycle Bin will result in the item being unpublished</remarks>
        /// <param name="content">The <see cref="IContent"/> to delete</param>
        /// <param name="userId">Optional Id of the User deleting the Content</param>
        public void MoveToRecycleBin(IContent content, int userId = 0)
        {
            ((IContentServiceOperations) this).MoveToRecycleBin(content, userId);
        }

        /// <summary>
        /// Deletes an <see cref="IContent"/> object by moving it to the Recycle Bin
        /// </summary>
        /// <remarks>Move an item to the Recycle Bin will result in the item being unpublished</remarks>
        /// <param name="content">The <see cref="IContent"/> to delete</param>
        /// <param name="userId">Optional Id of the User deleting the Content</param>
        Attempt<OperationStatus> IContentServiceOperations.MoveToRecycleBin(IContent content, int userId)
        {
            var moves = new List<Tuple<IContent, string>>();
            var evtMsgs = EventMessagesFactory.Get();
            var returnAttempt = false;
            var attempt = default(Attempt<OperationStatus>);

            using (ChangeSet.WithAmbient)
            {
                _lrepo.WithWriteLocked(xr =>
                {
                    var originalPath = content.Path;
                    if (Trashing.IsRaisedEventCancelled(new MoveEventArgs<IContent>(
                        new MoveEventInfo<IContent>(content, originalPath, Constants.System.RecycleBinContent)), this))
                    {
                        attempt = OperationStatus.Cancelled(evtMsgs);
                        returnAttempt = true;
                        return;
                    }

                    // if it's published we may want to force-unpublish it - that would be backward-compatible... but...
                    // making a radical decision here: trashing is equivalent to moving under an unpublished node so
                    // it's NOT unpublishing, only the content is now masked - allowing us to restore it if wanted
                    //if (content.HasPublishedVersion)
                    //{ }

                    PerformMoveLocked(content, Constants.System.RecycleBinContent, null, userId, moves, true, xr.Repository);

                    TreeChanged.RaiseEvent(new TreeChange<IContent>(content, TreeChangeTypes.RefreshBranch).ToEventArgs(), this);
                });
            }

            if (returnAttempt)
                return attempt;

            var moveInfo = moves
                .Select(x => new MoveEventInfo<IContent>(x.Item1, x.Item2, x.Item1.ParentId))
                .ToArray();
            Trashed.RaiseEvent(new MoveEventArgs<IContent>(false, moveInfo), this);
            Audit(AuditType.Move, "Move Content to Recycle Bin performed by user", userId, content.Id);

            return OperationStatus.Success(evtMsgs);
        }

        /// <summary>
        /// Moves an <see cref="IContent"/> object to a new location by changing its parent id.
        /// </summary>
        /// <remarks>
        /// If the <see cref="IContent"/> object is already published it will be
        /// published after being moved to its new location. Otherwise it'll just
        /// be saved with a new parent id.
        /// </remarks>
        /// <param name="content">The <see cref="IContent"/> to move</param>
        /// <param name="parentId">Id of the Content's new Parent</param>
        /// <param name="userId">Optional Id of the User moving the Content</param>
        public void Move(IContent content, int parentId, int userId = 0)
        {
            // if moving to the recycle bin then use the proper method
            if (parentId == Constants.System.RecycleBinContent)
            {
                MoveToRecycleBin(content, userId);
                return;
            }

            var moves = new List<Tuple<IContent, string>>();

            using (ChangeSet.WithAmbient)
            {
                _lrepo.WithWriteLocked(xr =>
                {
                    var parent = parentId == Constants.System.Root ? null : GetById(parentId);
                    if (parentId != Constants.System.Root && (parent == null || parent.Trashed))
                        throw new InvalidOperationException("Parent does not exist or is trashed.");

                    if (Moving.IsRaisedEventCancelled(new MoveEventArgs<IContent>(
                        new MoveEventInfo<IContent>(content, content.Path, parentId)), this))
                        return;

                    // if content was trashed, and since we're not moving to the recycle bin,
                    // indicate that the trashed status should be changed to false, else just
                    // leave it unchanged
                    var trashed = content.Trashed ? false : (bool?)null;

                    // if the content was trashed under another content, and so has a published version,
                    // it cannot move back as published but has to be unpublished first - that's for the
                    // root content, everything underneath will retain its published status
                    if (content.Trashed && content.HasPublishedVersion)
                    {
                        // however, it had been masked when being trashed, so there's no need for
                        // any special event here - just change its state
                        content.ChangePublishedState(PublishedState.Unpublishing);
                    }

                    PerformMoveLocked(content, parentId, parent, userId, moves, trashed, xr.Repository);

                    TreeChanged.RaiseEvent(new TreeChange<IContent>(content, TreeChangeTypes.RefreshBranch).ToEventArgs(), this);
                });
            }

            var moveInfo = moves //changes
                    .Select(x => new MoveEventInfo<IContent>(x.Item1, x.Item2, x.Item1.ParentId))
                    .ToArray();
            Moved.RaiseEvent(new MoveEventArgs<IContent>(false, moveInfo), this);
            Audit(AuditType.Move, "Move Content performed by user", userId, content.Id);
        }

        // MUST be called from within WriteLock
        // trash indicates whether we are trashing, un-trashing, or not changing anything
        private void PerformMoveLocked(IContent content, int parentId, IContent parent, int userId, 
            ICollection<Tuple<IContent, string>> moves,
            bool? trash, IContentRepository repository)
        {
            content.WriterId = userId;
            content.ParentId = parentId;

            // get the level delta (old pos to new pos)
            var levelDelta = parent == null
                ? 1 - content.Level + (parentId == Constants.System.RecycleBinContent ? 1 : 0)
                : parent.Level + 1 - content.Level;

            var paths = new Dictionary<int, string>();

            moves.Add(Tuple.Create(content, content.Path)); // capture original path

            // these will be updated by the repo because we changed parentId
            //content.Path = (parent == null ? "-1" : parent.Path) + "," + content.Id;
            //content.SortOrder = ((ContentRepository) repository).NextChildSortOrder(parentId);
            //content.Level += levelDelta;
            PerformMoveContentLocked(repository, content, userId, trash);

            // BUT content.Path will be updated only when the UOW commits, and
            //  because we want it now, we have to calculate it by ourselves
            //paths[content.Id] = content.Path;
            paths[content.Id] = (parent == null ? (parentId == Constants.System.RecycleBinContent ? "-1,-20" : "-1") : parent.Path) + "," + content.Id;

            var descendants = GetDescendants(content);
            foreach (var descendant in descendants)
            {
                moves.Add(Tuple.Create(descendant, descendant.Path)); // capture original path

                // update path and level since we do not update parentId
                descendant.Path = paths[descendant.Id] = paths[descendant.ParentId] + "," + descendant.Id;
                descendant.Level += levelDelta;
                PerformMoveContentLocked(repository, descendant, userId, trash);
            }
        }

        private static void PerformMoveContentLocked(IContentRepository repository, IContent content, int userId,
            bool? trash)
        {
            if (trash.HasValue) ((ContentBase) content).Trashed = trash.Value;
            content.WriterId = userId;
            repository.AddOrUpdate(content);
        }

        /// <summary>
        /// Empties the Recycle Bin by deleting all <see cref="IContent"/> that resides in the bin.
        /// </summary>
        public void EmptyRecycleBin()
        {
            var nodeObjectType = new Guid(Constants.ObjectTypes.Document);
            var deleted = new List<IContent>();
            var evtMsgs = EventMessagesFactory.Get();

            using (ChangeSet.WithAmbient)
            {
                _lrepo.WithWriteLocked(xr =>
                {
                    // no idea what those events are for, keep a simplified version
                    if (EmptyingRecycleBin.IsRaisedEventCancelled(new RecycleBinEventArgs(nodeObjectType), this))
                        return;

                    // emptying the recycle bin means deleting whetever is in there - do it properly!
                    var query = Query<IContent>.Builder.Where(x => x.ParentId == Constants.System.RecycleBinContent);
                    var contents = xr.Repository.GetByQuery(query).ToArray();
                    foreach (var content in contents)
                    {
                        DeleteLocked(content, xr.Repository, evtMsgs);
                        deleted.Add(content);
                    }

                    EmptiedRecycleBin.RaiseEvent(new RecycleBinEventArgs(nodeObjectType, true), this);
                    TreeChanged.RaiseEvent(deleted.Select(x => new TreeChange<IContent>(x, TreeChangeTypes.Remove)).ToEventArgs(), this);
                });
            }

            Audit(AuditType.Delete, "Empty Content Recycle Bin performed by user", 0, Constants.System.RecycleBinContent);
        }

        #endregion

        #region Others

        /// <summary>
        /// Copies an <see cref="IContent"/> object by creating a new Content object of the same type and copies all data from the current 
        /// to the new copy which is returned. Recursively copies all children.
        /// </summary>
        /// <param name="content">The <see cref="IContent"/> to copy</param>
        /// <param name="parentId">Id of the Content's new Parent</param>
        /// <param name="relateToOriginal">Boolean indicating whether the copy should be related to the original</param>
        /// <param name="userId">Optional Id of the User copying the Content</param>
        /// <returns>The newly created <see cref="IContent"/> object</returns>
        public IContent Copy(IContent content, int parentId, bool relateToOriginal, int userId = 0)
        {
            return Copy(content, parentId, relateToOriginal, true, userId);
        }

        /// <summary>
        /// Copies an <see cref="IContent"/> object by creating a new Content object of the same type and copies all data from the current 
        /// to the new copy which is returned.
        /// </summary>
        /// <param name="content">The <see cref="IContent"/> to copy</param>
        /// <param name="parentId">Id of the Content's new Parent</param>
        /// <param name="relateToOriginal">Boolean indicating whether the copy should be related to the original</param>
        /// <param name="recursive">A value indicating whether to recursively copy children.</param>
        /// <param name="userId">Optional Id of the User copying the Content</param>
        /// <returns>The newly created <see cref="IContent"/> object</returns>
        public IContent Copy(IContent content, int parentId, bool relateToOriginal, bool recursive, int userId = 0)
        {
            if (parentId == Constants.System.RecycleBinContent)
                throw new InvalidOperationException("Cannot create a copy in trash.");

            IContent copy = null;

            using (ChangeSet.WithAmbient)
            {
                _lrepo.WithWriteLocked(xr =>
                {
                    copy = content.DeepCloneWithResetIdentities();
                    copy.ParentId = parentId;

                    if (Copying.IsRaisedEventCancelled(new CopyEventArgs<IContent>(content, copy, parentId), this))
                    {
                        copy = null;
                        return;
                    }

                    // a copy is .Saving and will be .Unpublished
                    if (copy.Published)
                        copy.ChangePublishedState(PublishedState.Saving);

                    // update the create author and last edit author
                    copy.CreatorId = userId;
                    copy.WriterId = userId;

                    // save
                    xr.Repository.AddOrUpdate(copy);

                    // process descendants
                    if (recursive)
                    {
                        var copyIds = new Dictionary<int, IContent>();
                        copyIds[content.Id] = copy;
                        foreach (var descendant in GetDescendants(content))
                        {
                            var dcopy = descendant.DeepCloneWithResetIdentities();
                            //dcopy.ParentId = copyIds[descendant.ParentId];
                            var descendantParentId = descendant.ParentId;
                            ((Content) dcopy).SetLazyParentId(new Lazy<int>(() => copyIds[descendantParentId].Id));
                            if (dcopy.Published)
                                dcopy.ChangePublishedState(PublishedState.Saving);
                            dcopy.CreatorId = userId;
                            dcopy.WriterId = userId;
                            xr.Repository.AddOrUpdate(dcopy);

                            copyIds[descendant.Id] = dcopy;
                        }
                    }

                    // note: here was some code handling tags - which has been removed
                    // - tags should be handled by the content repository
                    // - a copy is unpublished and therefore has no impact on tags in DB
                });
            }

            TreeChanged.RaiseEvent(new TreeChange<IContent>(copy, TreeChangeTypes.RefreshBranch).ToEventArgs(), this);
            Copied.RaiseEvent(new CopyEventArgs<IContent>(content, copy, false, parentId, relateToOriginal), this);
            Audit(AuditType.Copy, "Copy Content performed by user", content.WriterId, content.Id);
            return copy;
        }

        /// <summary>
        /// Sends an <see cref="IContent"/> to Publication, which executes handlers and events for the 'Send to Publication' action.
        /// </summary>
        /// <param name="content">The <see cref="IContent"/> to send to publication</param>
        /// <param name="userId">Optional Id of the User issueing the send to publication</param>
        /// <returns>True if sending publication was succesfull otherwise false</returns>
        public bool SendToPublication(IContent content, int userId = 0)
        {
            if (SendingToPublish.IsRaisedEventCancelled(new SendToPublishEventArgs<IContent>(content), this))
                return false;

            //Save before raising event
            Save(content, userId);

            SentToPublish.RaiseEvent(new SendToPublishEventArgs<IContent>(content, false), this);

            Audit(AuditType.SendToPublish, "Send to Publish performed by user", content.WriterId, content.Id);

            return true;
        }

        /// <summary>
        /// Rollback an <see cref="IContent"/> object to a previous version.
        /// This will create a new version, which is a copy of all the old data.
        /// </summary>
        /// <remarks>
        /// The way data is stored actually only allows us to rollback on properties
        /// and not data like Name and Alias of the Content.
        /// </remarks>
        /// <param name="id">Id of the <see cref="IContent"/>being rolled back</param>
        /// <param name="versionId">Id of the version to rollback to</param>
        /// <param name="userId">Optional Id of the User issueing the rollback of the Content</param>
        /// <returns>The newly created <see cref="IContent"/> object</returns>
        public IContent Rollback(int id, Guid versionId, int userId = 0)
        {
            var content = GetByVersion(versionId);

            if (RollingBack.IsRaisedEventCancelled(new RollbackEventArgs<IContent>(content), this))
                return content;

            _lrepo.WithWriteLocked(xr =>
            {
                content.CreatorId = userId;
                //content.WriterId = userId;

                // need to make sure that the repository is going to save a new version
                // but if we're not changing anything, the repository would not save anything
                // so - make sure the property IS dirty, doing a flip-flop with an impossible value
                content.WriterId = -1;
                content.WriterId = userId;

                // a rolled back version is .Saving and will be .Unpublished
                content.ChangePublishedState(PublishedState.Saving);

                xr.Repository.AddOrUpdate(content);
            });

            RolledBack.RaiseEvent(new RollbackEventArgs<IContent>(content, false), this);
            TreeChanged.RaiseEvent(new TreeChange<IContent>(content, TreeChangeTypes.RefreshNode).ToEventArgs(), this);

            Audit(AuditType.RollBack, "Content rollback performed by user", content.WriterId, content.Id);

            return content;
        }

        /// <summary>
        /// Sorts a collection of <see cref="IContent"/> objects by updating the SortOrder according
        /// to the ordering of items in the passed in <see cref="IEnumerable{T}"/>.
        /// </summary>
        /// <remarks>
        /// Using this method will ensure that the Published-state is maintained upon sorting
        /// so the cache is updated accordingly - as needed.
        /// </remarks>
        /// <param name="items"></param>
        /// <param name="userId"></param>
        /// <param name="raiseEvents"></param>
        /// <returns>True if sorting succeeded, otherwise False</returns>
        public bool Sort(IEnumerable<IContent> items, int userId = 0, bool raiseEvents = true)
        {
            var itemsA = items.ToArray();
            if (itemsA.Length == 0) return true;

            //TODO:
            // firing Saving for all the items, but we're not going to save those that are already
            // correctly ordered, so we're not going to fire Saved for all the items, and that's not
            // really consistent - but the only way to be consistent would be to first check which
            // items we're going to save, then trigger the events... within the UOW transaction...
            // which is not something we want to do, so what?
            if (raiseEvents && Saving.IsRaisedEventCancelled(new SaveEventArgs<IContent>(itemsA), this))
                return false;

            var published = new List<IContent>();
            var saved = new List<IContent>();

            using (ChangeSet.WithAmbient)
            {
                _lrepo.WithWriteLocked(xr =>
                {
                    var sortOrder = 0;
                    foreach (var content in itemsA)
                    {
                        // if the current sort order equals that of the content we don't
                        // need to update it, so just increment the sort order and continue.
                        if (content.SortOrder == sortOrder)
                        {
                            sortOrder++;
                            continue;
                        }

                        // else update
                        content.SortOrder = sortOrder++;
                        content.WriterId = userId;

                        // if it's published, register it, no point running StrategyPublish
                        // since we're not really publishing it and it cannot be cancelled etc
                        if (content.Published)
                            published.Add(content);
                        else if (content.HasPublishedVersion)
                            published.Add(GetByVersion(content.PublishedVersionGuid));

                        // save
                        saved.Add(content);
                        xr.Repository.AddOrUpdate(content);
                    }
                });

                if (raiseEvents)
                    Saved.RaiseEvent(new SaveEventArgs<IContent>(saved, false), this);

                if (raiseEvents && published.Any())
                    Published.RaiseEvent(new PublishEventArgs<IContent>(published, false, false), this);

                TreeChanged.RaiseEvent(saved.Select(x => new TreeChange<IContent>(x, TreeChangeTypes.RefreshNode)).ToEventArgs(), this);
            }
            
            Audit(AuditType.Sort, "Sorting content performed by user", userId, 0);
            return true;
        }

        #endregion

        #region Internal Methods

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> descendants by the first Parent.
        /// </summary>
        /// <param name="content"><see cref="IContent"/> item to retrieve Descendants from</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        internal IEnumerable<IContent> GetPublishedDescendants(IContent content)
        {
            return _lrepo.WithReadLocked(xr => GetPublishedDescendantsLocked(content, xr.Repository));
        }

        internal IEnumerable<IContent> GetPublishedDescendantsLocked(IContent content, IContentRepository repository)
        {
            var pathMatch = content.Path + ",";
            var query = Query<IContent>.Builder.Where(x => x.Id != content.Id && x.Path.StartsWith(pathMatch) /*&& x.Trashed == false*/);
            var contents = repository.GetByPublishedVersion(query);

            // beware! contents contains all published version below content
            // including those that are not directly published because below an unpublished content
            // these must be filtered out here

            var parents = new List<int> { content.Id };
            foreach (var c in contents)
            {
                if (parents.Contains(c.ParentId))
                {
                    yield return c;
                    parents.Add(c.Id);
                }
            }
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

        /// <summary>
        /// Publishes a <see cref="IContent"/> object and all its children
        /// </summary>
        /// <param name="content">The <see cref="IContent"/> to publish along with its children</param>
        /// <param name="userId">Optional Id of the User issueing the publishing</param>
        /// <param name="includeUnpublished">If set to true, this will also publish descendants that are completely unpublished, normally this will only publish children that have previously been published</param>	    
        /// <returns>
        /// A list of publish statues. If the parent document is not valid or cannot be published because it's parent(s) is not published
        /// then the list will only contain one status item, otherwise it will contain status items for it and all of it's descendants that
        /// are to be published.
        /// </returns>
        private IEnumerable<Attempt<PublishStatus>> PublishWithChildrenDo(IContent content, int userId = 0, bool includeUnpublished = false)
        {
            if (content == null) throw new ArgumentNullException("content");

            var evtMsgs = EventMessagesFactory.Get();
            Attempt<PublishStatus>[] attempts = null;
            var publishedItems = new List<IContent>(); // this is for events

            using (ChangeSet.WithAmbient)
            {
                _lrepo.WithWriteLocked(xr =>
                {
                    // fail fast + use in alreadyChecked below to avoid duplicate checks
                    var attempt = EnsurePublishable(content, evtMsgs);
                    if (attempt.Success)
                        attempt = StrategyCanPublish(content, userId, evtMsgs);
                    if (attempt.Success == false)
                    {
                        attempts = new[] { attempt };
                        return;
                    }

                    var contents = new List<IContent> { content }; //include parent item
                    contents.AddRange(GetDescendants(content));

                    // publish using the strategy - for descendants,
                    // - published w/out changes: nothing to do
                    // - published w/changes: publish those changes
                    // - unpublished: publish if includeUnpublished, otherwise ignroe
                    var alreadyChecked = new[] { content };
                    attempts = StrategyPublishWithChildren(contents, alreadyChecked, userId, evtMsgs, includeUnpublished).ToArray();

                    foreach (var status in attempts.Where(x => x.Success).Select(x => x.Result))
                    {
                        // save them all, even those that are .Success because of (.StatusType == PublishStatusType.SuccessAlreadyPublished)
                        // so we bump the date etc
                        var publishedItem = status.ContentItem;
                        publishedItem.WriterId = userId;
                        xr.Repository.AddOrUpdate(publishedItem);
                        publishedItems.Add(publishedItem);
                    }
                });
            }

            if (attempts.All(x => x.Success == false))
                return attempts;

            Published.RaiseEvent(new PublishEventArgs<IContent>(publishedItems, false, false, evtMsgs), this);
            TreeChanged.RaiseEvent(new TreeChange<IContent>(content, TreeChangeTypes.RefreshBranch).ToEventArgs(), this);
            Audit(AuditType.Publish, "Publish with Children performed by user", userId, content.Id);
            return attempts;
        }

        /// <summary>
        /// Unpublishes a single <see cref="IContent"/> object.
        /// </summary>
        /// <param name="content">The <see cref="IContent"/> to publish.</param>
        /// <param name="userId">Optional unique identifier of the User issueing the unpublishing.</param>
        /// <returns>True if unpublishing succeeded, otherwise False.</returns>
        private Attempt<UnPublishStatus> UnPublishDo(IContent content, int userId = 0)
        {
            var returnAttempt = false;
            var attempt = default(Attempt<UnPublishStatus>);
            var evtMsgs = EventMessagesFactory.Get();

            using (ChangeSet.WithAmbient)
            {
                _lrepo.WithWriteLocked(xr =>
                {
                    var newest = GetById(content.Id); // ensure we have the newest version
                    if (content.Version != newest.Version) // but use the original object if it's already the newest version
                        content = newest;
                    if (content.Published == false && content.HasPublishedVersion == false)
                    {
                        attempt = Attempt.Succeed(new UnPublishStatus(content, UnPublishedStatusType.SuccessAlreadyUnPublished, evtMsgs));
                        returnAttempt = true;
                        return;
                    }

                    // strategy
                    attempt = StrategyUnPublish(content, userId, evtMsgs);
                    if (attempt.Success == false)
                    {
                        returnAttempt = true;
                        return;
                    }

                    content.WriterId = userId;
                    xr.Repository.AddOrUpdate(content);
                });
            }

            if (returnAttempt)
                return attempt;

            UnPublished.RaiseEvent(new PublishEventArgs<IContent>(content, false, false), this);
            TreeChanged.RaiseEvent(new TreeChange<IContent>(content, TreeChangeTypes.RefreshBranch).ToEventArgs(), this);
            Audit(AuditType.UnPublish, "UnPublish performed by user", userId, content.Id);
            return Attempt.Succeed(new UnPublishStatus(content, UnPublishedStatusType.Success, evtMsgs));
        }

        /// <summary>
        /// Saves and Publishes a single <see cref="IContent"/> object
        /// </summary>
        /// <param name="content">The <see cref="IContent"/> to save and publish</param>
        /// <param name="userId">Optional Id of the User issueing the publishing</param>
        /// <param name="raiseEvents">Optional boolean indicating whether or not to raise save events.</param>
        /// <returns>True if publishing succeeded, otherwise False</returns>
        private Attempt<PublishStatus> SaveAndPublishDo(IContent content, int userId = 0, bool raiseEvents = true)
        {
            var evtMsgs = EventMessagesFactory.Get();

            if (raiseEvents && Saving.IsRaisedEventCancelled(new SaveEventArgs<IContent>(content, evtMsgs), this))
                return Attempt.Fail(new PublishStatus(content, PublishStatusType.FailedCancelledByEvent, evtMsgs));

            var isNew = content.IsNewEntity();
            var changeType = isNew ? TreeChangeTypes.RefreshBranch : TreeChangeTypes.RefreshNode;
            var previouslyPublished = content.HasIdentity && content.HasPublishedVersion;
            var status = default(Attempt<PublishStatus>);

            _lrepo.WithWriteLocked(xr =>
            {
                // ensure content is publishable, and try to publish
                status = EnsurePublishable(content, evtMsgs);
                if (status.Success)
                {
                    // strategy handles events, and various business rules eg release & expire
                    // dates, trashed status...
                    status = StrategyPublish(content, false, userId, evtMsgs);
                }

                // save - aways, even if not publishing (this is SaveAndPublish)
                if (content.HasIdentity == false)
                    content.CreatorId = userId;
                content.WriterId = userId;

                xr.Repository.AddOrUpdate(content);
            });

            if (raiseEvents)
                Saved.RaiseEvent(new SaveEventArgs<IContent>(content, false, evtMsgs), this);

            if (status.Success == false)
            {
                // notify it's been saved
                using (ChangeSet.WithAmbient)
                    TreeChanged.RaiseEvent(new TreeChange<IContent>(content, changeType).ToEventArgs(), this);
                return status;
            }

            Published.RaiseEvent(new PublishEventArgs<IContent>(content, false, false, evtMsgs), this);

            // if was not published and now is... descendants that were 'published' (but 
            // had an unpublished ancestor) are 're-published' ie not explicitely published
            // but back as 'published' nevertheless
            if (isNew == false && previouslyPublished == false)
            {
                if (HasChildren(content.Id))
                {
                    var descendants = GetPublishedDescendants(content).ToArray();
                    Published.RaiseEvent(new PublishEventArgs<IContent>(descendants, false, false, evtMsgs), this);
                }
                changeType = TreeChangeTypes.RefreshBranch; // whole branch
            }

            // invalidate the node/branch
            using (ChangeSet.WithAmbient)
                TreeChanged.RaiseEvent(new TreeChange<IContent>(content, changeType).ToEventArgs(), this);
            Audit(AuditType.Publish, "Save and Publish performed by user", userId, content.Id);
            return status;
        }

        private Attempt<PublishStatus> EnsurePublishable(IContent content, EventMessages evtMsgs)
        {
            // root content can be published
            if (content.ParentId == Constants.System.Root)
                return Attempt<PublishStatus>.Succeed();

            // trashed content cannot be published
            if (content.ParentId != Constants.System.RecycleBinContent)
            {
                // ensure all ancestors are published
                // because content may be new its Path may be null - start with parent
                var path = content.Path ?? content.Parent().Path;
                if (path != null) // if parent is also null, give up
                {
                    var ancestorIds = path.Split(',')
                        .Skip(1) // remove leading "-1"
                        .Reverse()
                        .Select(int.Parse);
                    if (content.Path != null)
                        ancestorIds = ancestorIds.Skip(1); // remove trailing content.Id

                    if (ancestorIds.All(HasPublishedVersion))
                        return Attempt<PublishStatus>.Succeed();
                }
            }

            Logger.Info<ContentService>(
                string.Format( "Content '{0}' with Id '{1}' could not be published because its parent is not published.", content.Name, content.Id));
            return Attempt.Fail(new PublishStatus(content, PublishStatusType.FailedPathNotPublished, evtMsgs));
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Occurs before Delete
        /// </summary>		
        public static event TypedEventHandler<IContentService, DeleteEventArgs<IContent>> Deleting;

        /// <summary>
        /// Occurs after Delete
        /// </summary>
        public static event TypedEventHandler<IContentService, DeleteEventArgs<IContent>> Deleted;

        /// <summary>
        /// Occurs before Delete Versions
        /// </summary>		
        public static event TypedEventHandler<IContentService, DeleteRevisionsEventArgs> DeletingVersions;

        /// <summary>
        /// Occurs after Delete Versions
        /// </summary>
        public static event TypedEventHandler<IContentService, DeleteRevisionsEventArgs> DeletedVersions;

        /// <summary>
        /// Occurs before Save
        /// </summary>
        public static event TypedEventHandler<IContentService, SaveEventArgs<IContent>> Saving;

        /// <summary>
        /// Occurs after Save
        /// </summary>
        public static event TypedEventHandler<IContentService, SaveEventArgs<IContent>> Saved;

        /// <summary>
        /// Occurs before Create
        /// </summary>
        [Obsolete("Use the Created event instead, the Creating and Created events both offer the same functionality, Creating event has been deprecated.")]
        public static event TypedEventHandler<IContentService, NewEventArgs<IContent>> Creating;

        /// <summary>
        /// Occurs after Create
        /// </summary>
        /// <remarks>
        /// Please note that the Content object has been created, but might not have been saved
        /// so it does not have an identity yet (meaning no Id has been set).
        /// </remarks>
        public static event TypedEventHandler<IContentService, NewEventArgs<IContent>> Created;

        /// <summary>
        /// Occurs before Copy
        /// </summary>
        public static event TypedEventHandler<IContentService, CopyEventArgs<IContent>> Copying;

        /// <summary>
        /// Occurs after Copy
        /// </summary>
        public static event TypedEventHandler<IContentService, CopyEventArgs<IContent>> Copied;

        /// <summary>
        /// Occurs before Content is moved to Recycle Bin
        /// </summary>
        public static event TypedEventHandler<IContentService, MoveEventArgs<IContent>> Trashing;

        /// <summary>
        /// Occurs after Content is moved to Recycle Bin
        /// </summary>
        public static event TypedEventHandler<IContentService, MoveEventArgs<IContent>> Trashed;

        /// <summary>
        /// Occurs before Move
        /// </summary>
        public static event TypedEventHandler<IContentService, MoveEventArgs<IContent>> Moving;

        /// <summary>
        /// Occurs after Move
        /// </summary>
        public static event TypedEventHandler<IContentService, MoveEventArgs<IContent>> Moved;

        /// <summary>
        /// Occurs before Rollback
        /// </summary>
        public static event TypedEventHandler<IContentService, RollbackEventArgs<IContent>> RollingBack;

        /// <summary>
        /// Occurs after Rollback
        /// </summary>
        public static event TypedEventHandler<IContentService, RollbackEventArgs<IContent>> RolledBack;

        /// <summary>
        /// Occurs before Send to Publish
        /// </summary>
        public static event TypedEventHandler<IContentService, SendToPublishEventArgs<IContent>> SendingToPublish;

        /// <summary>
        /// Occurs after Send to Publish
        /// </summary>
        public static event TypedEventHandler<IContentService, SendToPublishEventArgs<IContent>> SentToPublish;

        /// <summary>
        /// Occurs before the Recycle Bin is emptied
        /// </summary>
        public static event TypedEventHandler<IContentService, RecycleBinEventArgs> EmptyingRecycleBin;

        /// <summary>
        /// Occurs after the Recycle Bin has been Emptied
        /// </summary>
        public static event TypedEventHandler<IContentService, RecycleBinEventArgs> EmptiedRecycleBin;

        /// <summary>
        /// Occurs before publish
        /// </summary>
        public static event TypedEventHandler<IContentService, PublishEventArgs<IContent>> Publishing;

        /// <summary>
        /// Occurs after publish
        /// </summary>
        public static event TypedEventHandler<IContentService, PublishEventArgs<IContent>> Published;

        /// <summary>
        /// Occurs before unpublish
        /// </summary>
        public static event TypedEventHandler<IContentService, PublishEventArgs<IContent>> UnPublishing;

        /// <summary>
        /// Occurs after unpublish
        /// </summary>
        public static event TypedEventHandler<IContentService, PublishEventArgs<IContent>> UnPublished;

        /// <summary>
        /// Occurs after change.
        /// </summary>
        internal static event TypedEventHandler<IContentService, TreeChange<IContent>.EventArgs> TreeChanged; 

        #endregion

        #region Publishing strategies

        // prob. want to find nicer names?

        internal Attempt<PublishStatus> StrategyCanPublish(IContent content, int userId, EventMessages evtMsgs)
        {
            if (Publishing.IsRaisedEventCancelled(new PublishEventArgs<IContent>(content, evtMsgs), this))
            {
                Logger.Info<ContentService>(
                    string.Format("Content '{0}' with Id '{1}' will not be published, the event was cancelled.", content.Name, content.Id));
                return Attempt.Fail(new PublishStatus(content, PublishStatusType.FailedCancelledByEvent, evtMsgs));
            }

            // check if the content is valid
            if (content.IsValid() == false)
            {
                Logger.Info<ContentService>(
                    string.Format("Content '{0}' with Id '{1}' could not be published because of invalid properties.", content.Name, content.Id));
                return Attempt.Fail(new PublishStatus(content, PublishStatusType.FailedContentInvalid, evtMsgs)
                {
                    InvalidProperties = ((ContentBase)content).LastInvalidProperties
                });
            }

            // check if the Content is Expired
            if (content.Status == ContentStatus.Expired)
            {
                Logger.Info<ContentService>(
                    string.Format("Content '{0}' with Id '{1}' has expired and could not be published.", content.Name, content.Id));
                return Attempt.Fail(new PublishStatus(content, PublishStatusType.FailedHasExpired, evtMsgs));
            }

            // check if the Content is Awaiting Release
            if (content.Status == ContentStatus.AwaitingRelease)
            {
                Logger.Info<ContentService>(
                    string.Format("Content '{0}' with Id '{1}' is awaiting release and could not be published.", content.Name, content.Id));
                return Attempt.Fail(new PublishStatus(content, PublishStatusType.FailedAwaitingRelease, evtMsgs));
            }

            // check if the Content is Trashed
            if (content.Status == ContentStatus.Trashed)
            {
                Logger.Info<ContentService>(
                    string.Format("Content '{0}' with Id '{1}' is trashed and could not be published.", content.Name, content.Id));
                return Attempt.Fail(new PublishStatus(content, PublishStatusType.FailedIsTrashed, evtMsgs));
            }

            return Attempt.Succeed(new PublishStatus(content, evtMsgs));
        }

        internal Attempt<PublishStatus> StrategyPublish(IContent content, bool alreadyCheckedCanPublish, int userId, EventMessages evtMsgs)
        {
            var attempt = alreadyCheckedCanPublish
                ? Attempt.Succeed(new PublishStatus(content, evtMsgs)) // already know we can
                : StrategyCanPublish(content, userId, evtMsgs); // else check
            if (attempt.Success == false)
                return attempt;

            // change state to publishing
            content.ChangePublishedState(PublishedState.Publishing);

            Logger.Info<ContentService>(
                string.Format("Content '{0}' with Id '{1}' has been published.", content.Name, content.Id));

            return attempt;
        }

        ///  <summary>
        ///  Publishes a list of content items
        ///  </summary>
        ///  <param name="contents">Contents, ordered by level ASC</param>
        /// <param name="alreadyChecked">Contents for which we've already checked CanPublish</param>
        /// <param name="userId"></param>
        /// <param name="evtMsgs"></param>
        /// <param name="includeUnpublished">Indicates whether to publish content that is completely unpublished (has no published
        ///  version). If false, will only publish already published content with changes. Also impacts what happens if publishing
        ///  fails (see remarks).</param>        
        ///  <returns></returns>
        ///  <remarks>
        ///  Navigate content & descendants top-down and for each,
        ///  - if it is published
        ///    - and unchanged, do nothing
        ///    - else (has changes), publish those changes
        ///  - if it is not published
        ///    - and at top-level, publish
        ///    - or includeUnpublished is true, publish
        ///    - else do nothing & skip the underlying branch
        /// 
        ///  When publishing fails
        ///  - if content has no published version, skip the underlying branch
        ///  - else (has published version),
        ///    - if includeUnpublished is true, process the underlying branch
        ///    - else, do not process the underlying branch
        ///  </remarks>
        internal IEnumerable<Attempt<PublishStatus>> StrategyPublishWithChildren(IEnumerable<IContent> contents, IEnumerable<IContent> alreadyChecked, int userId, EventMessages evtMsgs, bool includeUnpublished = true)
        {
            var statuses = new List<Attempt<PublishStatus>>();
            var alreadyCheckedA = (alreadyChecked ?? Enumerable.Empty<IContent>()).ToArray();

            // list of ids that we exclude because they could not be published
            var excude = new List<int>();

            var topLevel = -1;
            foreach (var content in contents)
            {
                // initialize - content is ordered by level ASC
                if (topLevel < 0)
                    topLevel = content.Level;

                if (excude.Contains(content.ParentId))
                {
                    // parent is excluded, so exclude content too
                    Logger.Info<ContentService>(
                        string.Format("Content '{0}' with Id '{1}' will not be published because it's parent's publishing action failed or was cancelled.", content.Name, content.Id));
                    excude.Add(content.Id);
                    // status has been reported for an ancestor and that one is excluded => no status
                    continue;
                }

                if (content.Published && content.Level > topLevel) // topLevel we DO want to (re)publish
                {
                    // newest is published already
                    statuses.Add(Attempt.Succeed(new PublishStatus(content, PublishStatusType.SuccessAlreadyPublished, evtMsgs)));
                    continue;
                }

                if (content.HasPublishedVersion)
                {
                    // newest is published already but we are topLevel, or
                    // newest is not published, but another version is - publish newest
                    var r = StrategyPublish(content, alreadyCheckedA.Contains(content), userId, evtMsgs);
                    if (r.Success == false)
                    {
                        // we tried to publish and it failed, but it already had / still has a published version,
                        // the rule in remarks says that we should skip the underlying branch if includeUnpublished
                        // is false, else process it - not that it makes much sense, but keep it like that for now
                        if (includeUnpublished == false)
                            excude.Add(content.Id);
                    }

                    statuses.Add(r);
                    continue;
                }

                if (content.Level == topLevel || includeUnpublished)
                {
                    // content has no published version, and we want to publish it, either
                    // because it is top-level or because we include unpublished.
                    // if publishing fails, and because content does not have a published 
                    // version at all, ensure we do not process its descendants
                    var r = StrategyPublish(content, alreadyCheckedA.Contains(content), userId, evtMsgs);
                    if (r.Success == false)
                        excude.Add(content.Id);

                    statuses.Add(r);
                    continue;
                }

                // content has no published version, and we don't want to publish it
                excude.Add(content.Id); // ignore everything below it
                // content is not even considered, really => no status
            }

            return statuses;
        }

        internal Attempt<UnPublishStatus> StrategyUnPublish(IContent content, int userId, EventMessages evtMsgs)
        {
            // content should (is assumed to) be the newest version, which may not be published,
            // don't know how to test this, so it's not verified

            // fire UnPublishing event
            if (UnPublishing.IsRaisedEventCancelled(new PublishEventArgs<IContent>(content, evtMsgs), this))
            {
                Logger.Info<ContentService>(
                    string.Format("Content '{0}' with Id '{1}' will not be unpublished, the event was cancelled.", content.Name, content.Id));
                return Attempt.Fail(new UnPublishStatus(content, UnPublishedStatusType.FailedCancelledByEvent, evtMsgs));
            }

            // if Content has a release date set to before now, it should be removed so it doesn't interrupt an unpublish
            // otherwise it would remain released == published
            if (content.ReleaseDate.HasValue && content.ReleaseDate.Value <= DateTime.Now)
            {
                content.ReleaseDate = null;
                Logger.Info<ContentService>(
                    string.Format("Content '{0}' with Id '{1}' had its release date removed, because it was unpublished.", content.Name, content.Id));
            }

            // version is published or unpublished, but content is published
            // change state to unpublishing
            content.ChangePublishedState(PublishedState.Unpublishing);

            Logger.Info<ContentService>(
                string.Format("Content '{0}' with Id '{1}' has been unpublished.", content.Name, content.Id));

            return Attempt.Succeed(new UnPublishStatus(content, evtMsgs));
        }

        internal IEnumerable<Attempt<UnPublishStatus>> StrategyUnPublish(IEnumerable<IContent> content, int userId, EventMessages evtMsgs)
        {
            return content.Select(x => StrategyUnPublish(x, userId, evtMsgs));
        }

        #endregion

        #region Content Types

        /// <summary>
        /// Deletes all content of specified type. All children of deleted content is moved to Recycle Bin.
        /// </summary>
        /// <remarks>This needs extra care and attention as its potentially a dangerous and extensive operation</remarks>
        /// <param name="contentTypeId">Id of the <see cref="IContentType"/></param>
        /// <param name="userId">Optional Id of the user issueing the delete operation</param>
        public void DeleteContentOfType(int contentTypeId, int userId = 0)
        {
            var changes = new List<TreeChange<IContent>>();
            var moves = new List<Tuple<IContent, string>>();
            var evtMsgs = EventMessagesFactory.Get();

            using (ChangeSet.WithAmbient)
            {
                _lrepo.WithWriteLocked(xr =>
                {
                    var query = Query<IContent>.Builder.Where(x => x.ContentTypeId == contentTypeId);
                    var contents = xr.Repository.GetByQuery(query).ToArray();

                    if (Deleting.IsRaisedEventCancelled(new DeleteEventArgs<IContent>(contents, evtMsgs), this))
                        return;

                    // order by level, descending, so deepest first - that way, we cannot move
                    // a content of the deleted type, to the recycle bin (and then delete it...)
                    foreach (var content in contents.OrderByDescending(x => x.Level))
                    {
                        // if it's not trashed yet, and published, we should unpublish
                        // but... UnPublishing event makes no sense (not going to cancel?) and no need to save
                        // just raise the event
                        if (content.Trashed == false && content.HasPublishedVersion)
                            UnPublished.RaiseEvent(new PublishEventArgs<IContent>(content, false, false, evtMsgs), this);

                        // if current content has children, move them to trash
                        var c = content;
                        var childQuery = Query<IContent>.Builder.Where(x => x.Path.StartsWith(c.Path));
                        var children = xr.Repository.GetByQuery(childQuery);
                        foreach (var child in children.Where(x => x.ContentTypeId != contentTypeId))
                        {
                            // see MoveToRecycleBin
                            PerformMoveLocked(child, Constants.System.RecycleBinContent, null, userId, moves, true, xr.Repository);
                            changes.Add(new TreeChange<IContent>(content, TreeChangeTypes.RefreshBranch));
                        }

                        // delete content
                        // triggers the deleted event (and handles the files)
                        DeleteLocked(content, xr.Repository, evtMsgs);
                        changes.Add(new TreeChange<IContent>(content, TreeChangeTypes.Remove));
                    }
                });

                var moveInfos = moves
                    .Select(x => new MoveEventInfo<IContent>(x.Item1, x.Item2, x.Item1.ParentId))
                    .ToArray();
                if (moveInfos.Length > 0)
                    Trashed.RaiseEvent(new MoveEventArgs<IContent>(false, moveInfos), this);
                TreeChanged.RaiseEvent(changes.ToEventArgs(), this);
            }

            Audit(AuditType.Delete,
                        string.Format("Delete Content of Type {0} performed by user", contentTypeId),
                        userId, Constants.System.Root);
        }

        private IContentType GetContentType(string contentTypeAlias)
        {
            var contentType = ContentTypeService.Get(contentTypeAlias);
            if (contentType == null)
                throw new Exception(string.Format("No ContentType matching alias: \"{0}\".", contentTypeAlias));
            return contentType;
        }

        #endregion
    }
}