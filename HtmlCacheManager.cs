using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Xml;

namespace JSite
{
    /// <summary>
    /// New layer of cache. Active when Akamai doesn't have the page html in cache
    /// so their server will take the page from our cache instead of generate it in real time.
    /// 27th October 2016
    /// </summary>


    public class HtmlCacheManager
    {
        public static void SetHTML(string html)
        {
            Set(html, "Html");
        }
        public static void SetAjax(string html)
        {
            Set(html, "Ajax");
        }
        private static void Set(string html, string keyPrefix)
        {
            if (!Params.Request["HtmlCacheMin"].IsNullOrEmpty() )
            {
                try
                {
                    int cacheMin = Convert.ToInt32(Params.Request["HtmlCacheMin"]);
                    HttpRequest request = HttpContext.Current.Request;
                    HttpResponse response = HttpContext.Current.Response;
                    if (response.StatusCode != 200 || cacheMin == 0)
                        return;
                    if (response.Cookies.Count > 0)
                        return;

                    XmlDocument xDoc = new XmlDocument("<Response><Headers/><Cookies/><Html/><Ajax/></Response>");

                    if (keyPrefix.Equals("Html"))
                    {
                        xDoc.SelectSingleNode("Response/Html").InnerXml = "<![CDATA[ " + html + " ]]>";
                    }
                    else
                    {
                        xDoc.SelectSingleNode("Response/Ajax").InnerXml = html;
                    }

                    foreach (string key in response.Headers.AllKeys)
                    {
                        if (key.Contains("Set-Cookie") || key.Contains("Server"))
                            continue;
                        XmlNode xn = xDoc.CreateElement("Header");
                        xn.SetAttribute("key", key);
                        xn.SetAttribute("value", response.Headers[key]);
                        xDoc.SelectSingleNode("Response/Headers").AppendChild(xn);
                    }
                    xDoc.SelectSingleNode("Response/Headers").SetAttribute("CacheControl",response.CacheControl);
                  
                    foreach (string key in response.Cookies.AllKeys)
                    {
                        if (key.Contains("ASP.NET_SessionId")
                            || key.Contains("R2NetUsername")
                            || key.Contains("jaDisplayName")
                            || key.Contains("jaUsrId")
                            || key.Contains("HashPassword"))
                            continue;
                        XmlNode xn = xDoc.CreateElement("Cookie");
                        xn.SetAttribute("key", key);
                        xn.SetAttribute("value", response.Cookies[key].Value);
                        if (response.Cookies[key].Expires > DateTime.MinValue)
                             xn.SetAttribute("expires", response.Cookies[key].Expires.ToString());

                        xDoc.SelectSingleNode("Response/Cookies").AppendChild(xn);
                    }

                    SiteCache sc = SiteCache.GetInstance();

                    string cacheKey = GetKey(keyPrefix);
                    sc.Insert(cacheKey, xDoc.OuterXml, DateTime.Now.AddMinutes(cacheMin), TimeSpan.Zero);
                }
                catch (Exception ex) { }

            }
        }

        public static string GetResponseAjaxXML()
        {
            string redisXML = GetCachedData("Ajax");
            if (redisXML.IsNullOrEmpty())
                return string.Empty;

            return ExtractResponse(redisXML, "Ajax");
        }

        public static bool RespondHtml()
        {
            return Respond("Html");
        }
        public static bool RespondAjax()
        {
            return Respond("Ajax");
        }
        private static bool Respond(string keyPrefix)
        {
            if (HttpContext.Current.Request.Cookies["admin_mode"] != null)
                return false;
            string redisXML = GetCachedData(keyPrefix);
            if (redisXML.IsNullOrEmpty())
                return false;

            HttpResponse response = HttpContext.Current.Response;
            response.Clear();
            if (keyPrefix == "Ajax")
                response.ContentType = "text/xml";

            response.Write(ExtractResponse(redisXML, keyPrefix));
            response.End();
            return true;
        }

