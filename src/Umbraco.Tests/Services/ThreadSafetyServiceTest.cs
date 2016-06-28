using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Umbraco.Core;
using Umbraco.Core.Cache;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Persistence;
using Umbraco.Core.Persistence.SqlSyntax;
using Umbraco.Core.Persistence.UnitOfWork;
using Umbraco.Core.Publishing;
using Umbraco.Core.Services;
using Umbraco.Tests.TestHelpers;
using Umbraco.Tests.TestHelpers.Entities;
using umbraco.editorControls.tinyMCE3;
using umbraco.interfaces;
using Umbraco.Core.Events;
using Umbraco.Core.Persistence.Repositories;

namespace Umbraco.Tests.Services
{
    [DatabaseTestBehavior(DatabaseBehavior.NewDbFileAndSchemaPerTest)]
	[TestFixture, RequiresSTA]
	public class ThreadSafetyServiceTest : BaseDatabaseFactoryTest
	{
		private PerThreadPetaPocoUnitOfWorkProvider _uowProvider;
		private PerThreadDatabaseFactory _dbFactory;

		[SetUp]
		public override void Initialize()
		{
			base.Initialize();

			//we need to use our own custom IDatabaseFactory for the DatabaseContext because we MUST ensure that
			//a Database instance is created per thread, whereas the default implementation which will work in an HttpContext
			//threading environment, or a single apartment threading environment will not work for this test because
			//it is multi-threaded.
			_dbFactory = new PerThreadDatabaseFactory(Logger);
			//overwrite the local object
            ApplicationContext.DatabaseContext = new DatabaseContext(_dbFactory, Logger, new SqlCeSyntaxProvider(), "System.Data.SqlServerCe.4.0");

            //disable cache
		    var cacheHelper = CacheHelper.CreateDisabledCacheHelper();

			//here we are going to override the ServiceContext because normally with our test cases we use a
			//global Database object but this is NOT how it should work in the web world or in any multi threaded scenario.
			//we need a new Database object for each thread.
            var repositoryFactory = new RepositoryFactory(cacheHelper, Logger, SqlSyntax, SettingsForTests.GenerateMockSettings());
			_uowProvider = new PerThreadPetaPocoUnitOfWorkProvider(_dbFactory);
		    var evtMsgs = new TransientMessagesFactory();
		    ApplicationContext.Services = new ServiceContext(
                repositoryFactory,
                _uowProvider,
                new FileUnitOfWorkProvider(),
                cacheHelper,
                Logger,
                evtMsgs);

			CreateTestData();
		}

		[TearDown]
		public override void TearDown()
		{
			_error = null;

			//dispose!
			_dbFactory.Dispose();
			_uowProvider.Dispose();

			base.TearDown();
		}

        [Test]
        public void SqlCeTest1()
        {
            var db1 = new UmbracoDatabase(Umbraco.Core.Configuration.GlobalSettings.UmbracoConnectionName, Logger);
            var db2 = new UmbracoDatabase(Umbraco.Core.Configuration.GlobalSettings.UmbracoConnectionName, Logger);
            var t1 = db1.GetTransaction(IsolationLevel.RepeatableRead);
            var t2 = db2.GetTransaction(IsolationLevel.RepeatableRead);

            // if it's the same then deadlock else works
            db1.Execute("UPDATE umbracoNode SET sortOrder = (CASE WHEN (sortOrder=1) THEN -1 ELSE 1 END) WHERE id=-333");
            db2.Execute("UPDATE umbracoNode SET sortOrder = (CASE WHEN (sortOrder=1) THEN -1 ELSE 1 END) WHERE id=-334");

            // inserting another node works
            db2.Execute("INSERT INTO umbracoNode (Text, ParentId, Level, sortOrder, Path) VALUES ('test', -1, 1, 0, '-1,')");

            t1.Complete();
            t2.Complete();
        }

