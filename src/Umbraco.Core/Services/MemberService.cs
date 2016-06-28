using System;
using System.Collections.Generic;
using System.Data;
using System.ComponentModel;
using System.Threading;
using System.Web.Security;
using System.Xml.Linq;
using Umbraco.Core.Auditing;
using Umbraco.Core.Configuration;
using Umbraco.Core.Events;
using Umbraco.Core.IO;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Models.Membership;
using Umbraco.Core.Models.Rdbms;
using Umbraco.Core.Persistence;
using Umbraco.Core.Persistence.DatabaseModelDefinitions;
using Umbraco.Core.Persistence.Querying;
using Umbraco.Core.Persistence.Repositories;
using Umbraco.Core.Persistence.SqlSyntax;
using Umbraco.Core.Persistence.UnitOfWork;
using System.Linq;
using Umbraco.Core.Security;

namespace Umbraco.Core.Services
{
    /// <summary>
    /// Represents the MemberService.
    /// </summary>
    public class MemberService : RepositoryService, IMemberService
    {
        private readonly IMemberGroupService _memberGroupService;
        private IMemberTypeService _memberTypeService;

        #region Constructor

        public MemberService(IDatabaseUnitOfWorkProvider provider, RepositoryFactory repositoryFactory, ILogger logger, IEventMessagesFactory eventMessagesFactory, IMemberGroupService memberGroupService, IDataTypeService dataTypeService)
            : base(provider, repositoryFactory, logger, eventMessagesFactory)
        {
            // though... these are not used?
            Mandate.ParameterNotNull(dataTypeService, "dataTypeService");
            Mandate.ParameterNotNull(memberGroupService, "memberGroupService");

            _memberGroupService = memberGroupService;

            _lmrepo = new LockingRepository<MemberRepository>(UowProvider,
                uow => RepositoryFactory.CreateMemberRepository(uow) as MemberRepository,
                LockingRepositoryLockIds, LockingRepositoryLockIds);

            _lgrepo = new LockingRepository<MemberGroupRepository>(UowProvider,
                uow => RepositoryFactory.CreateMemberGroupRepository(uow) as MemberGroupRepository,
                LockingRepositoryLockIds, LockingRepositoryLockIds);
        }

        internal IMemberTypeService MemberTypeService
        {
            get
            {
                if (_memberTypeService == null)
                    throw new InvalidOperationException("MemberService.MemberTypeService has not been initialized.");
                return _memberTypeService;
            }
            set { _memberTypeService = value; }
        }

        #endregion

        #region Locking

        // constant
        private static readonly int[] LockingRepositoryLockIds = { Constants.System.MemberTreeLock };

        private readonly LockingRepository<MemberRepository> _lmrepo;
        private readonly LockingRepository<MemberGroupRepository> _lgrepo;

        private void WithReadLocked(Action<MemberRepository> action, bool autoCommit = true)
        {
            _lmrepo.WithReadLocked(xr => action(xr.Repository), autoCommit);
        }

        internal TResult WithReadLocked<TResult>(Func<MemberRepository, TResult> func, bool autoCommit = true)
        {
            return _lmrepo.WithReadLocked(xr => func(xr.Repository), autoCommit);
        }

        internal void WithWriteLocked(Action<MemberRepository> action, bool autoCommit = true)
        {
            _lmrepo.WithWriteLocked(xr => action(xr.Repository), autoCommit);
        }

        private TResult WithReadLockedGroups<TResult>(Func<MemberGroupRepository, TResult> func, bool autoCommit = true)
        {
            return _lgrepo.WithReadLocked(xr => func(xr.Repository), autoCommit);
        }

        private void WithWriteLockedGroups(Action<MemberGroupRepository> action, bool autoCommit = true)
        {
            _lgrepo.WithWriteLocked(xr => action(xr.Repository), autoCommit);
        }

        #endregion

        #region Count

        /// <summary>
        /// Gets the total number of Members based on the count type
        /// </summary>
        /// <remarks>
        /// The way the Online count is done is the same way that it is done in the MS SqlMembershipProvider - We query for any members
        /// that have their last active date within the Membership.UserIsOnlineTimeWindow (which is in minutes). It isn't exact science
        /// but that is how MS have made theirs so we'll follow that principal.
        /// </remarks>
        /// <param name="countType"><see cref="MemberCountType"/> to count by</param>
        /// <returns><see cref="System.int"/> with number of Members for passed in type</returns>
        public int GetCount(MemberCountType countType)
        {
            IQuery<IMember> query;

            if (countType == MemberCountType.All)
                return WithReadLocked(repository => repository.Count(new Query<IMember>()));

            switch (countType)
            {
                case MemberCountType.Online:
                    var fromDate = DateTime.Now.AddMinutes(-Membership.UserIsOnlineTimeWindow);
                    query =
                        Query<IMember>.Builder.Where(x =>
                            ((Member)x).PropertyTypeAlias == Constants.Conventions.Member.LastLoginDate &&
                            ((Member)x).DateTimePropertyValue > fromDate);
                    break;
                case MemberCountType.LockedOut:
                    query =
                        Query<IMember>.Builder.Where(x =>
                            ((Member)x).PropertyTypeAlias == Constants.Conventions.Member.IsLockedOut &&
                            ((Member)x).BoolPropertyValue == true);
                    break;
                case MemberCountType.Approved:
                    query =
                        Query<IMember>.Builder.Where(x =>
                            ((Member)x).PropertyTypeAlias == Constants.Conventions.Member.IsApproved &&
                            ((Member)x).BoolPropertyValue == true);
                    break;
                default:
                    throw new ArgumentOutOfRangeException("countType");
            }

            return WithReadLocked(repository => repository.GetCountByQuery(query));
        }

