using System.Collections.Generic;
using System.Web.Mvc;
using Paging;

namespace Sitemaps
{
    /// <summary>
    /// A service for supporting the Sitemaps protocol
    /// <see href="http://www.sitemaps.org/"/>
    /// </summary>
    public interface ISitemapService
    {
        string GetSitemapXml(string name, ControllerContext controllerContext, int? page, int? count);
        IPagedEnumerable<SitemapNode> GetSitemapNodes(string name, ControllerContext context, int? page, int? count);

        void AddNode(params SitemapNode[] nodes);
        void AddNode(string siteMapName, params SitemapNode[] nodes);
        void AddNode(IEnumerable<SitemapNode> nodes);
        void AddNode(IEnumerable<SitemapNode> nodes, string siteMapName);
    }
}