        [Test]
        public void SqlCeTest1a()
        {
            var db1 = new UmbracoDatabase(Umbraco.Core.Configuration.GlobalSettings.UmbracoConnectionName, Logger);
            var db2 = new UmbracoDatabase(Umbraco.Core.Configuration.GlobalSettings.UmbracoConnectionName, Logger);
            var t1 = db1.GetTransaction(IsolationLevel.RepeatableRead);
            var t2 = db2.GetTransaction(IsolationLevel.RepeatableRead);

            // if it's the same then deadlock else works
            Console.WriteLine("Lock 1");
            db1.Execute("UPDATE umbracoNode SET sortOrder = (CASE WHEN (sortOrder=1) THEN -1 ELSE 1 END) WHERE id=-333");
            WriteLockInfo();

            /*
            Lock 1
            > 2 DB  ix   GRANT
            > 2 TAB  ix umbracoNode 1029 GRANT
            > 2 PAG (data) 1032 ix umbracoNode 1029 GRANT
            > 2 RID 1032:33 x umbracoNode 1029 GRANT
            */

            Console.WriteLine("Lock 2");
            var t = new Thread(() =>
            {
                Thread.Sleep(500);
                WriteLockInfo();
                Thread.Sleep(500);
                t1.Complete();
            });
            t.Start();
            db2.Execute("UPDATE umbracoNode SET sortOrder = (CASE WHEN (sortOrder=1) THEN -1 ELSE 1 END) WHERE id=-333");

            /*
            Lock 2
            > 2 DB  ix   GRANT
            > 2 TAB  ix umbracoNode 1029 GRANT
            > 2 PAG (data) 1032 ix umbracoNode 1029 GRANT
            > 2 RID 1032:33 x umbracoNode 1029 GRANT ---------------- exclusive lock on row
            > 4 DB  iu   GRANT
            > 4 TAB  iu umbracoNode 1029 GRANT
            > 4 PAG (data) 1032 iu umbracoNode 1029 GRANT
            > 4 PAG (idx) 1031 s umbracoNode 1029 GRANT
            > 4 RID 1032:33 u umbracoNode 1029 WAIT ----------------- waiting to update the row
            > 4 MD  Sch-s umbracoNode 1029 GRANT
            */

            Console.WriteLine("Lock 3");
            WriteLockInfo();

            /*
            Lock 3
            > 4 DB  ix   GRANT
            > 4 TAB  ix umbracoNode 1029 GRANT
            > 4 PAG (data) 1032 ix umbracoNode 1029 GRANT
            > 4 RID 1032:33 x umbracoNode 1029 GRANT ---------------- exclusive lock on row
            */
        }

        [Test]
        public void SqlCeTest2()
        {
            var db1 = new UmbracoDatabase(Umbraco.Core.Configuration.GlobalSettings.UmbracoConnectionName, Logger);
            var db2 = new UmbracoDatabase(Umbraco.Core.Configuration.GlobalSettings.UmbracoConnectionName, Logger);
            var t1 = db1.GetTransaction(IsolationLevel.RepeatableRead);
            var t2 = db2.GetTransaction(IsolationLevel.RepeatableRead);

            // if it's the same then deadlock else works
            db1.Execute("UPDATE umbracoNode SET sortOrder = (CASE WHEN (sortOrder=1) THEN -1 ELSE 1 END) WHERE id=-333");
            db2.Execute("UPDATE umbracoNode SET sortOrder = (CASE WHEN (sortOrder=1) THEN -1 ELSE 1 END) WHERE id=-334");

            // inserting another node works
            db2.Execute("INSERT INTO umbracoNode (Text, ParentId, Level, sortOrder, Path) VALUES ('test', -1, 1, 0, '-1,')");

            t1.Complete();

            // has been released so now it works
            db2.Execute("UPDATE umbracoNode SET sortOrder = (CASE WHEN (sortOrder=1) THEN -1 ELSE 1 END) WHERE id=-333");
            t2.Complete();
        }

