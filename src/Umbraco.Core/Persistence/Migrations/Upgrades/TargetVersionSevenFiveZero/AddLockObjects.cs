using Umbraco.Core.Configuration;
using Umbraco.Core.Logging;
using Umbraco.Core.Models.Rdbms;
using Umbraco.Core.Persistence.SqlSyntax;

namespace Umbraco.Core.Persistence.Migrations.Upgrades.TargetVersionSevenFiveZero
{
    [Migration("7.5.0", 11, GlobalSettings.UmbracoMigrationName)]
    public class AddLockObjects : MigrationBase
    {
        public AddLockObjects(ISqlSyntaxProvider sqlSyntax, ILogger logger)
            : base(sqlSyntax, logger)
        { }

        public override void Up()
        {
            // some may already exist, just ensure everything we need is here
            EnsureLockObject(Constants.Locks.Servers, "Servers");
            EnsureLockObject(Constants.Locks.ContentTypes, "ContentTypes");
            EnsureLockObject(Constants.Locks.ContentTree, "ContentTree");
            EnsureLockObject(Constants.Locks.MediaTree, "MediaTree");
            EnsureLockObject(Constants.Locks.MemberTree, "MemberTree");
            EnsureLockObject(Constants.Locks.MediaTypes, "MediaTypes");
            EnsureLockObject(Constants.Locks.MemberTypes, "MemberTypes");
            EnsureLockObject(Constants.Locks.Domains, "Domains");
        }

        public override void Down()
        {
            // not implemented
        }

        private void EnsureLockObject(int id, string name)
        {
            Execute.Code(db =>
            {
                var exists = db.Exists<LockDto>(id);
                if (exists) return string.Empty;
                // be safe: delete old umbracoNode lock objects if any
                db.Execute(string.Format("DELETE FROM umbracoNode WHERE id={0};", id));
                // then create umbracoLock object
                db.Execute(string.Format("INSERT umbracoLock (id, name, value) VALUES ({0}, '{1}', 1);", id, name));
                return string.Empty;
            });
        }
    }
}
