using System;
using System.Collections.Generic;
using System.Xml;
using System.Web;
using JSite;
using System.Configuration;
using System.Web.SessionState;
using System.Text.RegularExpressions;
using System.Text;

/// <summary>
/// Summary description for SPProcessor
/// </summary>
public class SPProcessor2 : IHttpHandler, IReadOnlySessionState
{

    public IHttpHandler OriginalHandler { get; set; }
    XmlDocument xResponse = new XmlDocument();
    XmlDocument xSM;
    XmlDocument xTemplates;

    string pageUrl;
    string container;

    string dataClientCash;
    string requestNum;
    JTransform jTrans = new JTransform();
    DataRef dataRef;


    public SPProcessor2(string pageUrl)
    {
        Process(pageUrl, string.Empty, "0");
    }

    public SPProcessor2(string pageUrl, string container, string requestNum, IHttpHandler handler)
    {
        OriginalHandler = handler;
        this.pageUrl = pageUrl;
        this.container = container;

        this.requestNum = requestNum;

        //Process(pageUrl, container, cashedTemplates, requestNum);
    }

    private void Process(string pageUrl, string container, string requestNum)
    {
        if (HttpContext.Current.Request.Url.Authority.ToLower().Contains("localhost"))
            ProcessIt(pageUrl, container, requestNum);
        else
        {
            try
            {
                ProcessIt(pageUrl, container, requestNum);
            }
            catch (System.Threading.ThreadAbortException tae)
            {
                // do nothing
            }
            catch (Exception ex)
            {
                if (!ex.Message.Contains("Server cannot set status after HTTP headers have been sent."))
                    ExceptionLogger.Instance.LogException(ex);

            }
        }

    }

    private void ProcessIt(string pageUrl, string container, string requestNum)
    {
        dataClientCash = HttpContext.Current.Request.Form["DataClientCash"];
        if (dataClientCash == null)
            dataClientCash = ",";


        DateTime start = DateTime.Now;

        string smP = "~/Settings/SiteMap.Auto.xml";
        if (ConfigurationManager.AppSettings["Env"] != "Dev")
        {
            xSM = (XmlDocument)HttpContext.Current.Cache["SiteMap.Auto.xml"];

        }
        if (xSM == null)
        {
            xSM = new XmlDocument(new VirtualPath(smP));
            HttpContext.Current.Cache.Insert("SiteMap.Auto.xml", xSM, null, DateTime.Now.AddHours(12), TimeSpan.Zero);
        }

        string tP = "~/Settings/Templates.xml";
        if (ConfigurationManager.AppSettings["Env"] != "Dev")
        {
            xTemplates = (XmlDocument)HttpContext.Current.Cache["Templates.xml"];

        }
        if (xTemplates == null)
        {
            xTemplates = new XmlDocument(new VirtualPath(tP));
            HttpContext.Current.Cache.Insert("Templates.xml", xTemplates, null, DateTime.Now.AddHours(12), TimeSpan.Zero);
        }

        pageUrl = pageUrl.Replace("%.", "&").Replace("&", "|").Replace("?", "//").Replace("///", "//").Replace("=", ":"); ;
        string url = "";
        try
        {
            url = UrlExtractor.Instance.GetUrl(pageUrl);
            if (url.IsNullOrEmpty())
            {
                ExceptionLogger.Instance.LogException(new HttpException(404, "SPProcessor2 404 Not Found"), "server:" + HttpContext.Current.Server.MachineName + ", pageKey:" + url + " , container:" + container + "sm pages:" + xSM.SelectNodes("//Page").Count.ToString1() + ", url:" + pageUrl, false);
            }
            if (FeatureSupport.Get("Jamus") == "1")
            {
                switch (url)
                {
                    case "LooseDiamonds":
                        url = "JamusLooseDiamonds";
                        break;

                    case "SearchDiamonds":
                        url = "JamusSearchDiamonds";
                        break;

                    case "MobileFancyColorDiamonds":
                        url = "MobileJamusFancyColorDiamonds";
                        break;

                    case "FancyColorDiamonds":
                        url = "JamusFancyColorDiamonds";
                        break;
                }


            }


        }
        catch (RedirectException re)
        {
            Redirect(re.RedirectTo, "301");
            return;
        }

        string xPath = "//Page[@Url='" + url + "']/Template";

        string userIDKey = ConfigurationManager.AppSettings["userIDKey"];
        bool needloginRedirect = false;
        if (!userIDKey.IsNullOrEmpty())
        {
            XmlNode xNode = xSM.SelectSingleNode("//Page[@Url='" + url + "']");
            if (xNode != null)
            {
                XmlAttribute RequireLogin = xNode.Attributes["RequireLogin"];
                if (
                    RequireLogin != null && RequireLogin.Value == "True" &&
                    pageUrl.ToLower() != "login/" &&
                    pageUrl.ToLower() != "accountactivation/" &&
                    HttpContext.Current.Session[userIDKey] == null
                   )
                {
                    xPath = "//Page[translate(@Url,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')='login']/Template";
                    container = null;
                    HttpContext.Current.Response.Redirect(ConfigurationManager.AppSettings["Domain"] + "Login/", true);
                    needloginRedirect = true;
                }
            }
        }

        if (needloginRedirect == true)
            LoginRedirect();
        else
            ProcessPage(container, requestNum, start, url, xPath, userIDKey);


    }

