using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Umbraco.Core;
using Umbraco.Core.Logging;
using Umbraco.Core.Models.PublishedContent;
using Umbraco.Core.Profiling;
using Umbraco.Core.PropertyEditors;
using Umbraco.Tests.TestHelpers;
using Umbraco.Web.PublishedCache.NuCache;
using Umbraco.Web.PublishedCache.NuCache.DataSource;

namespace Umbraco.Tests.Cache.PublishedCache
{
    [TestFixture]
    public class NuCacheTests : BaseUmbracoApplicationTest
    {
        private ILogger _logger;

        public override void Initialize()
        {
            PropertyValueConvertersResolver.Current = new PropertyValueConvertersResolver(new ActivatorServiceProvider(), Logger);
            base.Initialize();
            var logger = new Logger(new FileInfo(TestHelper.MapPathForTest("~/unit-test-log4net.config")));
            _logger = new ProfilingLogger(logger, new LogProfiler(logger)).Logger;
        }

        [TearDown]
        public override void TearDown()
        {
            PropertyValueConvertersResolver.Reset();
        }

        [Test]
        public void UpdateDataTypes()
        {
            var store = new ContentStore2(_logger);

            var props = new[]
                    {
                        new PublishedPropertyType("prop1", 1, "?"),
                    };
            var contentType1 = new PublishedContentType(1, "ContentType1", props);
            store.UpdateContentTypes(null, new[] { contentType1 }, null);

            var kit1 = CreateContentNodeKit(1, 1, null, 1234);
            store.Set(kit1);
            var snap1 = store.CreateSnapshot();
            Assert.AreSame(kit1.Node, snap1.Get(kit1.Node.Id));

            store.UpdateDataTypes(new[] { 1 }, x => new PublishedContentType(x, "ContentType1", props));
            var snap2 = store.CreateSnapshot();
            Assert.AreNotSame(kit1.Node, snap2.Get(kit1.Node.Id)); // snap2 contains a new content node

            // but both nodes have the *same* list of children, which is OK because should
            // the list be modified (adding or removing children) then the node would be
            // cloned anyways via CloneParent which *does* create a new list.
            Assert.AreSame(kit1.Node.ChildContentIds, snap2.Get(kit1.Node.Id).ChildContentIds);
        }

        [Test]
        public void NewContentNotVisible()
        {
            var store = new ContentStore2(_logger);

            var props = new[]
                    {
                        new PublishedPropertyType("prop1", 1, "?"),
                    };
            var contentType1 = new PublishedContentType(1, "ContentType1", props);
            store.UpdateContentTypes(null, new[] { contentType1 }, null);

            var kit1 = CreateContentNodeKit(1, 1, null, 1234);
            store.Set(kit1);
            var snap1 = store.CreateSnapshot();
            Assert.AreSame(kit1.Node, snap1.Get(kit1.Node.Id));

            var kit2 = CreateContentNodeKit(1, 2, null, 3456);
            store.Set(kit2);
            var snap2 = store.CreateSnapshot();
            Assert.AreSame(kit1.Node, snap1.Get(kit1.Node.Id));
            Assert.AreSame(kit1.Node, snap2.Get(kit1.Node.Id));
            Assert.AreSame(kit2.Node, snap2.Get(kit2.Node.Id));
            Assert.IsNull(snap1.Get(kit2.Node.Id));
        }

        [Test]
        public void EditedContentNotVisible()
        {
            var store = new ContentStore2(_logger);

            var props = new[]
                    {
                        new PublishedPropertyType("prop1", 1, "?"),
                    };
            var contentType1 = new PublishedContentType(1, "ContentType1", props);
            store.UpdateContentTypes(null, new[] { contentType1 }, null);

            var kit1 = CreateContentNodeKit(1, 1, null, 1234);
            store.Set(kit1);
            var snap1 = store.CreateSnapshot();
            Assert.AreSame(kit1.Node, snap1.Get(kit1.Node.Id));

            var kit2 = CreateContentNodeKit(1, 1, null, 5678);
            store.Set(kit2);
            var snap2 = store.CreateSnapshot();
            Assert.AreNotSame(snap1, snap2);
            Assert.AreSame(kit1.Node, snap1.Get(kit1.Node.Id));
            Assert.AreNotSame(kit1.Node, snap2.Get(kit1.Node.Id));
            Assert.AreSame(kit2.Node, snap2.Get(kit1.Node.Id));
            Assert.AreEqual(1234, snap1.Get(kit1.Node.Id).Published.GetProperty("prop1").Value);
            Assert.AreEqual(5678, snap2.Get(kit1.Node.Id).Published.GetProperty("prop1").Value);
        }