        /// <summary>
        /// Gets the count of Members by an optional MemberType alias
        /// </summary>
        /// <remarks>If no alias is supplied then the count for all Member will be returned</remarks>
        /// <param name="memberTypeAlias">Optional alias for the MemberType when counting number of Members</param>
        /// <returns><see cref="System.int"/> with number of Members</returns>
        public int Count(string memberTypeAlias = null)
        {
            return WithReadLocked(repository => repository.Count(memberTypeAlias));
        }

        #endregion

        #region Create

        /// <summary>
        /// Creates an <see cref="IMember"/> object without persisting it
        /// </summary>
        /// <remarks>This method is convenient for when you need to add properties to a new Member
        /// before persisting it in order to limit the amount of times its saved.
        /// Also note that the returned <see cref="IMember"/> will not have an Id until its saved.</remarks>
        /// <param name="username">Username of the Member to create</param>
        /// <param name="email">Email of the Member to create</param>
        /// <param name="name">Name of the Member to create</param>
        /// <param name="memberTypeAlias">Alias of the MemberType the Member should be based on</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember CreateMember(string username, string email, string name, string memberTypeAlias)
        {
            var memberType = GetMemberType(memberTypeAlias);
            var member = new Member(name, email.ToLower().Trim(), username, memberType);
            CreateMember(member, 0, false);
            return member;
        }

        /// <summary>
        /// Creates an <see cref="IMember"/> object without persisting it
        /// </summary>
        /// <remarks>This method is convenient for when you need to add properties to a new Member
        /// before persisting it in order to limit the amount of times its saved.
        /// Also note that the returned <see cref="IMember"/> will not have an Id until its saved.</remarks>
        /// <param name="username">Username of the Member to create</param>
        /// <param name="email">Email of the Member to create</param>
        /// <param name="name">Name of the Member to create</param>
        /// <param name="memberType">MemberType the Member should be based on</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember CreateMember(string username, string email, string name, IMemberType memberType)
        {
            var member = new Member(name, email.ToLower().Trim(), username, memberType);
            CreateMember(member, 0, false);
            return member;
        }

        /// <summary>
        /// Creates and persists a Member
        /// </summary>
        /// <remarks>Using this method will persist the Member object before its returned 
        /// meaning that it will have an Id available (unlike the CreateMember method)</remarks>
        /// <param name="username">Username of the Member to create</param>
        /// <param name="email">Email of the Member to create</param>
        /// <param name="name">Name of the Member to create</param>
        /// <param name="memberTypeAlias">Alias of the MemberType the Member should be based on</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember CreateMemberWithIdentity(string username, string email, string name, string memberTypeAlias)
        {
            var memberType = GetMemberType(memberTypeAlias);
            var member = new Member(name, email.ToLower().Trim(), username, memberType);
            CreateMember(member, 0, true);
            return member;
        }

        /// <summary>
        /// Creates and persists a Member
        /// </summary>
        /// <remarks>Using this method will persist the Member object before its returned 
        /// meaning that it will have an Id available (unlike the CreateMember method)</remarks>
        /// <param name="username">Username of the Member to create</param>
        /// <param name="email">Email of the Member to create</param>
        /// <param name="memberType">MemberType the Member should be based on</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember CreateMemberWithIdentity(string username, string email, IMemberType memberType)
        {
            var member = new Member(username, email.ToLower().Trim(), username, memberType);
            CreateMember(member, 0, true);
            return member;
        }

        /// <summary>
        /// Creates and persists a Member
        /// </summary>
        /// <remarks>Using this method will persist the Member object before its returned 
        /// meaning that it will have an Id available (unlike the CreateMember method)</remarks>
        /// <param name="username">Username of the Member to create</param>
        /// <param name="email">Email of the Member to create</param>
        /// <param name="name">Name of the Member to create</param>
        /// <param name="memberType">MemberType the Member should be based on</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember CreateMemberWithIdentity(string username, string email, string name, IMemberType memberType)
        {
            var member = new Member(name, email.ToLower().Trim(), username, memberType);
            CreateMember(member, 0, true);
            return member;
        }

        /// <summary>
        /// Creates and persists a new <see cref="IMember"/>
        /// </summary>
        /// <remarks>An <see cref="IMembershipUser"/> can be of type <see cref="IMember"/> or <see cref="IUser"/></remarks>
        /// <param name="username">Username of the <see cref="IMembershipUser"/> to create</param>
        /// <param name="email">Email of the <see cref="IMembershipUser"/> to create</param>
        /// <param name="passwordValue">This value should be the encoded/encrypted/hashed value for the password that will be stored in the database</param>
        /// <param name="memberTypeAlias">Alias of the Type</param>
        /// <returns><see cref="IMember"/></returns>
        IMember IMembershipMemberService<IMember>.CreateWithIdentity(string username, string email, string passwordValue, string memberTypeAlias)
        {
            var memberType = GetMemberType(memberTypeAlias);
            var member = new Member(username, email.ToLower().Trim(), username, passwordValue, memberType);
            CreateMember(member, 0, true);
            return member;
        }

