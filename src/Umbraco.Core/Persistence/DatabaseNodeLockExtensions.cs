using System;
using System.Collections.Generic;
using System.Data;
using System.Runtime.CompilerServices;

namespace Umbraco.Core.Persistence
{
    internal static class DatabaseNodeLockExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ValidateDatabase(UmbracoDatabase database)
        {
            if (database == null)
                throw new ArgumentNullException("database");
            if (database.CurrentTransactionIsolationLevel < IsolationLevel.RepeatableRead)
                throw new InvalidOperationException("A transaction with minimum RepeatableRead isolation level is required.");
        }

        // updating a record within a repeatable-read transaction gets an exclusive lock on
        // that record which will be kept until the transaction is ended, effectively locking
        // out all other accesses to that record - thus obtaining an exclusive lock over the
        // protected resources.
        public static void AcquireLockNodeWriteLock(this UmbracoDatabase database, int nodeId)
        {
            ValidateDatabase(database);

            var x = database.Execute("UPDATE umbracoLock SET value = (CASE WHEN (value=1) THEN -1 ELSE 1 END) WHERE id=@id",
                new { @id = nodeId });
            if (x != 1) throw new Exception("nothing to lock?");
        }

        // reading a record within a repeatable-read transaction gets a shared lock on
        // that record which will be kept until the transaction is ended, effectively preventing
        // other write accesses to that record - thus obtaining a shared lock over the protected
        // resources.
        public static void AcquireLockNodeReadLock(this UmbracoDatabase database, int nodeId)
        {
            ValidateDatabase(database);

            var r = database.ExecuteScalar<int?>("SELECT value FROM umbracoLock WHERE id=@id",
                new { @id = nodeId });
            if (r == null) throw new Exception("nothing to lock?");
        }
    }
}