        [Test]
        public void RootContent()
        {
            var store = new ContentStore2(_logger);

            var props = new[]
                    {
                        new PublishedPropertyType("prop1", 1, "?"),
                    };
            var contentType1 = new PublishedContentType(1, "ContentType1", props);
            var contentType2 = new PublishedContentType(2, "ContentType2", props);
            store.UpdateContentTypes(null, new[] { contentType1, contentType2 }, null);

            var kit1 = CreateContentNodeKit(1, 1, null, 1234);
            store.Set(kit1);

            var snap1 = store.CreateSnapshot();
            Assert.AreEqual(1, snap1.GetAtRoot().Count());

            var node2 = CreateContentNodeKit(2, 2, null, 3456);
            store.Set(node2);

            var snap2 = store.CreateSnapshot();
            Assert.AreEqual(1, snap1.GetAtRoot().Count());
            Assert.AreEqual(2, snap2.GetAtRoot().Count());

            store.Clear(kit1.Node.Id);

            var snap3 = store.CreateSnapshot();
            Assert.AreEqual(1, snap1.GetAtRoot().Count());
            Assert.AreEqual(2, snap2.GetAtRoot().Count());
            Assert.AreEqual(1, snap3.GetAtRoot().Count());
        }

        private void SetGetContentByIdOverride(ContentStore2.Snapshot snapshot)
        {
            Umbraco.Web.PublishedCache.NuCache.PublishedContent.GetContentByIdFunc = ((cache, preview, id) =>
            {
                var n = snapshot.Get(id);
                if (preview == false) return n.Published;
                throw new NotSupportedException();
                //if (n.Draft != null) return n.Draft;
                //return n.Published.AsPublishedWhaterver
            });
        }

        [Test]
        public void Children()
        {
            var store = new ContentStore2(_logger);

            var props = new[]
                    {
                        new PublishedPropertyType("prop1", 1, "?"),
                    };
            var contentType1 = new PublishedContentType(1, "ContentType1", props);
            var contentType2 = new PublishedContentType(2, "ContentType2", props);
            store.UpdateContentTypes(null, new[] { contentType1, contentType2 }, null);

            var kit1 = CreateContentNodeKit(1, 1, null, 1234);
            store.Set(kit1);

            // content1 goes at root and has no children
            var snap1 = store.CreateSnapshot();
            Assert.AreEqual(1, snap1.GetAtRoot().Count());
            SetGetContentByIdOverride(snap1);
            Assert.AreEqual(0, snap1.Get(kit1.Node.Id).Published.Children.Count());

            var kit2 = CreateContentNodeKit(2, 2, kit1.Node, 3456);
            store.Set(kit2);
            store.Set(kit2);
            store.Set(kit2);
            store.Set(kit2); // no duplicate

            // still only 1 content at root
            var snap2 = store.CreateSnapshot();
            Assert.AreEqual(1, snap1.GetAtRoot().Count());
            Assert.AreEqual(1, snap2.GetAtRoot().Count());
            // content1 from snap1 still has no children
            SetGetContentByIdOverride(snap1);
            Assert.AreEqual(0, snap1.Get(kit1.Node.Id).Published.Children.Count());
            // content1 from snap2 now has one child
            SetGetContentByIdOverride(snap2);
            Assert.AreEqual(1, snap2.Get(kit1.Node.Id).Published.Children.Count());

            store.Clear(kit2.Node.Id);
            var snap3 = store.CreateSnapshot();
            // content1 from snap3 now has no child
            SetGetContentByIdOverride(snap3);
            Assert.AreEqual(0, snap3.Get(kit1.Node.Id).Published.Children.Count());
        }