    private void LoginRedirect()
    {
        Redirect(ConfigurationManager.AppSettings["Domain"] + "Login/", "302");
    }

    private void Redirect(string to, string type)
    {
        XmlNode xp = xResponse.CreateElement("Page");
        XmlAttribute at = xResponse.CreateAttribute("Redirect");
        at.Value = to.ToLower();
        xp.Attributes.Append(at);
        at = xResponse.CreateAttribute("RedirectType");
        at.Value = type;
        xp.Attributes.Append(at);
        xResponse.AppendChild(xp);
    }


    private string EnsureAbsoluteUrls(string html)
    {
        string prefix = ConfigurationManager.AppSettings["Domain"];
        string pattern = "<(.*?)(href)=\"(?!http|//|javascript|mailto)(.*?)\"(.*?)>";

        var reg = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
        var result = reg.Replace(html, "<$1$2=\"" + prefix + "$3\"$4>");
        return result.Replace(prefix + "/", prefix);
    }


    private void ProcessPage(string container, string requestNum, DateTime start, string pageKey, string xPath, string userIDKey)
    {
        if (!container.IsNullOrEmpty())
            xPath = "//Template[@Container='" + container + "' and ancestor::Page[@Url='" + pageKey + "']]";
        XmlNode xT = xSM.SelectSingleNode(xPath);
        if (xT == null)
        {
            //if (HttpContext.Current.Request.Url.Authority.ToLower().Contains("localhost") && !container.IsNullOrEmpty())
            //  throw new Exception("Could not find Container " + container);

            xT = xSM.SelectSingleNode("//Page[@Url='" + pageKey + "']/Template");
            if (xT == null)
            {
                if (!pageKey.IsNullOrEmpty())
                {
                    ExceptionLogger.Instance.LogException(new HttpException(404, "SPProcessor2 404 Not Found"), "server:" + HttpContext.Current.Server.MachineName + ", pageKey:" + pageKey + " , container:" + container + "sm pages:" + xSM.SelectNodes("//Page").Count.ToString1(), false);
                }
                // throw new HttpException(404, "HTTP/1.1 404 Not Found");
                HttpContext.Current.Response.Clear();
                HttpContext.Current.Response.TrySkipIisCustomErrors = false;
                if (!HttpContext.Current.Request.QueryString["InternalUrl"].IsNullOrEmpty()
                    && HttpContext.Current.Request.QueryString["InternalUrl"].Length > 200)
                {
                    HttpContext.Current.Response.StatusCode = 410;
                    HttpContext.Current.Response.Status = "410 Gone";
                }
                else
                {
                    //HttpContext.Current.Response.Status = "404 Not Found";
                    //HttpContext.Current.Response.StatusCode = 404;
                    HttpContext.Current.Response.StatusCode = 410;
                    HttpContext.Current.Response.Status = "410 Gone";
                }

                HttpContext.Current.Response.Write(EnsureAbsoluteUrls(FilesHandler.Read(HttpContext.Current.Request.MapPath("~/404.htm"))));

                HttpContext.Current.Response.End();
            }
        }

        XmlNode xSMPage = xSM.SelectSingleNode("//Page[@Url='" + pageKey + "']");

        if (HttpContext.Current.Response.StatusCode == 200)
        {
            int cacheMin;
            if (!int.TryParse(xSMPage.GetAttribute("HtmlCacheMin"), out cacheMin))
            {
                cacheMin = 10;
            }

            // cache policy:
            bool isCookieable = HttpContext.Current.Request.Cookies["isCookieable"] != null && HttpContext.Current.Request.Cookies["isCookieable"].Value == "true";
            int csCount = xT.SelectNodes("descendant::Param[@Type='Cookie' or @Type='Session']").Count;
            bool adminMode = (HttpContext.Current.Request.Cookies["admin_mode"] != null);

            bool diableCahce = true;

            if (csCount > 0 || FeatureSupport.GetServerVal("HtmlCache") != "1" || isCookieable || cacheMin == 0 || adminMode || diableCahce)
            {
                // no cache
                HttpContext.Current.Response.CacheControl = "private"; // HTTP 1.1.
                HttpContext.Current.Response.AppendHeader("Pragma", "no-cache"); // HTTP 1.0.
                HttpContext.Current.Response.AppendHeader("Expires", "0"); // Proxies.
            }
            else
            {
                DateTime cacheEndedTime = DateTime.Now.AddMinutes(cacheMin);
                DateTime dt = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, 1, 0, 0);

                if (DateTime.Now.DayOfWeek != DayOfWeek.Friday && DateTime.Now.DayOfWeek != DayOfWeek.Saturday)
                {
                    // If current NY Time (or the time with cache) between 01:00 AM to 09:00 AM then set cache to 10 minutes
                    if ((DateTime.Now.Hour <= 9 && DateTime.Now.Hour >= 1 && cacheMin > 10))
                    {
                        cacheMin = 10;
                    }
                    else if (DateTime.Now.Hour < 1 && cacheEndedTime > dt)
                    {
                        cacheMin = dt.Subtract(DateTime.Now).Minutes;
                    }
                    else if (DateTime.Now.Hour > 9 && cacheEndedTime > dt.AddDays(1))
                    {
                        cacheMin = dt.AddDays(1).Subtract(DateTime.Now).Minutes;
                    }
                }
                HttpContext.Current.Response.CacheControl = "public"; // HTTP 1.1.
                HttpContext.Current.Response.AppendHeader("Expires", DateTime.Now.ToUniversalTime().AddMinutes(cacheMin).ToString("R")); // Proxies.

                Params.Request["HtmlCacheMin"] = cacheMin.ToString();
            }
        }
        XmlNode xp = xResponse.CreateElement("Page");
        XmlAttribute at = xResponse.CreateAttribute("RequestNum");
        at.Value = requestNum;
        xp.Attributes.Append(at);
        at = xResponse.CreateAttribute("PageKey");
        at.Value = pageKey;
        Params.Request["PageKey"] = pageKey;
        xp.Attributes.Append(at);
        at = xResponse.CreateAttribute("Source");
        at.Value = "SPP";
        xp.Attributes.Append(at);

