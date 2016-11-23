using System.IO;
using System.Xml.Serialization;
using HtmlAgilityPack;
using RestSharp.Serializers;
using System.Net;

namespace NationalParkScraper
{
    public class CookieWebClient
    {
        // The cookies will be here.
        private CookieContainer _cookies = new CookieContainer();

        // In case you need to clear the cookies
        public void ClearCookies()
        {
            _cookies = new CookieContainer();
        }

        public HtmlDocument GetPage(string url, out string html)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";

            // This is the important part.
            request.CookieContainer = _cookies;

            HttpWebResponse response = (HttpWebResponse)request.GetResponse();
            var stream = response.GetResponseStream();

            // When you get the response from the website, the cookies will be stored
            // automatically in "_cookies".
            using (var reader = new StreamReader(stream))
            {
                html = reader.ReadToEnd();
                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                return doc;
            }
        }
    }

    public static class XML
    {
        public static T Deserialize<T>(string input) where T : class
        {
            var ser = new System.Xml.Serialization.XmlSerializer(typeof(T));

            using (StringReader sr = new StringReader(input))
                return (T)ser.Deserialize(sr);
        }

        public static string Serialize<T>(object input) where T : class
        {
            var ser = new System.Xml.Serialization.XmlSerializer(typeof(T));

            using (var textWriter = new StringWriter())
            {
                ser.Serialize(textWriter, input);
                return textWriter.ToString();
            }
        }
    }
}