        [Test]
        public void ClearBranch()
        {
            var store = new ContentStore2(_logger);

            var props = new[]
            {
                new PublishedPropertyType("prop1", 1, "?"),
            };
            var contentType1 = new PublishedContentType(1, "ContentType1", props);
            store.UpdateContentTypes(null, new[] { contentType1 }, null);

            var kit1 = CreateContentNodeKit(1, 1, null, 1234);
            store.Set(kit1);

            var kit2 = CreateContentNodeKit(1, 2, kit1.Node, 1234);
            store.Set(kit2);

            var kit3 = CreateContentNodeKit(1, 3, kit2.Node, 1234);
            store.Set(kit3);

            // only 1 content at root
            var snap1 = store.CreateSnapshot();
            Assert.AreEqual(1, snap1.GetAtRoot().Count());
            // children
            SetGetContentByIdOverride(snap1);
            Assert.AreEqual(1, snap1.Get(kit1.Node.Id).Published.Children.Count());
            Assert.AreEqual(1, snap1.Get(kit2.Node.Id).Published.Children.Count());
            Assert.AreEqual(0, snap1.Get(kit3.Node.Id).Published.Children.Count());

            // removing a content removes the whole branch
            store.Clear(kit2.Node.Id);
            var snap2 = store.CreateSnapshot();
            SetGetContentByIdOverride(snap2);
            Assert.AreEqual(0, snap2.Get(kit1.Node.Id).Published.Children.Count());
            Assert.IsNull(snap2.Get(kit2.Node.Id));
            Assert.IsNull(snap2.Get(kit3.Node.Id));

            // but not on view 1
            SetGetContentByIdOverride(snap1);
            Assert.AreEqual(1, snap1.Get(kit1.Node.Id).Published.Children.Count());
            Assert.AreEqual(1, snap1.Get(kit2.Node.Id).Published.Children.Count());
            Assert.AreEqual(0, snap1.Get(kit3.Node.Id).Published.Children.Count());
            Assert.IsNotNull(snap1.Get(kit2.Node.Id));
            Assert.IsNotNull(snap1.Get(kit3.Node.Id));
        }

        [Test]
        public void SetBranch()
        {
            var store = new ContentStore2(_logger);

            var props = new[]
            {
                new PublishedPropertyType("prop1", 1, "?"),
            };
            var contentType1 = new PublishedContentType(1, "ContentType1", props);
            store.UpdateContentTypes(null, new[] { contentType1 }, null);

            var kit1 = CreateContentNodeKit(1, 1, null, 1234);
            store.Set(kit1);

            var kit2 = CreateContentNodeKit(1, 2, kit1.Node, 1234);
            store.Set(kit2);

            var kit3 = CreateContentNodeKit(1, 3, kit2.Node, 1234);
            store.Set(kit3);

            // only 1 content at root
            var snap1 = store.CreateSnapshot();
            Assert.AreEqual(1, snap1.GetAtRoot().Count());
            // children
            SetGetContentByIdOverride(snap1);
            Assert.AreEqual(1, snap1.Get(kit1.Node.Id).Published.Children.Count());
            Assert.AreEqual(1, snap1.Get(kit2.Node.Id).Published.Children.Count());
            Assert.AreEqual(0, snap1.Get(kit3.Node.Id).Published.Children.Count());

            // editing a content preserves the branch (NOT moving)
            kit2 = CreateContentNodeKit(1, 2, kit1.Node, 9999);
            store.Set(kit2);
            var snap2 = store.CreateSnapshot();
            SetGetContentByIdOverride(snap2);
            Assert.AreEqual(1, snap2.Get(kit1.Node.Id).Published.Children.Count());
            Assert.AreEqual(1, snap2.Get(kit2.Node.Id).Published.Children.Count());
            Assert.AreEqual(0, snap2.Get(kit3.Node.Id).Published.Children.Count());
            Assert.IsNotNull(snap2.Get(kit2.Node.Id));
            Assert.IsNotNull(snap2.Get(kit3.Node.Id));
            Assert.AreEqual(9999, snap2.Get(kit2.Node.Id).Published.GetProperty("prop1").Value);

            // but not on view 1
            SetGetContentByIdOverride(snap1);
            Assert.AreEqual(1, snap1.Get(kit1.Node.Id).Published.Children.Count());
            Assert.AreEqual(1, snap1.Get(kit2.Node.Id).Published.Children.Count());
            Assert.AreEqual(0, snap1.Get(kit3.Node.Id).Published.Children.Count());
            Assert.IsNotNull(snap1.Get(kit2.Node.Id));
            Assert.IsNotNull(snap1.Get(kit3.Node.Id));
            Assert.AreEqual(1234, snap1.Get(kit2.Node.Id).Published.GetProperty("prop1").Value);
        }