        dataRef = new DataRef(xSMPage.SelectSingleNode("DataSource"));

        if (xSMPage != null && xSMPage.Attributes["DisableBackScroll"] != null)
        {
            at = xResponse.CreateAttribute("DisableBackScroll");
            at.Value = xSMPage.Attributes["DisableBackScroll"].Value;
            xp.Attributes.Append(at);
        }

        if (xSMPage != null && xSMPage.Attributes["alter"] != null)
        {
            at = xResponse.CreateAttribute("alter");
            at.Value = xSMPage.Attributes["alter"].Value;
            xp.Attributes.Append(at);
        }

        if (!userIDKey.IsNullOrEmpty() && HttpContext.Current.Session[userIDKey] != null)
        {
            at = xResponse.CreateAttribute("UserID");
            at.Value = (string)HttpContext.Current.Session[userIDKey];
            xp.Attributes.Append(at);
        }

        xResponse.AppendChild(xp);
        if (xT != null)
            AppendTemplate(xT);


        AddParamAttribute(xp, "Title");
        AddParamAttribute(xp, "Description");
        AddParamAttribute(xp, "Canonical");

        XmlNode xDataSource = xResponse.CreateElement("DataSource");
        xDataSource.InnerXml = dataRef.GetXmlString();
        xp.AppendChild(xDataSource);

