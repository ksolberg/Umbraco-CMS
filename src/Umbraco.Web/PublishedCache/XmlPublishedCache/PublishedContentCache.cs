using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.XPath;
using Umbraco.Core;
using Umbraco.Core.Cache;
using Umbraco.Core.Configuration;
using Umbraco.Core.Models;
using Umbraco.Core.Models.PublishedContent;
using Umbraco.Core.Xml;
using Umbraco.Web.Routing;
using GlobalSettings = umbraco.GlobalSettings;

namespace Umbraco.Web.PublishedCache.XmlPublishedCache
{
    internal class PublishedContentCache : PublishedCacheBase, IPublishedContentCache
    {
        // initialize a PublishedContentCache instance with
        // an XmlStore containing the master xml
        // an ICacheProvider that should be at request-level
        // a RoutesCache - need to cleanup that one
        // a preview token string (or null if not previewing)
        public PublishedContentCache(
            XmlStore xmlStore, // an XmlStore containing the master xml
            IDomainCache domainCache, // an IDomainCache implementation
            ICacheProvider cacheProvider, // an ICacheProvider that should be at request-level
            PublishedContentTypeCache contentTypeCache, // a PublishedContentType cache
            RoutesCache routesCache, // a RoutesCache
            string previewToken) // a preview token string (or null if not previewing)
            : base(previewToken.IsNullOrWhiteSpace() == false)
        {
            _cacheProvider = cacheProvider;
            _routesCache = routesCache; // may be null for unit-testing
            _contentTypeCache = contentTypeCache;
            _domainCache = domainCache;
            _domainHelper = new DomainHelper(_domainCache);

            _xmlStore = xmlStore;
            _xml = _xmlStore.Xml; // capture - because the cache has to remain consistent

            if (previewToken.IsNullOrWhiteSpace() == false)
                _previewContent = new PreviewContent(_xmlStore, previewToken);
        }

        private readonly ICacheProvider _cacheProvider;
        private readonly RoutesCache _routesCache;
        private readonly IDomainCache _domainCache;
        private readonly DomainHelper _domainHelper;
        private readonly PublishedContentTypeCache _contentTypeCache;

        // for unit tests
        internal RoutesCache RoutesCache { get { return _routesCache; } }

        // for unit tests
        internal XmlStore XmlStore { get { return _xmlStore; } }

        #region Routes

        public virtual IPublishedContent GetByRoute(bool preview, string route, bool? hideTopLevelNode = null)
        {
            if (route == null) throw new ArgumentNullException("route");

            // try to get from cache if not previewing
            var contentId = (preview || _routesCache == null) ? 0 : _routesCache.GetNodeId(route);

            // if found id in cache then get corresponding content
            // and clear cache if not found - for whatever reason
            IPublishedContent content = null;
            if (contentId > 0)
            {
                content = GetById(preview, contentId);
                if (content == null && _routesCache != null)
                    _routesCache.ClearNode(contentId);
            }

            // still have nothing? actually determine the id
            hideTopLevelNode = hideTopLevelNode ?? GlobalSettings.HideTopLevelNodeFromPath; // default = settings
            content = content ?? DetermineIdByRoute(preview, route, hideTopLevelNode.Value);

            // cache if we have a content and not previewing
            if (content != null && preview == false && _routesCache != null)
                AddToCacheIfDeepestRoute(content, route);

            return content;
        }

        private void AddToCacheIfDeepestRoute(IPublishedContent content, string route)
        {
            var domainRootNodeId = route.StartsWith("/") ? -1 : int.Parse(route.Substring(0, route.IndexOf('/')));
            // so we have a route that maps to a content... say "1234/path/to/content" - however, there could be a
            // domain set on "to" and route "4567/content" would also map to the same content - and due to how
            // urls computing work (by walking the tree up to the first domain we find) it is that second route
            // that would be returned - the "deepest" route - and that is the route we want to cache, *not* the
            // longer one - so make sure we don't cache the wrong route

            var deepest = DomainHelper.ExistsDomainInPath(_domainCache.GetAll(false), content.Path, domainRootNodeId) == false;

            if (deepest)
                _routesCache.Store(content.Id, route);
        }

        public IPublishedContent GetByRoute(string route, bool? hideTopLevelNode = null)
        {
            return GetByRoute(PreviewDefault, route, hideTopLevelNode);
        }

