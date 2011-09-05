using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Web;
using System.Web.Mvc;
using System.Web.Routing;
using System.Xml.Linq;
using Paging;

namespace Sitemaps
{
    public class SitemapService : ISitemapService
    {
        private static readonly IDictionary<string, ICollection<SitemapNode>> StaticNodes;
        private static readonly IDictionary<string, ICollection<SitemapNode>> DynamicNodes;
        private static readonly object Sync = new object();
        private const string DefaultSitemapName = "TheSiteMap";
        private const string ContentType = "application/xml";
        private const string IfNoneMatchHeader = "If-None-Match";


        public static int PageSize { get; set; }

        private const string SitemapsNamespace = "http://www.sitemaps.org/schemas/sitemap/0.9";
        
        static SitemapService()
        {
            PageSize = 125;
            StaticNodes = new Dictionary<string, ICollection<SitemapNode>>();
            DynamicNodes = new Dictionary<string, ICollection<SitemapNode>>();
        }

        public static void Register()
        {
            Register("sitemap");
        }

        public static void Register(string url)
        {
            Register(DefaultSitemapName, url, "sitemap", "index");
        }

        public static void Register(string name, string url, string controller, string action)
        {
            Register(name, url, controller, action, null, null); 
        }

        // We can now register multiple sitemaps.
        public static void Register(string name, string url, string controller, string action, object constraints, string[] namespaces)
        {
            var routes = RouteTable.Routes;

            using (routes.GetWriteLock())
            {
                routes.MapRoute(name, url, new { controller, action }, constraints, namespaces);
            }
        }

        // We can now register multiple sitemaps.
        public static void Register(string name, string url, object defaults, object constraints, string[] namespaces)
        {
            var routes = RouteTable.Routes;
                       
            using (routes.GetWriteLock())
            {
                routes.MapRoute(name, url, defaults, constraints, namespaces);
            }
        }

        public string GetSitemapXml(string siteMapName, ControllerContext context, int? page, int? count)
        {
            XElement root;
            XNamespace xmlns = SitemapsNamespace;

            // Links for the current page.
            var nodes = GetSitemapNodes(siteMapName, context, page, count.HasValue ? count.Value : PageSize);
            
            // Do we have more links than the current page AND we haven't specified what page to show?
            // If yes, then we need to show the main sitemap index page.
            if (nodes.Count() < nodes.TotalCount && !page.HasValue)
            {
                root = new XElement(xmlns + "sitemapindex");

                var pages = Math.Ceiling(nodes.TotalCount / (double)PageSize);

                for (var i = 0; i < pages; i++)
                {
                    // Each sitemap loc element needs to list the most recent modified item.
                    // As such, we need to keep loading in the nodes, for this page.
                    var pagedNodes = GetSitemapNodes(siteMapName, context, i + 1, count.HasValue ? count.Value : PageSize);
                    
                    // Find the most recent node, from this list.
                    var timestamp = (from x in pagedNodes
                                     orderby x.LastModified descending
                                     select new DateTimeOffset(x.LastModified)).First();
    
                    root.Add(
                    new XElement(xmlns + "sitemap",
                        new XElement(xmlns + "loc", Uri.EscapeUriString(string.Format("{0}/?page={1}", GetUrl(context), i + 1))),
                        new XElement(xmlns + "lastmod", timestamp.ToString("yyyy-MM-ddTHH:mmK",
                            System.Globalization.CultureInfo.InvariantCulture)))
                        );
                }
            }
            else
            {
                root = new XElement(xmlns + "urlset");

                foreach (var node in nodes)
                {
                    root.Add(
                    new XElement(xmlns + "url",
                        new XElement(xmlns + "loc", Uri.EscapeUriString(node.Url)),
                        new XElement(xmlns + "lastmod", new DateTimeOffset(node.LastModified)
                            .ToString("yyyy-MM-ddTHH:mmK", System.Globalization.CultureInfo.InvariantCulture)),
                        new XElement(xmlns + "changefreq", node.Frequency.ToString().ToLowerInvariant()),
                        new XElement(xmlns + "priority", node.Priority.ToString().ToLowerInvariant())
                        ));
                }
            }

            using (var ms = new MemoryStream())
            {
                using (var writer = new StreamWriter(ms, Encoding.UTF8))
                {
                    root.Save(writer);
                }

                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public IPagedEnumerable<SitemapNode> GetSitemapNodes(string siteMapName, ControllerContext context, int? page, int? count)
        {
            var nodes = CacheOrGetStaticNodes(context, siteMapName);

            var source = new List<SitemapNode>(nodes);
            source.AddRange(DynamicNodes[siteMapName]);
            
            return new PagedQueryable<SitemapNode>(source.AsQueryable(), page, count);
        }

        public void AddNode(params SitemapNode[] nodes)
        {
            AddNode(DefaultSitemapName, nodes);
        }

        public void AddNode(string siteMapName, params SitemapNode[] nodes)
        {
            AddNode(nodes.ToList());
        }

        public void AddNode(IEnumerable<SitemapNode> nodes)
        {
            AddNode(nodes, DefaultSitemapName);
        }

        public void AddNode(IEnumerable<SitemapNode> nodes, string siteMapName)
        {
            // Do we have any nodes, for the key?
            if (!DynamicNodes.ContainsKey(siteMapName))
            {
                DynamicNodes.Add(siteMapName, new List<SitemapNode>());
            }
            else
            {
                DynamicNodes[siteMapName].Clear();
            }

            foreach(var node in nodes)
            {
                DynamicNodes[siteMapName].Add(node);
            }
        }

        private IEnumerable<SitemapNode> CacheOrGetStaticNodes(ControllerContext context, string siteMapName)
        {
            lock (Sync)
            {
                if (!StaticNodes.ContainsKey(siteMapName))
                {
                    StaticNodes.Add(siteMapName, new List<SitemapNode>());
                }

                var staticNodes = StaticNodes[siteMapName];
                if (staticNodes.Count == 0)
                {
                    var manifest = GetStaticManifest();
                    var timestamp = DateTime.UtcNow;

                    var nodes = manifest.SelectMany(entry => entry.Value.Select(pair => new SitemapNode(GetUrl(context, new
                    {
                        controller = entry.Key.Name.Replace("Controller", ""),
                        action = pair.Item1
                    }))
                    {
                        LastModified = pair.Item2.LastModified != default(DateTime) ? pair.Item2.LastModified : timestamp,
                        Frequency = pair.Item2.Frequency,
                        Priority = pair.Item2.Priority
                    }));

                    foreach (var node in nodes.Where(n => Uri.IsWellFormedUriString(n.Url, UriKind.Absolute) && n.Url.MatchesRouteWithHttpGet()))
                    {
                        StaticNodes[siteMapName].Add(node);
                    }
                }

                return StaticNodes[siteMapName];
            }
        }

        private static IEnumerable<KeyValuePair<Type, List<Tuple<string, SitemapAttribute>>>> GetStaticManifest()
        {
            lock (Sync)
            {
                var manifest = new Dictionary<Type, List<Tuple<string, SitemapAttribute>>>(0);

                var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.GlobalAssemblyCache && !a.ReflectionOnly);
                var controllerTypes = assemblies.SelectMany(assembly => assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(Controller))));

                foreach (var type in controllerTypes)
                {
                    var attribute = type.GetCustomAttributes(true).OfType<SitemapAttribute>().FirstOrDefault();
                    var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    ProcessMethodSitemaps(manifest, type, methods, attribute);
                }

                return manifest;
            }
        }
        
