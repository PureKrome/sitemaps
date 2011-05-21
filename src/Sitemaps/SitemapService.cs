using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Mvc;
using System.Web.Routing;
using System.Xml.Linq;
using Paging;

namespace Sitemaps
{
    public class SitemapService : ISitemapService
    {
        private static readonly ICollection<SitemapNode> _staticNodes;
        private static readonly ICollection<SitemapNode> _dynamicNodes;

        private static readonly object _sync = new object();

        public static int PageSize { get; set; }

        private const string SitemapsNamespace = "http://www.sitemaps.org/schemas/sitemap/0.9";
        
        static SitemapService()
        {
            PageSize = 125;
            _staticNodes = new List<SitemapNode>(0);
            _dynamicNodes = new List<SitemapNode>(0);
        }

        public static void Register()
        {
            Register("sitemap");
        }

        public static void Register(string url)
        {
            var routes = RouteTable.Routes;

            using (routes.GetWriteLock())
            {
                routes.MapRoute("sitemaps", url, new { controller = "Sitemap", action = "Index" });
            }
        }

        public string GetSitemapXml(ControllerContext context, int? page, int? count)
        {
            XElement root;
            XNamespace xmlns = SitemapsNamespace;

            var nodes = GetSitemapNodes(context, page, count.HasValue ? count.Value : PageSize);
            
            if (nodes.Count() < nodes.TotalCount && !page.HasValue)
            {
                root = new XElement(xmlns + "sitemapindex");

                var pages = Math.Ceiling(nodes.TotalCount / (double)PageSize);

                var timestamp = nodes.First().LastModified;

                for (var i = 0; i < pages; i++)
                {
                    root.Add(
                    new XElement(xmlns + "sitemap",
                        new XElement(xmlns + "loc", Uri.EscapeUriString(string.Format("{0}/?page={1}", GetUrl(context), i + 1))),
                        new XElement(xmlns + "lastmod", timestamp.ToString("s", System.Globalization.CultureInfo.InvariantCulture)))
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
                        new XElement(xmlns + "lastmod", node.LastModified.ToString("s", System.Globalization.CultureInfo.InvariantCulture)),
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

        public IPagedEnumerable<SitemapNode> GetSitemapNodes(ControllerContext context, int? page, int? count)
        {
            var nodes = CacheOrGetStaticNodes(context);

            var source = new List<SitemapNode>(nodes);
            source.AddRange(_dynamicNodes);
            
            return new PagedQueryable<SitemapNode>(source.AsQueryable(), page, count);
        }

        public void AddNode(params SitemapNode[] nodes)
        {
            AddNode(nodes.ToList());
        }

        public void AddNode(IEnumerable<SitemapNode> nodes)
        {
            foreach(var node in nodes)
            {
                _dynamicNodes.Add(node);
            }
        }

        private IEnumerable<SitemapNode> CacheOrGetStaticNodes(ControllerContext context)
        {
            lock (_sync)
            {
                if (_staticNodes.Count == 0)
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
                        _staticNodes.Add(node);
                    }
                }

                return _staticNodes;
            }
        }

        private static IEnumerable<KeyValuePair<Type, List<Tuple<string, SitemapAttribute>>>> GetStaticManifest()
        {
            lock (_sync)
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
            var data = routes.GetVirtualPathForArea(request, values);

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
    }
}