        [Test]
        public async void ContentStore()
        {
            var store = new ContentStore2(_logger);

            var props = new[]
            {
                new PublishedPropertyType("prop1", 1, "?"),
            };

            var contentType1 = new PublishedContentType(1, "ContentType1", props);
            var contentType2 = new PublishedContentType(2, "ContentType2", props);
            store.UpdateContentTypes(null, new[] { contentType1, contentType2 }, null);

            var kit1 = CreateContentNodeKit(1, 1, null, 1234);
            var kit2 = CreateContentNodeKit(2, 2, null, 3456);
            store.Set(kit1);
            store.Set(kit2);

            // we haven't requested a view yet
            Assert.AreEqual(0, store.GenCount);

            // get a snapshot, and again
            var snap1 = store.CreateSnapshot();
            Assert.AreEqual(1, store.GenCount);
            Assert.AreEqual(1, store.SnapCount);
            var snap1B = store.CreateSnapshot();
            Assert.AreNotSame(snap1, snap1B);
            Assert.AreEqual(snap1.Gen, snap1B.Gen);
            Assert.AreEqual(1, store.GenCount);
            Assert.AreEqual(2, store.SnapCount);

            // try to get content
            Assert.AreSame(kit1.Node, snap1.Get(kit1.Node.Id));
            Assert.IsNull(snap1.Get(666));

            kit1 = CreateContentNodeKit(1, 1, null, 5678);
            store.Set(kit1);


            // get a snapshot, and again
            var snap2 = store.CreateSnapshot();
            Assert.AreEqual(2, store.GenCount);
            Assert.AreEqual(3, store.SnapCount);
            Assert.AreNotSame(snap1, snap2);
            Assert.AreNotSame(snap1B, snap2);
            Assert.AreEqual(snap1.Gen + 1, snap2.Gen);
            var snap2B = store.CreateSnapshot();
            Assert.AreNotSame(snap2, snap2B);
            Assert.AreNotSame(snap2.Gen, snap2B.Gen);
            Assert.AreEqual(2, store.GenCount);
            Assert.AreEqual(4, store.SnapCount);

            // try to get content
            Assert.AreSame(kit1.Node, snap2.Get(kit1.Node.Id));
            Assert.AreNotSame(kit1.Node, snap1.Get(kit1.Node.Id));
            Assert.IsNull(snap2.Get(666));

            // each view has its own copy of modified content
            Assert.AreEqual(1234, snap1.Get(kit1.Node.Id).Published.GetProperty("prop1").Value);
            Assert.AreEqual(5678, snap2.Get(kit1.Node.Id).Published.GetProperty("prop1").Value);

            // but same content is shared if not modified
            Assert.AreEqual(3456, snap1.Get(kit2.Node.Id).Published.GetProperty("prop1").Value);
            Assert.AreSame(snap1.Get(kit2.Node.Id), snap2.Get(kit2.Node.Id));

            // dereference snap1 and it's (not) gone
            snap1 = snap1B = null;
            GC.Collect();
            Assert.AreEqual(2, store.GenCount);
            Assert.AreEqual(4, store.SnapCount);
            await store.CollectAsync();

            // in Release mode, it works, but in Debug mode, the weak reference is still alive
            // and for some reason we need to do this to ensure it is collected
#if DEBUG
            GC.Collect();
            await store.CollectAsync();
#endif

            Assert.AreEqual(1, store.GenCount);
            Assert.AreEqual(2, store.SnapCount);

            // dereference snap2 and it's (not) gone
            snap2 = snap2B = null;
            GC.Collect();
            Assert.AreEqual(1, store.GenCount);
            Assert.AreEqual(2, store.SnapCount);
            await store.CollectAsync();
            Assert.AreEqual(0, store.GenCount);
            Assert.AreEqual(0, store.SnapCount);
        }