        public virtual string GetRouteById(bool preview, int contentId)
        {
            // try to get from cache if not previewing
            var route = (preview || _routesCache == null) ? null : _routesCache.GetRoute(contentId);

            // if found in cache then return
            if (route != null)
                return route;

            // else actually determine the route
            route = DetermineRouteById(preview, contentId);

            // node not found
            if (route == null)
                return null;

            // find the content back, detect routes collisions: we should find ourselves back,
            // else it means that another content with "higher priority" is sharing the same route.
            // perf impact:
            // - non-colliding, adds one complete "by route" lookup, only on the first time a url is computed (then it's cached anyways)
            // - colliding, adds one "by route" lookup, the first time the url is computed, then one dictionary looked each time it is computed again
            // assuming no collisions, the impact is one complete "by route" lookup the first time each url is computed
            var loopId = preview || _routesCache == null ? 0 : _routesCache.GetNodeId(route); // might be cached already in case of collision
            if (loopId == 0)
            {
                var content = DetermineIdByRoute(preview, route, GlobalSettings.HideTopLevelNodeFromPath);

                // add the other route to cache so next time we have it already
                if (route != null && preview == false && _routesCache != null)
                    AddToCacheIfDeepestRoute(content, route);

                loopId = content == null ? 0 : content.Id; // though... 0 here would be quite weird?
            }

            // cache if we have a route and not previewing and it's not a colliding route
            // (the result of DetermineRouteById is always the deepest route)
            if (/*route != null &&*/ preview == false && loopId == contentId && _routesCache != null)
                _routesCache.Store(contentId, route);

            // return route if no collision, else report collision
            return loopId == contentId ? route : ("err/" + loopId);
        }

        public string GetRouteById(int contentId)
        {
            return GetRouteById(PreviewDefault, contentId);
        }

        IPublishedContent DetermineIdByRoute(bool preview, string route, bool hideTopLevelNode)
        {
            if (route == null) throw new ArgumentNullException("route");

            //the route always needs to be lower case because we only store the urlName attribute in lower case
            route = route.ToLowerInvariant();

            var pos = route.IndexOf('/');
            var path = pos == 0 ? route : route.Substring(pos);
            var startNodeId = pos == 0 ? 0 : int.Parse(route.Substring(0, pos));
            IEnumerable<XPathVariable> vars;

            var xpath = CreateXpathQuery(startNodeId, path, hideTopLevelNode, out vars);

            //check if we can find the node in our xml cache
            var content = GetSingleByXPath(preview, xpath, vars == null ? null : vars.ToArray());

            // if hideTopLevelNodePath is true then for url /foo we looked for /*/foo
            // but maybe that was the url of a non-default top-level node, so we also
            // have to look for /foo (see note in ApplyHideTopLevelNodeFromPath).
            if (content == null && hideTopLevelNode && path.Length > 1 && path.IndexOf('/', 1) < 0)
            {
                xpath = CreateXpathQuery(startNodeId, path, false, out vars);
                content = GetSingleByXPath(preview, xpath, vars == null ? null : vars.ToArray());
            }

            return content;
        }

        string DetermineRouteById(bool preview, int contentId)
        {
            var node = GetById(preview, contentId);
            if (node == null)
                return null;

            // walk up from that node until we hit a node with a domain,
            // or we reach the content root, collecting urls in the way
            var pathParts = new List<string>();
            var n = node;
            var hasDomains = _domainHelper.NodeHasDomains(n.Id);
            while (hasDomains == false && n != null) // n is null at root
            {
                // get the url
                var urlName = n.UrlName;
                pathParts.Add(urlName);

                // move to parent node
                n = n.Parent;
                hasDomains = n != null && _domainHelper.NodeHasDomains(n.Id);
            }

            // no domain, respect HideTopLevelNodeFromPath for legacy purposes
            if (hasDomains == false && GlobalSettings.HideTopLevelNodeFromPath)
                ApplyHideTopLevelNodeFromPath(node, pathParts, preview);

            // assemble the route
            pathParts.Reverse();
            var path = "/" + string.Join("/", pathParts); // will be "/" or "/foo" or "/foo/bar" etc
            var route = (n == null ? "" : n.Id.ToString(CultureInfo.InvariantCulture)) + path;

            return route;
        }

        void ApplyHideTopLevelNodeFromPath(IPublishedContent content, IList<string> segments, bool preview)
        {
            // in theory if hideTopLevelNodeFromPath is true, then there should be only once
            // top-level node, or else domains should be assigned. but for backward compatibility
            // we add this check - we look for the document matching "/" and if it's not us, then
            // we do not hide the top level path
            // it has to be taken care of in GetByRoute too so if
            // "/foo" fails (looking for "/*/foo") we try also "/foo".
            // this does not make much sense anyway esp. if both "/foo/" and "/bar/foo" exist, but
            // that's the way it works pre-4.10 and we try to be backward compat for the time being
            if (content.Parent == null)
            {
                var rootNode = GetByRoute(preview, "/", true);
                if (rootNode == null)
                    throw new Exception("Failed to get node at /.");
                if (rootNode.Id == content.Id) // remove only if we're the default node
                    segments.RemoveAt(segments.Count - 1);
            }
            else
            {
                segments.RemoveAt(segments.Count - 1);
            }
        }