        private static void ProcessMethodSitemaps(IDictionary<Type, List<Tuple<string, SitemapAttribute>>> manifest, Type type, IEnumerable<MethodInfo> methods, SitemapAttribute defaultAttribute = null)
        {
            foreach (var method in methods)
            {
                SitemapAttribute attribute;
                if (defaultAttribute != null)
                {
                    attribute = defaultAttribute;
                }
                else
                {
                    attribute = method.GetCustomAttributes(true).OfType<SitemapAttribute>().FirstOrDefault();
                    if (attribute == null)
                    {
                        continue;
                    }
                }

                // Support the user changing the action name with an attribute
                var action = method.Name;
                var actionName = method.GetCustomAttributes(true).OfType<ActionNameAttribute>().FirstOrDefault();
                if (actionName != null)
                {
                    action = actionName.Name;
                }
                
                if (!manifest.ContainsKey(type))
                {
                    manifest.Add(type, new List<Tuple<string, SitemapAttribute>>());
                }

                manifest[type].Add(new Tuple<string, SitemapAttribute>(action, attribute));
            }
        }

        protected string GetUrl(ControllerContext context)
        {
            return GetUrl(context.RequestContext, context.RouteData.Values);
        }

        protected string GetUrl(ControllerContext context, object routeValues)
        {
            var values = new RouteValueDictionary(routeValues);
            var request = new RequestContext(context.HttpContext, context.RouteData);

            return GetUrl(request, values);
        }

        private static string GetUrl(RequestContext request, RouteValueDictionary values)
        {
            var routes = RouteTable.Routes;
            var data = routes.GetVirtualPath(request, values);

            if(data == null)
            {
                return null;
            }

            var baseUrl = request.HttpContext.Request.Url;
            var relativeUrl = data.VirtualPath;
            
            return request.HttpContext != null &&
                   (request.HttpContext.Request != null && baseUrl != null)
                       ? new Uri(baseUrl, relativeUrl).AbsoluteUri
                       : null;
        }

        public static ActionResult RenderSiteMap(ControllerContext controllerContext, int? page, int? count)
        {
            return RenderSiteMap(DefaultSitemapName, controllerContext, page, count);
        }

        public static ActionResult RenderSiteMap(string siteMapName, ControllerContext controllerContext, int? page, int? count)
        {
            ISitemapService sitemapService = new SitemapService();
            var content = sitemapService.GetSitemapXml(siteMapName, controllerContext, page, count);
            var etag = Md5(content);

            if (BrowserIsRequestingFileIdentifiedBy(controllerContext.RequestContext.HttpContext.Request, etag))
            {
                return NotModified(controllerContext.HttpContext.Response);
            }

            var cache = controllerContext.HttpContext.Response.Cache;
            cache.SetCacheability(HttpCacheability.Public);
            cache.SetETag(etag);

            return new ContentResult
                       {
                           Content = content, 
                           ContentEncoding = Encoding.UTF8, 
                           ContentType = ContentType
                       };
        }

        private static bool BrowserIsRequestingFileIdentifiedBy(HttpRequestBase request, string etag)
        {
            if (request.Headers[IfNoneMatchHeader] == null)
            {
                return false;
            }

            var ifNoneMatch = request.Headers[IfNoneMatchHeader];
            return ifNoneMatch.Equals(etag, StringComparison.OrdinalIgnoreCase);
        }

        private static string Md5(string input)
        {
            var sb = new StringBuilder();
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
                foreach (var hex in hash)
                {
                    sb.Append(hex.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        public static ActionResult NotModified(HttpResponseBase responseBase)
        {
            return StopWith(responseBase, HttpStatusCode.NotModified);
        }

        private static ActionResult StopWith(HttpResponseBase responseBase, HttpStatusCode statusCode)
        {
            responseBase.StatusCode = (int)statusCode;
            responseBase.SuppressContent = true;
            responseBase.TrySkipIisCustomErrors = true;
            return null;
        }
    }
}