        private void CreateMember(Member member, int userId, bool withIdentity)
        {
            // there's no Creating event for members

            if (Saving.IsRaisedEventCancelled(new SaveEventArgs<IMember>(member), this))
            {
                member.WasCancelled = true;
                return;
            }

            member.CreatorId = userId;

            if (withIdentity)
            {
                if (Saving.IsRaisedEventCancelled(new SaveEventArgs<IMember>(member), this))
                {
                    member.WasCancelled = true;
                    return;
                }

                WithWriteLocked(repository => repository.AddOrUpdate(member));

                Saved.RaiseEvent(new SaveEventArgs<IMember>(member, false), this);
            }

            Created.RaiseEvent(new NewEventArgs<IMember>(member, false, member.ContentType.Alias, -1), this);

            var msg = withIdentity
                ? "Member '{0}' was created with Id {1}"
                : "Member '{0}' was created";
            Audit(AuditType.New, string.Format(msg, member.Name, member.Id), member.CreatorId, member.Id);
        }
        
        #endregion

        #region Get, Has, Is, Exists...

        /// <summary>
        /// Gets a Member by its integer id
        /// </summary>
        /// <param name="id"><see cref="System.int"/> Id</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember GetById(int id)
        {
            return WithReadLocked(repository => repository.Get(id));
        }

        /// <summary>
        /// Gets a Member by the unique key
        /// </summary>
        /// <remarks>The guid key corresponds to the unique id in the database
        /// and the user id in the membership provider.</remarks>
        /// <param name="id"><see cref="Guid"/> Id</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember GetByKey(Guid id)
        {
            var query = Query<IMember>.Builder.Where(x => x.Key == id);
            return WithReadLocked(repository => repository.GetByQuery(query).FirstOrDefault());
        }