        #endregion

        #region Converters

        private IPublishedContent ConvertToDocument(XmlNode xmlNode, bool isPreviewing, ICacheProvider cacheProvider)
		{
		    return xmlNode == null
                ? null
                : (new XmlPublishedContent(xmlNode, isPreviewing, cacheProvider, _contentTypeCache)).CreateModel();
		}

        private IEnumerable<IPublishedContent> ConvertToDocuments(XmlNodeList xmlNodes, bool isPreviewing, ICacheProvider cacheProvider)
        {
            return xmlNodes.Cast<XmlNode>()
                .Select(xmlNode => (new XmlPublishedContent(xmlNode, isPreviewing, cacheProvider, _contentTypeCache)).CreateModel());
        }

        #endregion

        #region Getters

        public override IPublishedContent GetById(bool preview, int nodeId)
    	{
    		return ConvertToDocument(GetXml(preview).GetElementById(nodeId.ToString(CultureInfo.InvariantCulture)), preview, _cacheProvider);
    	}

        public override bool HasById(bool preview, int contentId)
        {
            return GetXml(preview).CreateNavigator().MoveToId(contentId.ToString(CultureInfo.InvariantCulture));
        }

        public override IEnumerable<IPublishedContent> GetAtRoot(bool preview)
        {
            return ConvertToDocuments(GetXml(preview).SelectNodes(XPathStrings.RootDocuments), preview, _cacheProvider);
		}

        public override IPublishedContent GetSingleByXPath(bool preview, string xpath, params XPathVariable[] vars)
        {
            if (xpath == null) throw new ArgumentNullException("xpath");
            if (string.IsNullOrWhiteSpace(xpath)) return null;

            var xml = GetXml(preview);
            var node = vars == null
                ? xml.SelectSingleNode(xpath)
                : xml.SelectSingleNode(xpath, vars);
            return ConvertToDocument(node, preview, _cacheProvider);
        }

        public override IPublishedContent GetSingleByXPath(bool preview, XPathExpression xpath, params XPathVariable[] vars)
        {
            if (xpath == null) throw new ArgumentNullException("xpath");

            var xml = GetXml(preview);
            var node = vars == null
                ? xml.SelectSingleNode(xpath)
                : xml.SelectSingleNode(xpath, vars);
            return ConvertToDocument(node, preview, _cacheProvider);
        }

        public override IEnumerable<IPublishedContent> GetByXPath(bool preview, string xpath, params XPathVariable[] vars)
        {
            if (xpath == null) throw new ArgumentNullException("xpath");
            if (string.IsNullOrWhiteSpace(xpath)) return Enumerable.Empty<IPublishedContent>();

            var xml = GetXml(preview);
            var nodes = vars == null
                ? xml.SelectNodes(xpath)
                : xml.SelectNodes(xpath, vars);
            return ConvertToDocuments(nodes, preview, _cacheProvider);
        }

        public override IEnumerable<IPublishedContent> GetByXPath(bool preview, XPathExpression xpath, params XPathVariable[] vars)
        {
            if (xpath == null) throw new ArgumentNullException("xpath");

            var xml = GetXml(preview);
            var nodes = vars == null
                ? xml.SelectNodes(xpath)
                : xml.SelectNodes(xpath, vars);
            return ConvertToDocuments(nodes, preview, _cacheProvider);
        }

        public override bool HasContent(bool preview)
        {
	        var xml = GetXml(preview);
			if (xml == null)
				return false;
			var node = xml.SelectSingleNode(XPathStrings.RootDocuments);
			return node != null;
        }

        public override XPathNavigator CreateNavigator(bool preview)
        {
            var xml = GetXml(preview);
            return xml.CreateNavigator();
        }

        public override XPathNavigator CreateNodeNavigator(int id, bool preview)
        {
            // hackish - backward compatibility ;-(

            XPathNavigator navigator = null;

            if (preview)
            {
                var node = _xmlStore.GetPreviewXmlNode(id);
                if (node != null)
                {
                    navigator = node.CreateNavigator();
                }
            }
            else
            {
                var node = GetXml(false).GetElementById(id.ToInvariantString());
                if (node != null)
                {
                    var doc = new XmlDocument();
                    var clone = doc.ImportNode(node, false);
                    var xpath = UmbracoConfig.For.UmbracoSettings().Content.UseLegacyXmlSchema ? "./data" : "./* [not(@id)]";
                    var props = node.SelectNodes(xpath);
                    if (props == null) throw new Exception("oops"); 
                    foreach (var n in props.Cast<XmlNode>())
                        clone.AppendChild(doc.ImportNode(n, true));
                    navigator = node.CreateNavigator();
                }
            }

            return navigator;
        }

        #endregion

        #region Legacy Xml