        string refs = string.Empty;

        XmlNodeList xnl = xSMPage.SelectNodes("Resources/Resource/File");
        for (int j = 0; j < xnl.Count; j++)
        {
            string src = xnl[j].GetAttribute("Src");
            string hash = xnl[j].GetAttribute("Hash");
            Boolean keepName = xnl[j].GetAttribute("keepName") == "true";

            string name = System.IO.Path.GetFileNameWithoutExtension(src);
            string type = System.IO.Path.GetExtension(src);

            StringBuilder builder = new StringBuilder();

            if (keepName)
            {
                builder.Append(src);
            }
            else
            {
                builder.Append("mini/");
                builder.Append(name);
                builder.Append("-");
                builder.Append(hash);
                builder.Append(type);
            }  

            refs += "<Ref src=\"" + builder.ToString() + "\" type=\"" + type.Substring(1) + "\"" + " hash=\"" + hash + "\" />";
            //refs += "<Ref src=\"" + builder.ToString() + "\" type=\"" + type.Substring(1) + "\" />";
        }

        // xSMPage.InnerXml += refs;

        XmlNode xResources = xResponse.CreateElement("Resources");
        xResources.InnerXml = refs;
        xp.AppendChild(xResources);

        TimeSpan ts = DateTime.Now - start;
        xp.SetAttribute("TotalProcessTime", ts.TotalMilliseconds.ToString());
    }

    private void AddParamAttribute(XmlNode xp, string atName)
    {
        if (Params.Request[atName].IsNullOrEmpty())
            return;
        XmlAttribute attr = xResponse.CreateAttribute(atName);
        attr.Value = Params.Request[atName];
        xp.Attributes.Append(attr);
    }


    private void AppendTemplate(XmlNode xTemplate)
    {
        DateTime start = DateTime.Now;
        string tID = xTemplate.GetAttribute("TemplateID");
        string tContainer = xTemplate.GetAttribute("Container");
        if (xTemplate.GetAttribute("Ondemand") != "True" || HttpContext.Current.Request.QueryString["Ondemand"] == "True")
        {
            XmlNode t = xResponse.CreateElement("Template");
            t.SetAttribute("TemplateID", tID);
            t.SetAttribute("Container", tContainer);

            string runAt = xTemplate.GetAttribute("RunAt");
            var isHtml = xTemplate.GetAttribute("IsHtml") == "true";

            string xslPath = xTemplate.GetAttribute("Xsl");
            if (!xslPath.IsNullOrEmpty())
            {
                XmlNode t1 = xTemplates.SelectSingleNode("Templates/Template[@xsl='" + xslPath + "']");

                string xsl = "<Xsl ID=\"" + t1.Attributes["ID"].Value + "\" />";

                t.InnerXml += xsl;
            }


            // Get Xml 
            var xData = xTemplate.SelectSingleNode("Data");
            if (xData == null)
            {
                string dataRefKey = xTemplate.SelectSingleNode("DataRef").GetAttribute("Key");

                if (!dataRefKey.IsNullOrEmpty())
                {
                    dataRef.LoadDataSourceXml(dataRefKey);
                    t.InnerXml += "<DataRef Key=\"" + dataRefKey + "\" />";
                }
            }
            else
            {
                string cc = xTemplate.GetAttribute("ClientCache");
                XmlResponder responder = XmlResponder.GetInstance(xData);
                string dataClientCacheKey = responder.GetCacheKey();
                if (cc.IsNullOrEmpty() || dataClientCacheKey.IsNullOrEmpty() || !IsDataClientCached(dataClientCacheKey))
                {
                    // get xml 
                    string xmlStr = responder.GetXml();
                    xmlStr = xmlStr.Replace("cdn1.r2net.com", "ion.r2net.com").Replace("cdn2.r2net.com", "ion.r2net.com").Replace("cdn3.r2net.com", "ion.r2net.com").Replace("cdn4.r2net.com", "ion.r2net.com");
                    dataClientCacheKey = responder.GetCacheKey();

                    if (!cc.IsNullOrEmpty())
                        cc = " ClientCache=\"" + cc + "\" ";
                    if (!dataClientCacheKey.IsNullOrEmpty())
                        dataClientCacheKey = " DataClientCacheKey=\"" + dataClientCacheKey + "\" ";

                    if (!IsDataClientCached(dataClientCacheKey))
                    {

                        if (xmlStr.IsNullOrEmpty())
                            throw new Exception("Empty xml from responder, container:" + tContainer + ", templateID:" + tID);

                        //replace(/\<-\!-\[/g, '<![').replace(/]-]->/g, ']]>')
                        t.InnerXml += "<Xml " + cc + dataClientCacheKey + " ><![CDATA[" + xmlStr.Replace("<![", "<-!-[").Replace("]]>", "]-]->") + "]]></Xml>";

                    }

                    else if (isHtml)
                    {
                        string html = xmlStr;
                        t.InnerXml += "<Xml " + cc + dataClientCacheKey + " ><![CDATA[" + html + "]]></Xml>";

                    }


                    else
                    {
                        t.InnerXml += "<Xml " + cc + dataClientCacheKey + " />";
                    }
                }
                else
                {
                    if (!cc.IsNullOrEmpty())
                        cc = " ClientCache=\"" + cc + "\" ";
                    if (!dataClientCacheKey.IsNullOrEmpty())
                        dataClientCacheKey = " DataClientCacheKey=\"" + dataClientCacheKey + "\" ";
                    t.InnerXml += "<Xml " + cc + dataClientCacheKey + " />";
                }
            }
            if (!xTemplate.GetAttribute("Js").IsNullOrEmpty())
            {
                string f = HttpContext.Current.Server.MapPath("~/" + xTemplate.GetAttribute("Js"));
                string js = string.Empty;
                if (ConfigurationManager.AppSettings["Env"] != "Dev")
                {
                    js = (string)HttpContext.Current.Cache["jsFile_" + f];

                }
                if (js.IsNullOrEmpty())
                {
                    js = System.IO.File.ReadAllText(f);
                    HttpContext.Current.Cache.Insert("jsFile_" + f, js, null, DateTime.Now.AddHours(12), TimeSpan.Zero);
                }
                t.InnerXml += "<Js><![CDATA[" + js + "]]></Js>";
            }
            if (!xTemplate.GetAttribute("JsFn").IsNullOrEmpty())
            {
                t.InnerXml += "<Js><![CDATA[" + xTemplate.GetAttribute("JsFn") + "]]></Js>";
            }

            TimeSpan ts = DateTime.Now - start;
            t.SetAttribute("ProcessTime", ts.TotalMilliseconds.ToString());
            xResponse.SelectSingleNode("Page").AppendChild(t);
            XmlNodeList xnlT = xTemplate.SelectNodes("Template");
            for (int i = 0; i < xnlT.Count; i++)
            {
                AppendTemplate(xnlT[i]);
            }
        }
    }



    private bool IsDataClientCached(string clientCacheKey)
    {
        return dataClientCash.Contains("," + clientCacheKey + ",");
    }

    public string GetResponse()
    {
        string response = xResponse.OuterXml;
        if ((HttpContext.Current.Request.IsSecureConnection
            || HttpContext.Current.Request.Headers["LB-Offload"] == "True"))
            response = response.Replace("src=\"http:", "src=\"https:");

        response = response.Replace("#!/", "");
        HtmlCacheManager.SetAjax(response);

        return response;
    }

    public XmlDocument GetPageXml()
    {
        XmlDocument xDoc = new XmlDocument(GetResponse());
        return xDoc;
    }



    #region IHttpHandler Members

    public bool IsReusable
    {
        get { return false; }
    }

    public void ProcessRequest(HttpContext context)
    {
        Process(pageUrl, container, requestNum);

        context.Response.ContentType = "text/xml";
        context.Response.Write(GetResponse());
        context.Response.Flush();
    }

    #endregion
}