        [Obsolete("Use the overload with 'long' parameter types instead")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public IEnumerable<IMember> GetAll(int pageIndex, int pageSize, out int totalRecords)
        {
            long total;
            var result = GetAll(Convert.ToInt64(pageIndex), pageSize, out total);
            totalRecords = Convert.ToInt32(total);
            return result;
        }

        [Obsolete("Use the overload with 'long' parameter types instead")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public IEnumerable<IMember> GetAll(int pageIndex, int pageSize, out int totalRecords, string orderBy, Direction orderDirection, string memberTypeAlias = null, string filter = "")
        {
            long total;
            var result = GetAll(Convert.ToInt64(pageIndex), pageSize, out total, orderBy, orderDirection, memberTypeAlias, filter);
            totalRecords = Convert.ToInt32(total);
            return result;
        }

        /// <summary>
        /// Gets a list of paged <see cref="IMember"/> objects
        /// </summary>
        /// <param name="pageIndex">Current page index</param>
        /// <param name="pageSize">Size of the page</param>
        /// <param name="totalRecords">Total number of records found (out)</param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetAll(long pageIndex, int pageSize, out long totalRecords)
        {
            IEnumerable<IMember> ret = null;
            long totalRecords2 = 0;
            WithReadLocked(repository =>
            {
                ret = repository.GetPagedResultsByQuery(null, pageIndex, pageSize, out totalRecords2, "LoginName", Direction.Ascending, true);
            });
            totalRecords = totalRecords2;
            return ret;
        }

        public IEnumerable<IMember> GetAll(long pageIndex, int pageSize, out long totalRecords, string orderBy, Direction orderDirection, string memberTypeAlias = null, string filter = "")
        {
            return GetAll(pageIndex, pageSize, out totalRecords, orderBy, orderDirection, true, memberTypeAlias, filter);
        }

        public IEnumerable<IMember> GetAll(long pageIndex, int pageSize, out long totalRecords, string orderBy, Direction orderDirection, bool orderBySystemField, string memberTypeAlias, string filter)
        {
            IEnumerable<IMember> ret = null;
            long totalRecords2 = 0;
            WithReadLocked(repository =>
            {
                if (memberTypeAlias == null)
                {
                    ret = repository.GetPagedResultsByQuery(null, pageIndex, pageSize, out totalRecords2, orderBy, orderDirection, orderBySystemField, filter);
                }
                else
                {
                    var query = new Query<IMember>().Where(x => x.ContentTypeAlias == memberTypeAlias);
                    ret = repository.GetPagedResultsByQuery(query, pageIndex, pageSize, out totalRecords2, orderBy, orderDirection, orderBySystemField, filter);
                }
            });
            totalRecords = totalRecords2;
            return ret;
        }

        /// <summary>
        /// Gets an <see cref="IMember"/> by its provider key
        /// </summary>
        /// <param name="id">Id to use for retrieval</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember GetByProviderKey(object id)
        {
            var asGuid = id.TryConvertTo<Guid>();
            if (asGuid.Success)
                return GetByKey((Guid)id);

            var asInt = id.TryConvertTo<int>();
            if (asInt.Success)
                return GetById((int)id);

            return null;
        }

        /// <summary>
        /// Get an <see cref="IMember"/> by email
        /// </summary>
        /// <param name="email">Email to use for retrieval</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember GetByEmail(string email)
        {
            var query = Query<IMember>.Builder.Where(x => x.Email.Equals(email));
            return WithReadLocked(repository => repository.GetByQuery(query).FirstOrDefault());
        }

        /// <summary>
        /// Get an <see cref="IMember"/> by username
        /// </summary>
        /// <param name="username">Username to use for retrieval</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember GetByUsername(string username)
        {
            //TODO: Somewhere in here, whether at this level or the repository level, we need to add 
            // a caching mechanism since this method is used by all the membership providers and could be
            // called quite a bit when dealing with members.

            var query = Query<IMember>.Builder.Where(x => x.Username.Equals(username));
            return WithReadLocked(repository => repository.GetByQuery(query).FirstOrDefault());
        }

        /// <summary>
        /// Gets all Members for the specified MemberType alias
        /// </summary>
        /// <param name="memberTypeAlias">Alias of the MemberType</param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetMembersByMemberType(string memberTypeAlias)
        {
            var query = Query<IMember>.Builder.Where(x => x.ContentTypeAlias == memberTypeAlias);
            return WithReadLocked(repository => repository.GetByQuery(query));
        }

        /// <summary>
        /// Gets all Members for the MemberType id
        /// </summary>
        /// <param name="memberTypeId">Id of the MemberType</param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetMembersByMemberType(int memberTypeId)
        {
            var query = Query<IMember>.Builder.Where(x => x.ContentTypeId == memberTypeId);
            return WithReadLocked(repository => repository.GetByQuery(query));
        }

        /// <summary>
        /// Gets all Members within the specified MemberGroup name
        /// </summary>
        /// <param name="memberGroupName">Name of the MemberGroup</param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetMembersByGroup(string memberGroupName)
        {
            return WithReadLocked(repository => repository.GetByMemberGroup(memberGroupName));
        }

        /// <summary>
        /// Gets all Members with the ids specified
        /// </summary>
        /// <remarks>If no Ids are specified all Members will be retrieved</remarks>
        /// <param name="ids">Optional list of Member Ids</param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetAllMembers(params int[] ids)
        {
            return WithReadLocked(repository => repository.GetAll(ids));
        }

        private IEnumerable<IMember> FindMembersByQuery(Query<IMember> query, long pageIndex, int pageSize, out long totalRecords, string orderBy, Direction direction, bool orderBySystemField)
        {
            IEnumerable<IMember> ret = null;
            long totalRecords2 = 0;
            WithReadLocked(repository =>
            {
                ret = repository.GetPagedResultsByQuery(query, pageIndex, pageSize, out totalRecords2, orderBy, direction, orderBySystemField);
            });
            totalRecords = totalRecords2;
            return ret;
        }

        [Obsolete("Use the overload with 'long' parameter types instead")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public IEnumerable<IMember> FindMembersByDisplayName(string displayNameToMatch, int pageIndex, int pageSize, out int totalRecords, StringPropertyMatchType matchType = StringPropertyMatchType.StartsWith)
        {
            long total;
            var result = FindMembersByDisplayName(displayNameToMatch, Convert.ToInt64(pageIndex), pageSize, out total, matchType);
            totalRecords = Convert.ToInt32(total);
            return result;
        }

        /// <summary>
        /// Finds Members based on their display name
        /// </summary>
        /// <param name="displayNameToMatch">Display name to match</param>
        /// <param name="pageIndex">Current page index</param>
        /// <param name="pageSize">Size of the page</param>
        /// <param name="totalRecords">Total number of records found (out)</param>
        /// <param name="matchType">The type of match to make as <see cref="StringPropertyMatchType"/>. Default is <see cref="StringPropertyMatchType.StartsWith"/></param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> FindMembersByDisplayName(string displayNameToMatch, long pageIndex, int pageSize, out long totalRecords, StringPropertyMatchType matchType = StringPropertyMatchType.StartsWith)
        {
            var query = new Query<IMember>();

            switch (matchType)
            {
                case StringPropertyMatchType.Exact:
                    query.Where(member => member.Name.Equals(displayNameToMatch));
                    break;
                case StringPropertyMatchType.Contains:
                    query.Where(member => member.Name.Contains(displayNameToMatch));
                    break;
                case StringPropertyMatchType.StartsWith:
                    query.Where(member => member.Name.StartsWith(displayNameToMatch));
                    break;
                case StringPropertyMatchType.EndsWith:
                    query.Where(member => member.Name.EndsWith(displayNameToMatch));
                    break;
                case StringPropertyMatchType.Wildcard:
                    query.Where(member => member.Name.SqlWildcard(displayNameToMatch, TextColumnType.NVarchar));
                    break;
                default:
                    throw new ArgumentOutOfRangeException("matchType");
            }

            return FindMembersByQuery(query, pageIndex, pageSize, out totalRecords, "Name", Direction.Ascending, true);
        }

        [Obsolete("Use the overload with 'long' parameter types instead")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public IEnumerable<IMember> FindByEmail(string emailStringToMatch, int pageIndex, int pageSize, out int totalRecords, StringPropertyMatchType matchType = StringPropertyMatchType.StartsWith)
        {
            long total;
            var result = FindByEmail(emailStringToMatch, Convert.ToInt64(pageIndex), pageSize, out total, matchType);
            totalRecords = Convert.ToInt32(total);
            return result;
        }

        /// <summary>
        /// Finds a list of <see cref="IMember"/> objects by a partial email string
        /// </summary>
        /// <param name="emailStringToMatch">Partial email string to match</param>
        /// <param name="pageIndex">Current page index</param>
        /// <param name="pageSize">Size of the page</param>
        /// <param name="totalRecords">Total number of records found (out)</param>
        /// <param name="matchType">The type of match to make as <see cref="StringPropertyMatchType"/>. Default is <see cref="StringPropertyMatchType.StartsWith"/></param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> FindByEmail(string emailStringToMatch, long pageIndex, int pageSize, out long totalRecords, StringPropertyMatchType matchType = StringPropertyMatchType.StartsWith)
        {
            var query = new Query<IMember>();

            switch (matchType)
            {
                case StringPropertyMatchType.Exact:
                    query.Where(member => member.Email.Equals(emailStringToMatch));
                    break;
                case StringPropertyMatchType.Contains:
                    query.Where(member => member.Email.Contains(emailStringToMatch));
                    break;
                case StringPropertyMatchType.StartsWith:
                    query.Where(member => member.Email.StartsWith(emailStringToMatch));
                    break;
                case StringPropertyMatchType.EndsWith:
                    query.Where(member => member.Email.EndsWith(emailStringToMatch));
                    break;
                case StringPropertyMatchType.Wildcard:
                    query.Where(member => member.Email.SqlWildcard(emailStringToMatch, TextColumnType.NVarchar));
                    break;
                default:
                    throw new ArgumentOutOfRangeException("matchType");
            }

            return FindMembersByQuery(query, pageIndex, pageSize, out totalRecords, "Email", Direction.Ascending, true);
        }

        [Obsolete("Use the overload with 'long' parameter types instead")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public IEnumerable<IMember> FindByUsername(string login, int pageIndex, int pageSize, out int totalRecords, StringPropertyMatchType matchType = StringPropertyMatchType.StartsWith)
        {
            long total;
            var result = FindByUsername(login, Convert.ToInt64(pageIndex), pageSize, out total, matchType);
            totalRecords = Convert.ToInt32(total);
            return result;
        }

        /// <summary>
        /// Finds a list of <see cref="IMember"/> objects by a partial username
        /// </summary>
        /// <param name="login">Partial username to match</param>
        /// <param name="pageIndex">Current page index</param>
        /// <param name="pageSize">Size of the page</param>
        /// <param name="totalRecords">Total number of records found (out)</param>
        /// <param name="matchType">The type of match to make as <see cref="StringPropertyMatchType"/>. Default is <see cref="StringPropertyMatchType.StartsWith"/></param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> FindByUsername(string login, long pageIndex, int pageSize, out long totalRecords, StringPropertyMatchType matchType = StringPropertyMatchType.StartsWith)
        {
            var query = new Query<IMember>();

            switch (matchType)
            {
                case StringPropertyMatchType.Exact:
                    query.Where(member => member.Username.Equals(login));
                    break;
                case StringPropertyMatchType.Contains:
                    query.Where(member => member.Username.Contains(login));
                    break;
                case StringPropertyMatchType.StartsWith:
                    query.Where(member => member.Username.StartsWith(login));
                    break;
                case StringPropertyMatchType.EndsWith:
                    query.Where(member => member.Username.EndsWith(login));
                    break;
                case StringPropertyMatchType.Wildcard:
                    query.Where(member => member.Email.SqlWildcard(login, TextColumnType.NVarchar));
                    break;
                default:
                    throw new ArgumentOutOfRangeException("matchType");
            }

            return FindMembersByQuery(query, pageIndex, pageSize, out totalRecords, "LoginName", Direction.Ascending, true);
        }

        /// <summary>
        /// Gets a list of Members based on a property search
        /// </summary>
        /// <param name="propertyTypeAlias">Alias of the PropertyType to search for</param>
        /// <param name="value"><see cref="System.string"/> Value to match</param>
        /// <param name="matchType">The type of match to make as <see cref="StringPropertyMatchType"/>. Default is <see cref="StringPropertyMatchType.Exact"/></param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetMembersByPropertyValue(string propertyTypeAlias, string value, StringPropertyMatchType matchType = StringPropertyMatchType.Exact)
        {
            IQuery<IMember> query;

            switch (matchType)
            {
                case StringPropertyMatchType.Exact:
                    query = Query<IMember>.Builder.Where(x =>
                        ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                        (((Member)x).LongStringPropertyValue.SqlEquals(value, TextColumnType.NText) ||
                            ((Member)x).ShortStringPropertyValue.SqlEquals(value, TextColumnType.NVarchar)));
                    break;
                case StringPropertyMatchType.Contains:
                    query = Query<IMember>.Builder.Where(x =>
                        ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                        (((Member)x).LongStringPropertyValue.SqlContains(value, TextColumnType.NText) ||
                            ((Member)x).ShortStringPropertyValue.SqlContains(value, TextColumnType.NVarchar)));
                    break;
                case StringPropertyMatchType.StartsWith:
                    query = Query<IMember>.Builder.Where(x =>
                        ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                        (((Member)x).LongStringPropertyValue.SqlStartsWith(value, TextColumnType.NText) ||
                            ((Member)x).ShortStringPropertyValue.SqlStartsWith(value, TextColumnType.NVarchar)));
                    break;
                case StringPropertyMatchType.EndsWith:
                    query = Query<IMember>.Builder.Where(x =>
                        ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                        (((Member)x).LongStringPropertyValue.SqlEndsWith(value, TextColumnType.NText) ||
                            ((Member)x).ShortStringPropertyValue.SqlEndsWith(value, TextColumnType.NVarchar)));
                    break;
                default:
                    throw new ArgumentOutOfRangeException("matchType");
            }

            return WithReadLocked(repository => repository.GetByQuery(query));
        }

        /// <summary>
        /// Gets a list of Members based on a property search
        /// </summary>
        /// <param name="propertyTypeAlias">Alias of the PropertyType to search for</param>
        /// <param name="value"><see cref="System.int"/> Value to match</param>
        /// <param name="matchType">The type of match to make as <see cref="StringPropertyMatchType"/>. Default is <see cref="StringPropertyMatchType.Exact"/></param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetMembersByPropertyValue(string propertyTypeAlias, int value, ValuePropertyMatchType matchType = ValuePropertyMatchType.Exact)
        {
            IQuery<IMember> query;

            switch (matchType)
            {
                case ValuePropertyMatchType.Exact:
                    query = Query<IMember>.Builder.Where(x =>
                        ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                        ((Member)x).IntegerPropertyValue == value);
                    break;
                case ValuePropertyMatchType.GreaterThan:
                    query = Query<IMember>.Builder.Where(x =>
                        ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                        ((Member)x).IntegerPropertyValue > value);
                    break;
                case ValuePropertyMatchType.LessThan:
                    query = Query<IMember>.Builder.Where(x =>
                        ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                        ((Member)x).IntegerPropertyValue < value);
                    break;
                case ValuePropertyMatchType.GreaterThanOrEqualTo:
                    query = Query<IMember>.Builder.Where(x =>
                        ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                        ((Member)x).IntegerPropertyValue >= value);
                    break;
                case ValuePropertyMatchType.LessThanOrEqualTo:
                    query = Query<IMember>.Builder.Where(x =>
                        ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                        ((Member)x).IntegerPropertyValue <= value);
                    break;
                default:
                    throw new ArgumentOutOfRangeException("matchType");
            }

            return WithReadLocked(repository => repository.GetByQuery(query));
        }

        /// <summary>
        /// Gets a list of Members based on a property search
        /// </summary>
        /// <param name="propertyTypeAlias">Alias of the PropertyType to search for</param>
        /// <param name="value"><see cref="System.bool"/> Value to match</param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetMembersByPropertyValue(string propertyTypeAlias, bool value)
        {
            var query = Query<IMember>.Builder.Where(x =>
                ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                ((Member)x).BoolPropertyValue == value);

            return WithReadLocked(repository => repository.GetByQuery(query));
        }

        /// <summary>
        /// Gets a list of Members based on a property search
        /// </summary>
        /// <param name="propertyTypeAlias">Alias of the PropertyType to search for</param>
        /// <param name="value"><see cref="System.DateTime"/> Value to match</param>
        /// <param name="matchType">The type of match to make as <see cref="StringPropertyMatchType"/>. Default is <see cref="StringPropertyMatchType.Exact"/></param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetMembersByPropertyValue(string propertyTypeAlias, DateTime value, ValuePropertyMatchType matchType = ValuePropertyMatchType.Exact)
        {
            IQuery<IMember> query;

            switch (matchType)
            {
                case ValuePropertyMatchType.Exact:
                    query = Query<IMember>.Builder.Where(x =>
                        ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                        ((Member)x).DateTimePropertyValue == value);
                    break;
                case ValuePropertyMatchType.GreaterThan:
                    query = Query<IMember>.Builder.Where(x =>
                        ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                        ((Member)x).DateTimePropertyValue > value);
                    break;
                case ValuePropertyMatchType.LessThan:
                    query = Query<IMember>.Builder.Where(x =>
                        ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                        ((Member)x).DateTimePropertyValue < value);
                    break;
                case ValuePropertyMatchType.GreaterThanOrEqualTo:
                    query = Query<IMember>.Builder.Where(x =>
                        ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                        ((Member)x).DateTimePropertyValue >= value);
                    break;
                case ValuePropertyMatchType.LessThanOrEqualTo:
                    query = Query<IMember>.Builder.Where(x =>
                        ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                        ((Member)x).DateTimePropertyValue <= value);
                    break;
                default:
                    throw new ArgumentOutOfRangeException("matchType");
            }

            //TODO: Since this is by property value, we need a GetByPropertyQuery on the repo!
            return WithReadLocked(repository => repository.GetByQuery(query));
        }

        /// <summary>
        /// Checks if a Member with the id exists
        /// </summary>
        /// <param name="id">Id of the Member</param>
        /// <returns><c>True</c> if the Member exists otherwise <c>False</c></returns>
        public bool Exists(int id)
        {
            return WithReadLocked(repository => repository.Exists(id));
        }

        /// <summary>
        /// Checks if a Member with the username exists
        /// </summary>
        /// <param name="username">Username to check</param>
        /// <returns><c>True</c> if the Member exists otherwise <c>False</c></returns>
        public bool Exists(string username)
        {
            return WithReadLocked(repository => repository.Exists(username));
        }

        #endregion

        #region Save

        /// <summary>
        /// Saves an <see cref="IMember"/>
        /// </summary>
        /// <param name="member"><see cref="IMember"/> to Save</param>
        /// <param name="raiseEvents">Optional parameter to raise events. 
        /// Default is <c>True</c> otherwise set to <c>False</c> to not raise events</param>
        public void Save(IMember member, bool raiseEvents = true)
        {
            if (raiseEvents && Saving.IsRaisedEventCancelled(new SaveEventArgs<IMember>(member), this))
                return;

            WithWriteLocked(repository =>
            {
                //member.CreatorId = 
                repository.AddOrUpdate(member);
            });

            if (raiseEvents)
                Saved.RaiseEvent(new SaveEventArgs<IMember>(member, false), this);
            Audit(AuditType.Save, "Save Member performed by user", 0, member.Id);
        }

        /// <summary>
        /// Saves a list of <see cref="IMember"/> objects
        /// </summary>
        /// <param name="members"><see cref="IEnumerable{IMember}"/> to save</param>
        /// <param name="raiseEvents">Optional parameter to raise events. 
        /// Default is <c>True</c> otherwise set to <c>False</c> to not raise events</param>
        public void Save(IEnumerable<IMember> members, bool raiseEvents = true)
        {
            var membersA = members.ToArray();

            if (raiseEvents && Saving.IsRaisedEventCancelled(new SaveEventArgs<IMember>(membersA), this))
                    return;

            WithWriteLocked(repository =>
            {
                foreach (var member in membersA)
                {
                    //member.CreatorId = 
                    repository.AddOrUpdate(member);
                }
            });

            if (raiseEvents)
                Saved.RaiseEvent(new SaveEventArgs<IMember>(membersA, false), this);
            Audit(AuditType.Save, "Save Member items performed by user", 0, -1);
        }

        #endregion

        #region Delete

        /// <summary>
        /// Deletes an <see cref="IMember"/>
        /// </summary>
        /// <param name="member"><see cref="IMember"/> to Delete</param>
        public void Delete(IMember member)
        {
            if (Deleting.IsRaisedEventCancelled(new DeleteEventArgs<IMember>(member), this))
                return;

            WithWriteLocked(repository => DeleteLocked(member, repository));

            Audit(AuditType.Delete, "Delete Member performed by user", 0, member.Id);
        }

        private void DeleteLocked(IMember member, IMemberRepository repository)
        {
            // a member has no descendants
            repository.Delete(member);
            var args = new DeleteEventArgs<IMember>(member, false); // raise event & get flagged files
            Deleted.RaiseEvent(args, this);
            IOHelper.DeleteFiles(args.MediaFilesToDelete, // remove flagged files
                (file, e) => Logger.Error<MemberService>("An error occurred while deleting file attached to nodes: " + file, e));
        }

        #endregion

        #region Roles

        public void AddRole(string roleName)
        {
            WithWriteLockedGroups(repository => repository.CreateIfNotExists(roleName));
        }

        public IEnumerable<string> GetAllRoles()
        {
            return WithReadLockedGroups(repository => repository.GetAll().Select(x => x.Name).Distinct());
        }

        public IEnumerable<string> GetAllRoles(int memberId)
        {
            return WithReadLockedGroups(repository => repository.GetMemberGroupsForMember(memberId).Select(x => x.Name).Distinct());
        }

        public IEnumerable<string> GetAllRoles(string username)
        {
            return WithReadLockedGroups(repository => repository.GetMemberGroupsForMember(username).Select(x => x.Name).Distinct());
        }

        public IEnumerable<IMember> GetMembersInRole(string roleName)
        {
            return WithReadLocked(repository => repository.GetByMemberGroup(roleName));
        }

        public IEnumerable<IMember> FindMembersInRole(string roleName, string usernameToMatch, StringPropertyMatchType matchType = StringPropertyMatchType.StartsWith)
        {
            return WithReadLocked(repository => repository.FindMembersInRole(roleName, usernameToMatch, matchType));
        }

        public bool DeleteRole(string roleName, bool throwIfBeingUsed)
        {
            return _lgrepo.WithWriteLocked(xr =>
            {
                if (throwIfBeingUsed)
                {
                    var mrepo = RepositoryFactory.CreateMemberRepository(xr.UnitOfWork); // safe, all is write-locked already
                    var inRole = mrepo.GetByMemberGroup(roleName);
                    if (inRole.Any())
                        throw new InvalidOperationException("The role " + roleName + " is currently assigned to members");
                }

                var query = new Query<IMemberGroup>().Where(g => g.Name == roleName);
                var groups = xr.Repository.GetByQuery(query).ToArray();

                foreach (var group in groups)
                    _memberGroupService.Delete(group);

                return groups.Length > 0;
            });
        }

        public void AssignRole(string username, string roleName)
        {
            AssignRoles(new[] { username }, new[] { roleName });
        }

        public void AssignRoles(string[] usernames, string[] roleNames)
        {
            WithWriteLockedGroups(repository => repository.AssignRoles(usernames, roleNames));
        }

        public void DissociateRole(string username, string roleName)
        {
            DissociateRoles(new[] { username }, new[] { roleName });
        }

        public void DissociateRoles(string[] usernames, string[] roleNames)
        {
            WithWriteLockedGroups(repository => repository.DissociateRoles(usernames, roleNames));
        }
        
        public void AssignRole(int memberId, string roleName)
        {
            AssignRoles(new[] { memberId }, new[] { roleName });
        }

        public void AssignRoles(int[] memberIds, string[] roleNames)
        {
            WithWriteLockedGroups(repository => repository.AssignRoles(memberIds, roleNames));
        }

        public void DissociateRole(int memberId, string roleName)
        {
            DissociateRoles(new[] { memberId }, new[] { roleName });
        }

        public void DissociateRoles(int[] memberIds, string[] roleNames)
        {
            WithWriteLockedGroups(repository => repository.DissociateRoles(memberIds, roleNames));
        }       

        #endregion

        #region Private methods

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
        public static event TypedEventHandler<IMemberService, DeleteEventArgs<IMember>> Deleting;

        /// <summary>
        /// Occurs after Delete
        /// </summary>
        public static event TypedEventHandler<IMemberService, DeleteEventArgs<IMember>> Deleted;

        /// <summary>
        /// Occurs before Save
        /// </summary>
        public static event TypedEventHandler<IMemberService, SaveEventArgs<IMember>> Saving;

        /// <summary>
        /// Occurs after Create
        /// </summary>
        /// <remarks>
        /// Please note that the Member object has been created, but might not have been saved
        /// so it does not have an identity yet (meaning no Id has been set).
        /// </remarks>
        public static event TypedEventHandler<IMemberService, NewEventArgs<IMember>> Created;

        /// <summary>
        /// Occurs after Save
        /// </summary>
        public static event TypedEventHandler<IMemberService, SaveEventArgs<IMember>> Saved;

        #endregion

        #region Membership

        /// <summary>
        /// This is simply a helper method which essentially just wraps the MembershipProvider's ChangePassword method
        /// </summary>
        /// <remarks>This method exists so that Umbraco developers can use one entry point to create/update 
        /// Members if they choose to. </remarks>
        /// <param name="member">The Member to save the password for</param>
        /// <param name="password">The password to encrypt and save</param>
        public void SavePassword(IMember member, string password)
        {
            if (member == null) throw new ArgumentNullException("member");

            var provider = MembershipProviderExtensions.GetMembersMembershipProvider();
            if (provider.IsUmbracoMembershipProvider())
                provider.ChangePassword(member.Username, "", password);
            else
                throw new NotSupportedException("When using a non-Umbraco membership provider you must change the member password by using the MembershipProvider.ChangePassword method");

            // go re-fetch the member and update the properties that may have changed
            var result = GetByUsername(member.Username);

            // should never be null but it could have been deleted by another thread.
            if (result == null)
                return;

            member.RawPasswordValue = result.RawPasswordValue;
            member.LastPasswordChangeDate = result.LastPasswordChangeDate;
            member.UpdateDate = result.UpdateDate;

            // not saving?
        }

        /// <summary>
        /// A helper method that will create a basic/generic member for use with a generic membership provider
        /// </summary>
        /// <returns></returns>
        internal static IMember CreateGenericMembershipProviderMember(string name, string email, string username, string password)
        {
            var identity = int.MaxValue;

            var memType = new MemberType(-1);
            var propGroup = new PropertyGroup
            {
                Name = "Membership",
                Id = --identity
            };
            propGroup.PropertyTypes.Add(new PropertyType(Constants.PropertyEditors.TextboxAlias, DataTypeDatabaseType.Ntext, Constants.Conventions.Member.Comments)
            {
                Name = Constants.Conventions.Member.CommentsLabel,
                SortOrder = 0,
                Id = --identity,
                Key = identity.ToGuid()
            });
            propGroup.PropertyTypes.Add(new PropertyType(Constants.PropertyEditors.TrueFalseAlias, DataTypeDatabaseType.Integer, Constants.Conventions.Member.IsApproved)
            {
                Name = Constants.Conventions.Member.IsApprovedLabel,
                SortOrder = 3,
                Id = --identity,
                Key = identity.ToGuid()
            });
            propGroup.PropertyTypes.Add(new PropertyType(Constants.PropertyEditors.TrueFalseAlias, DataTypeDatabaseType.Integer, Constants.Conventions.Member.IsLockedOut)
            {
                Name = Constants.Conventions.Member.IsLockedOutLabel,
                SortOrder = 4,
                Id = --identity,
                Key = identity.ToGuid()
            });
            propGroup.PropertyTypes.Add(new PropertyType(Constants.PropertyEditors.NoEditAlias, DataTypeDatabaseType.Date, Constants.Conventions.Member.LastLockoutDate)
            {
                Name = Constants.Conventions.Member.LastLockoutDateLabel,
                SortOrder = 5,
                Id = --identity,
                Key = identity.ToGuid()
            });
            propGroup.PropertyTypes.Add(new PropertyType(Constants.PropertyEditors.NoEditAlias, DataTypeDatabaseType.Date, Constants.Conventions.Member.LastLoginDate)
            {
                Name = Constants.Conventions.Member.LastLoginDateLabel,
                SortOrder = 6,
                Id = --identity,
                Key = identity.ToGuid()
            });
            propGroup.PropertyTypes.Add(new PropertyType(Constants.PropertyEditors.NoEditAlias, DataTypeDatabaseType.Date, Constants.Conventions.Member.LastPasswordChangeDate)
            {
                Name = Constants.Conventions.Member.LastPasswordChangeDateLabel,
                SortOrder = 7,
                Id = --identity,
                Key = identity.ToGuid()
            });

            memType.PropertyGroups.Add(propGroup);

            // should we "create member"?
            var member = new Member(name, email, username, password, memType);

            //we've assigned ids to the property types and groups but we also need to assign fake ids to the properties themselves.
            foreach (var property in member.Properties)
            {
                property.Id = --identity;
            }

            return member;
        }

        #endregion

        #region Content Types

        /// <summary>
        /// Delete Members of the specified MemberType id
        /// </summary>
        /// <param name="memberTypeId">Id of the MemberType</param>
        public void DeleteMembersOfType(int memberTypeId)
        {
            // no tree to manage here

            var query = Query<IMember>.Builder.Where(x => x.ContentTypeId == memberTypeId);

            WithWriteLocked(repository =>
            {
                var members = repository.GetByQuery(query).ToArray();
                if (Deleting.IsRaisedEventCancelled(new DeleteEventArgs<IMember>(members), this))
                    return;
                foreach (var member in members)
                    DeleteLocked(member, repository);
            });

            Audit(AuditType.Delete,
                string.Format("Delete Members of Type {0} performed by user", memberTypeId),
                0, -1);
        }

        private IMemberType GetMemberType(string memberTypeAlias)
        {
            var memberType = MemberTypeService.Get(memberTypeAlias);
            if (memberType == null)
                throw new Exception(string.Format("No MemberType matching alias: \"{0}\".", memberTypeAlias));
            return memberType;
        }

        public string GetDefaultMemberType()
        {
            return MemberTypeService.GetDefault();
        }

        #endregion
    }
}