        [Test]
        public void SqlCeTest3()
        {
            const int threadCount = 20; // with 20 ... they can all fail?!
            const int timeout = 32000;

            var threads = new List<Thread>();
            var exceptions = new List<Exception>();
            var tt = new Thread(() =>
            {
                for (var i = 0; i < 10; i++)
                {
                    Console.WriteLine("Locks:");
                    WriteLockInfo();
                    Thread.Sleep(500);
                }
            });
            tt.Start();
            for (var i = 0; i < threadCount; i++)
                threads.Add(new Thread(() =>
                {
                    try
                    {
                        Console.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] ({DateTime.Now.ToString("HH:mm:ss,FFF")}) Begin.");
                        var db = new UmbracoDatabase(Umbraco.Core.Configuration.GlobalSettings.UmbracoConnectionName, Logger);
                        db.Execute($"SET LOCK_TIMEOUT {timeout};"); // NOT changing anything?!
                        var t = db.GetTransaction(IsolationLevel.RepeatableRead);
                        //db.Execute("UPDATE umbracoLock SET value = (CASE WHEN (value=1) THEN -1 ELSE 1 END) WHERE id=-333");
                        db.Execute("UPDATE umbracoNode SET sortOrder = (CASE WHEN (sortOrder=1) THEN -1 ELSE 1 END) WHERE id=-333");
                        db.Execute("INSERT INTO umbracoNode (Text, ParentId, Level, sortOrder, Path) VALUES ('test', -1, 1, 0, '-1,')");
                        t.Complete();
                        Console.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] ({DateTime.Now.ToString("HH:mm:ss,FFF")}) Done.");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] ({DateTime.Now.ToString("HH:mm:ss,FFF")}) Exception!");
                        exceptions.Add(e);
                    }
                }));
            threads.ForEach(x => x.Start());
            threads.ForEach(x => x.Join());
            //Assert.IsEmpty(exceptions);
            Assert.AreEqual(0, exceptions.Count);
        }

        [Test]
        public void SqlCeTest4()
        {
            var contentService = (ContentService)ServiceContext.ContentService;
            var threads = new List<Thread>();
            var exceptions = new List<Exception>();
            for (var i = 0; i < 2; i++)
                threads.Add(new Thread(() =>
            {
                try
                {
                    //ApplicationContext.Current.DatabaseContext.Database.Execute("SET LOCK_TIMEOUT 10000");
                    Console.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] Begin.");
                    var content = contentService.CreateContent("test" + Guid.NewGuid(), -1, "umbTextpage", 0);
                    Console.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] Save.");
                    contentService.Save(content);
                    Console.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] End.");
                    //ApplicationContext.Current.DatabaseContext.Database.CloseSharedConnection();
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] Exception!");
                    exceptions.Add(e);
                    WriteLockInfo();
                }
            }));
            threads.ForEach(x => x.Start());
            threads.ForEach(x => x.Join());
            Thread.Sleep(4000);
            Assert.IsEmpty(exceptions);
        }

        private void WriteLockInfo()
        {
            var info = ApplicationContext.Current.DatabaseContext.Database.Query<dynamic>("SELECT * FROM sys.lock_information;");
            foreach (var row in info)
            {
                Console.WriteLine($"> {row.request_spid} {row.resource_type} {row.resource_description} {row.request_mode} {row.resource_table} {row.resource_table_id} {row.request_status}");
            }
        }

        [Test]
        public void SqlCeTest5()
        {
            var contentService = (ContentService)ServiceContext.ContentService;
            var threads = new List<Thread>();
            var exceptions = new List<Exception>();
            for (var i = 0; i < 2; i++)
                threads.Add(new Thread(() =>
                {
                    try
                    {
                        var content = contentService.CreateContent("test" + Guid.NewGuid(), -1, "umbTextpage", 0);
                        contentService.Save(content);
                        Thread.Sleep(1000);
                        content = contentService.CreateContent("test" + Guid.NewGuid(), -1, "umbTextpage", 0);
                        contentService.Save(content);
                    }
                    catch (Exception e)
                    {
                        //Console.WriteLine("Exception!");
                        exceptions.Add(e);
                    }
                }));
            threads.ForEach(x => x.Start());
            threads.ForEach(x => x.Join());
            Assert.IsEmpty(exceptions);
        }

        /// <summary>
        /// Used to track exceptions during multi-threaded tests, volatile so that it is not locked in CPU registers.
        /// </summary>
        private volatile Exception _error = null;

		private const int MaxThreadCount = 20;

		[Test]
		public void Ensure_All_Threads_Execute_Successfully_Content_Service()
		{
			//we will mimick the ServiceContext in that each repository in a service (i.e. ContentService) is a singleton
			var contentService = (ContentService)ServiceContext.ContentService;

			var threads = new List<Thread>();

			Debug.WriteLine("Starting test...");

			for (var i = 0; i < MaxThreadCount; i++)
			{
				var t = new Thread(() =>
					{
						try
						{
                            Debug.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] ({DateTime.Now.ToString("HH:mm:ss,FFF")}) Create 1st content.");
                            var content1 = contentService.CreateContent("test" + Guid.NewGuid(), -1, "umbTextpage", 0);

                            Debug.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] ({DateTime.Now.ToString("HH:mm:ss,FFF")}) Save 1st content.");
                            contentService.Save(content1);
                            Debug.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] ({DateTime.Now.ToString("HH:mm:ss,FFF")}) Saved 1st content.");

                            Thread.Sleep(100); //quick pause for maximum overlap!

                            Debug.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] ({DateTime.Now.ToString("HH:mm:ss,FFF")}) Create 2nd content.");
                            var content2 = contentService.CreateContent("test" + Guid.NewGuid(), -1, "umbTextpage", 0);

                            Debug.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] ({DateTime.Now.ToString("HH:mm:ss,FFF")}) Save 2nd content.");
                            contentService.Save(content2);
                            Debug.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] ({DateTime.Now.ToString("HH:mm:ss,FFF")}) Saved 2nd content.");
                        }
                        catch (Exception e)
						{
							_error = e;
						}
					});
				threads.Add(t);
			}

			//start all threads
			threads.ForEach(x => x.Start());

			//wait for all to complete
			threads.ForEach(x => x.Join());

			//kill them all
			threads.ForEach(x => x.Abort());

			if (_error == null)
			{
				//now look up all items, there should be 40!
				var items = contentService.GetRootContent();
				Assert.AreEqual(40, items.Count());
			}
			else
			{
			    throw new Exception("Error!", _error);
			}

		}

		[Test]
		public void Ensure_All_Threads_Execute_Successfully_Media_Service()
		{
			//we will mimick the ServiceContext in that each repository in a service (i.e. ContentService) is a singleton
			var mediaService = (MediaService)ServiceContext.MediaService;

			var threads = new List<Thread>();

			Debug.WriteLine("Starting test...");

			for (var i = 0; i < MaxThreadCount; i++)
			{
				var t = new Thread(() =>
				{
					try
					{
						Debug.WriteLine("Created content on thread: " + Thread.CurrentThread.ManagedThreadId);

						//create 2 content items

                        string name1 = "test" + Guid.NewGuid();
					    var folder1 = mediaService.CreateMedia(name1, -1, Constants.Conventions.MediaTypes.Folder, 0);
						Debug.WriteLine("Saving folder1 on thread: " + Thread.CurrentThread.ManagedThreadId);
						mediaService.Save(folder1, 0);

						Thread.Sleep(100); //quick pause for maximum overlap!

                        string name = "test" + Guid.NewGuid();
                        var folder2 = mediaService.CreateMedia(name, -1, Constants.Conventions.MediaTypes.Folder, 0);
						Debug.WriteLine("Saving folder2 on thread: " + Thread.CurrentThread.ManagedThreadId);
						mediaService.Save(folder2, 0);
					}
					catch (Exception e)
					{
						_error = e;
					}
				});
				threads.Add(t);
			}

			//start all threads
			threads.ForEach(x => x.Start());

			//wait for all to complete
			threads.ForEach(x => x.Join());

			//kill them all
			threads.ForEach(x => x.Abort());

			if (_error == null)
			{
				//now look up all items, there should be 40!
				var items = mediaService.GetRootMedia();
				Assert.AreEqual(40, items.Count());
			}
			else
			{
				Assert.Fail("ERROR! " + _error);
			}

		}

		public void CreateTestData()
		{
			//Create and Save ContentType "umbTextpage" -> 1045
			ContentType contentType = MockedContentTypes.CreateSimpleContentType("umbTextpage", "Textpage");
			contentType.Key = new Guid("1D3A8E6E-2EA9-4CC1-B229-1AEE19821522");
			ServiceContext.ContentTypeService.Save(contentType);
		}

		/// <summary>
		/// Creates a Database object per thread, this mimics the web context which is per HttpContext and is required for the multi-threaded test
		/// </summary>
		internal class PerThreadDatabaseFactory : DisposableObject, IDatabaseFactory
		{
		    private readonly ILogger _logger;

		    public PerThreadDatabaseFactory(ILogger logger)
		    {
		        _logger = logger;
		    }

		    private readonly ConcurrentDictionary<int, UmbracoDatabase> _databases = new ConcurrentDictionary<int, UmbracoDatabase>();

			public UmbracoDatabase CreateDatabase()
			{
				var db = _databases.GetOrAdd(
                    Thread.CurrentThread.ManagedThreadId,
                    i => new UmbracoDatabase(Umbraco.Core.Configuration.GlobalSettings.UmbracoConnectionName, _logger));
				return db;
			}

			protected override void DisposeResources()
			{
				//dispose the databases
				_databases.ForEach(x => x.Value.Dispose());
			}
		}

		/// <summary>
		/// Creates a UOW with a Database object per thread
		/// </summary>
		internal class PerThreadPetaPocoUnitOfWorkProvider : DisposableObject, IDatabaseUnitOfWorkProvider
		{
			private readonly PerThreadDatabaseFactory _dbFactory;

			public PerThreadPetaPocoUnitOfWorkProvider(PerThreadDatabaseFactory dbFactory)
			{
				_dbFactory = dbFactory;
			}

			public IDatabaseUnitOfWork GetUnitOfWork()
			{
				//Create or get a database instance for this thread.
				var db = _dbFactory.CreateDatabase();
				return new PetaPocoUnitOfWork(db);
			}

			protected override void DisposeResources()
			{
				//dispose the databases
				_dbFactory.Dispose();
			}
		}

	}
}
 