        private static string ExtractResponse(string redisXML, string keyPrefix)
        {
            XmlDocument xDoc = new XmlDocument(redisXML);

            HttpResponse response = HttpContext.Current.Response;

            response.CacheControl =  xDoc.SelectSingleNode("Response/Headers").GetAttribute("CacheControl");

            XmlNodeList xnl = xDoc.SelectNodes("Response/Headers/Header");
            for (int i = 0; i < xnl.Count; i++)
            {
                response.Headers.Add(xnl[i].GetAttribute("key"), xnl[i].GetAttribute("value"));
            }

            xnl = xDoc.SelectNodes("Response/Cookies/Cookie");
            for (int i = 0; i < xnl.Count; i++)
            {
                HttpCookie cookie = new HttpCookie(xnl[i].GetAttribute("key"), xnl[i].GetAttribute("value"));
                string expires = xnl[i].GetAttribute("expires");
                if (!expires.IsNullOrEmpty())
                    cookie.Expires = DateTime.Parse(expires);

                response.Cookies.Add(cookie);
            }

            if (keyPrefix.Equals("Html"))
            {
                return xDoc.SelectSingleNode("Response/Html").FirstChild.InnerText;
            }
            else
            {
                XmlNode xPage = xDoc.SelectSingleNode("Response/Ajax/Page");
                xPage.Attributes["RequestNum"].Value = HttpContext.Current.Request.QueryString["RequestNum"];
                xPage.Attributes["Source"].Value = "Cache";

                return xDoc.SelectSingleNode("Response/Ajax").InnerXml;
            }
        }

        private static string GetCachedData(string keyPrefix)
        {
            SiteCache sc = SiteCache.GetInstance();
            string cacheKey = GetKey(keyPrefix);

            object xml = sc.Get(cacheKey);
            if (xml == null)
                return string.Empty;

            string xmlString;
            try
            {
                xmlString = xml.ToString();
                return xmlString;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetKey(string keyPrefix)
        {
            //https://www.jamesallen.com/JSite/Core/jx.ashx?PageUrl=engagement-rings/channel-set/?MaxPrice=4393&Container=%23WidePane&RequestNum=6
            HttpRequest request = HttpContext.Current.Request;

            bool isCookieable = HttpContext.Current.Request.Cookies["isCookieable"] != null && HttpContext.Current.Request.Cookies["isCookieable"].Value == "true";
            string device = request.Headers["X-Akamai-Device-Characteristics"];
            if (device.IsNullOrEmpty())
                device = request.UserAgent;

            Currency currency = new Currency();
            string url = request.Url.Query.ToString();

            if (!request.QueryString["PageUrl"].IsNullOrEmpty())
            {
                url = url.Replace("PageUrl", "InternalUrl").Replace("%3F", "&");
            }
            else if (request.QueryString["InternalUrl"].IsNullOrEmpty())
            {
                url = "?InternalUrl=" + request.Url.LocalPath + url.Replace("?", "&").Replace("%3F", "&");
            }


            url = removeDummyKeys(url);
            var protocol = "http-";
            if ((request.IsSecureConnection || request.Headers["LB-Offload"] == "True"))
                protocol = "https";
            string key = protocol +  keyPrefix + ":MiniVer-" + VersionManager.Instance + ":" + url + ":IsCookieable:" + isCookieable.ToString() + ":Device:" +
                device + ":Currency:" + currency.Code;

            return key;
        }

        public static string removeDummyKeys(string url)
        {
            string newUrl = HttpContext.Current.Server.UrlDecode(url);
            string[] dummyKeys = "a_aid,kmi,km_source,km_medium,km_term,km_campaign,km_keyword,km_account,km_adid,km_adgroup,gclid,utm_source,utm_medium,utm_content,utm_campaign,utm_term,pp,chan,RequestNum".Split(",".ToCharArray());
          
            foreach (string key in dummyKeys)
            {
                newUrl = newUrl.Replace("&" + key + "=" + HttpContext.Current.Request.QueryString[key], "");
            }


            return newUrl;
        }
    }
}