        private readonly XmlStore _xmlStore;
        private XmlDocument _xml;
        private readonly PreviewContent _previewContent;

        internal XmlDocument GetXml(bool preview)
        {
            // not trying to be thread-safe here, that's not the point

            if (preview)
            {
                // Xml cache does not support retrieving preview content when not previewing
                if (_previewContent == null)
                    throw new InvalidOperationException("Cannot retrieve preview content when not previewing.");

                // PreviewContent tries to load the Xml once and if it fails,
                // it invalidates itself and always return null for XmlContent.
                var previewXml = _previewContent.XmlContent;
                if (previewXml != null)
                    return previewXml;
            }

            return _xml;
        }

        internal void Resync()
        {
            _xml = _xmlStore.Xml; // re-capture

            // note: we're not resyncing "preview" because that would mean re-building the whole
            // preview set which is costly, so basically when previewing, there will be no resync.

            // clear recursive properties cached by XmlPublishedContent.GetProperty
            // assume that nothing else is going to cache IPublishedProperty items (else would need to do ByKeySearch)
            // NOTE also clears all the media cache properties, which is OK (see media cache)
            _cacheProvider.ClearCacheObjectTypes<IPublishedProperty>();
            //_cacheProvider.ClearCacheByKeySearch("XmlPublishedCache.PublishedContentCache:RecursiveProperty-");
        }

        #endregion

        #region XPathQuery

        static readonly char[] SlashChar = { '/' };

        protected string CreateXpathQuery(int startNodeId, string path, bool hideTopLevelNodeFromPath, out IEnumerable<XPathVariable> vars)
        {
            string xpath;
            vars = null;

            if (path == string.Empty || path == "/")
            {
                // if url is empty
                if (startNodeId > 0)
                {
					// if in a domain then use the root node of the domain
					xpath = string.Format(XPathStrings.Root + XPathStrings.DescendantDocumentById, startNodeId);                    
                }
                else
                {
                    // if not in a domain - what is the default page?
                    // let's say it is the first one in the tree, if any -- order by sortOrder

					// but!
					// umbraco does not consistently guarantee that sortOrder starts with 0
					// so the one that we want is the one with the smallest sortOrder
					// read http://stackoverflow.com/questions/1128745/how-can-i-use-xpath-to-find-the-minimum-value-of-an-attribute-in-a-set-of-elemen

					// so that one does not work, because min(@sortOrder) maybe 1
					// xpath = "/root/*[@isDoc and @sortOrder='0']";

					// and we can't use min() because that's XPath 2.0
					// that one works
					xpath = XPathStrings.RootDocumentWithLowestSortOrder;
                }
            }
            else
            {
                // if url is not empty, then use it to try lookup a matching page
                var urlParts = path.Split(SlashChar, StringSplitOptions.RemoveEmptyEntries);
                var xpathBuilder = new StringBuilder();
                int partsIndex = 0;
                List<XPathVariable> varsList = null;

                if (startNodeId == 0)
                {
                    // if hiding, first node is not in the url
                    xpathBuilder.Append(hideTopLevelNodeFromPath ? XPathStrings.RootDocuments : XPathStrings.Root);
                }
                else
                {
					xpathBuilder.AppendFormat(XPathStrings.Root + XPathStrings.DescendantDocumentById, startNodeId);
					// always "hide top level" when there's a domain
                }

                while (partsIndex < urlParts.Length)
                {
                    var part = urlParts[partsIndex++];
                    if (part.Contains('\'') || part.Contains('"'))
                    {
                        // use vars, escaping gets ugly pretty quickly
                        varsList = varsList ?? new List<XPathVariable>();
                        var varName = string.Format("var{0}", partsIndex);
                        varsList.Add(new XPathVariable(varName, part));
                        xpathBuilder.AppendFormat(XPathStrings.ChildDocumentByUrlNameVar, varName);
                    }
                    else
                    {
                        xpathBuilder.AppendFormat(XPathStrings.ChildDocumentByUrlName, part);

                    }
                }

                xpath = xpathBuilder.ToString();
                if (varsList != null)
                    vars = varsList.ToArray();
            }

            return xpath;
        }

        #endregion

        #region Detached

        public IPublishedProperty CreateDetachedProperty(PublishedPropertyType propertyType, object value, bool isPreviewing)
        {
            if (propertyType.IsDetachedOrNested == false)
                throw new ArgumentException("Property type is neither detached nor nested.", "propertyType");
            return new XmlPublishedProperty(propertyType, isPreviewing, value.ToString());
        }

        #endregion

        #region Content types

        public override PublishedContentType GetContentType(int id)
        {
            return _contentTypeCache.Get(PublishedItemType.Content, id);
        }

        public override PublishedContentType GetContentType(string alias)
        {
            return _contentTypeCache.Get(PublishedItemType.Content, alias);
        }

        #endregion
    }
}