using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Xml.Linq;
using Umbraco.Core.Events;
using Umbraco.Core.Models;
using Umbraco.Core.Models.Membership;
using Umbraco.Core.Persistence.DatabaseModelDefinitions;
using Umbraco.Core.Persistence.Querying;
using Umbraco.Core.Services;

namespace Umbraco.Core.Persistence.Repositories
{
    public interface IContentRepository : IRepositoryVersionable<int, IContent>, IRecycleBinRepository<IContent>
    {
        /// <summary>
        /// Get the count of published items
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// We require this on the repo because the IQuery{IContent} cannot supply the 'newest' parameter
        /// </remarks>
        int CountPublished(string contentTypeAlias = null);

        /// <summary>
        /// Used to bulk update the permissions set for a content item. This will replace all permissions
        /// assigned to an entity with a list of user id & permission pairs.
        /// </summary>
        /// <param name="permissionSet"></param>
        void ReplaceContentPermissions(EntityPermissionSet permissionSet);

        /// <summary>
        /// Gets all published Content by the specified query
        /// </summary>
        /// <param name="query">Query to execute against published versions</param>
        /// <returns>An enumerable list of <see cref="IContent"/></returns>
        IEnumerable<IContent> GetByPublishedVersion(IQuery<IContent> query);

        /// <summary>
        /// Assigns a single permission to the current content item for the specified user ids
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="permission"></param>
        /// <param name="userIds"></param>
        void AssignEntityPermission(IContent entity, char permission, IEnumerable<int> userIds);

        /// <summary>
        /// Gets the list of permissions for the content item
        /// </summary>
        /// <param name="entityId"></param>
        /// <returns></returns>
        IEnumerable<EntityPermission> GetPermissionsForEntity(int entityId);

        /// <summary>
        /// Gets paged content results
        /// </summary>
        /// <param name="query">Query to excute</param>
        /// <param name="pageIndex">Page number</param>
        /// <param name="pageSize">Page size</param>
        /// <param name="totalRecords">Total records query would return without paging</param>
        /// <param name="orderBy">Field to order by</param>
        /// <param name="orderDirection">Direction to order by</param>
        /// <param name="orderBySystemField">Flag to indicate when ordering by system field</param>
        /// <param name="filter">Search text filter</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        IEnumerable<IContent> GetPagedResultsByQuery(IQuery<IContent> query, long pageIndex, int pageSize, out long totalRecords,
            string orderBy, Direction orderDirection, bool orderBySystemField, string filter = "");

        /// <summary>
        /// Gets a value indicating whether a specified content is path-published, ie whether it is published
        /// and all its ancestors are published too and none are trashed.
        /// </summary>
        /// <param name="content">The content.</param>
        /// <returns>true if the content is path-published; otherwise, false.</returns>
        bool IsPathPublished(IContent content);
    }
}