        [Test]
        public void WeakRef()
        {
            var o = new object();
            var wr = new WeakReference(o);
            Assert.IsTrue(wr.IsAlive);
            GC.Collect();
            Assert.IsTrue(wr.IsAlive);
            o = null;
            GC.Collect();
            Assert.IsFalse(wr.IsAlive);
        }

        [Test]
        public void RemoveFromStore()
        {
            var store = new ContentStore2(_logger);

            var props = new[]
            {
                new PublishedPropertyType("prop1", 1, "?"),
            };

            var contentType1 = new PublishedContentType(1, "ContentType1", props);
            var contentType2 = new PublishedContentType(2, "ContentType2", props);
            var contentType3 = new PublishedContentType(3, "ContentType3", props);
            store.UpdateContentTypes(null, new[] { contentType1, contentType2, contentType3 }, null);

            var kit1 = CreateContentNodeKit(1, 1, null, 1234);
            var kit2 = CreateContentNodeKit(2, 2, null, 3456);
            store.Set(kit1);
            store.Set(kit2);
            Assert.AreEqual(0, store.GenCount);

            var snap1 = store.CreateSnapshot();
            Assert.AreEqual(1, store.GenCount);
            Assert.AreEqual(1, store.SnapCount);
            Assert.IsFalse(snap1.IsEmpty);
            var snap1B = store.CreateSnapshot();
            Assert.AreNotSame(snap1, snap1B);
            Assert.AreEqual(snap1.Gen, snap1B.Gen);
            Assert.AreEqual(1, store.GenCount);
            Assert.AreEqual(2, store.SnapCount);

            Assert.AreSame(kit1.Node, snap1.Get(kit1.Node.Id));
            Assert.AreSame(kit2.Node, snap1.Get(kit2.Node.Id));

            var kit3 = CreateContentNodeKit(3, 3, null, 5678);
            store.Set(kit3);

            Assert.IsNull(snap1.Get(kit3.Node.Id)); // protected!

            store.Set(kit3);
            Assert.IsNull(snap1.Get(kit3.Node.Id)); // protected!

            var snap2 = store.CreateSnapshot();
            Assert.AreSame(kit3.Node, snap2.Get(kit3.Node.Id)); // it's there!

            store.Clear(kit2.Node.Id);
            Assert.AreSame(kit2.Node, snap1.Get(kit2.Node.Id)); // still there
            var snap3 = store.CreateSnapshot();
            Assert.IsNull(snap3.Get(kit2.Node.Id)); // gone!

            Assert.AreEqual(2, snap1.GetAtRoot().Count());
            Assert.AreEqual(3, snap2.GetAtRoot().Count());
            Assert.AreEqual(2, snap3.GetAtRoot().Count());
        }

        private static ContentNodeKit CreateContentNodeKit(int contentTypeId, int id, ContentNode parent, int value)
        {
            var d = new ContentData
            {
                Published = true,
                Name = "Content " + id,
                Version = Guid.Empty,
                TemplateId = -1,
                VersionDate = DateTime.Now,
                WriterId = -1,
                Properties = new Dictionary<string, object>
                {
                    {"prop1", value}
                }
            };
            var n = new ContentNode(id, Guid.NewGuid(),
                (parent == null ? 0 : parent.Level) + 1, (parent == null ? "" : parent.Path) + "/" + id, 0,
                (parent == null ? -1 : parent.Id),
                DateTime.Now, -1);
            var k = new ContentNodeKit
            {
                ContentTypeId = contentTypeId,
                Node = n,
                DraftData = null,
                PublishedData = d
            };
            return k;
        }
    }
}
