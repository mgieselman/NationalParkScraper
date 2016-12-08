using System.IO;
using RestSharp.Extensions.MonoHttp;
using SendGrid;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using Twilio;

namespace NationalParkScraper
{

    public class SiteMatch
    {
        public DateTime Sent { get; set; } = DateTime.Now;
        public string Campground { get; set; }
        public string Title { get; set; }

        public string Identifier
        {
            get
            {
                return $"{Campground}-{Title}";
            }
        }
    }

    public class Notification
    {
        public string Email { get; set; }
        public string Sms { get; set; }
    }

    public class SearchCriteria
    {
        public string Name { get; set; }
        public int ParkId { get; set; }
        public string StartDate { get; set; }
        public int StayLength { get; set; }
        public int MaximumDays { get; set; }
        public int MaximumVehicleLength { get; set; }

        public List<Ignore> Ignores { get; set; }
        public List<string> Filters { get; set; }
    }

    public class Ignore
    {
        public string Site { get; set; }
    }

    [Serializable]
    public class NationalParkScraperSettings
    {
        public int IntervalSeconds { get; set; } = 1;
        public List<Notification> Notifications { get; set; }
        public List<SearchCriteria> SearchCriterias { get; set; }
    }

    class Program
    {
        private static readonly log4net.ILog _log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly CookieContainer _cookies = new CookieContainer();
        private static int _emailsSent = 0;

        static void Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.WriteLine("Expected 1 argument to be path to settings file.");
            }

            var settingsPath = args[0];
            if (!File.Exists(settingsPath))
            {
                Console.WriteLine($"The file {settingsPath} does not exist or cannot be accessed.");
            }

            var settings = XML.Deserialize<NationalParkScraperSettings>(File.ReadAllText(settingsPath));
            var siteMatches = new List<SiteMatch>();
           
            while (true)
            {
                string html = null;
                
                try
                {
                    _log.Debug($"Processing {settings.SearchCriterias.Count()} searches.");
                    foreach (var searchCriteria in settings.SearchCriterias)
                    {
                        var webClient = new CookieWebClient();
                        var maxIdx = 1000;
                        var startIdx = 0;

                        while (startIdx < maxIdx)
                        {
                            var requestUrl = $"http://www.reserveamerica.com/campsiteCalendar.do?page=calendar&contractCode=CA&parkId={searchCriteria.ParkId}&calarvdate={searchCriteria.StartDate}";
                            if (startIdx > 0)
                            {
                                requestUrl += $"&sitepage=true&startIdx={startIdx}";
                            }

                            var document = webClient.GetPage(requestUrl, out html);

                            var calendarNode = document.GetElementbyId("calendar");
                            if (calendarNode == null)
                            {
                                _log.Debug($"No calendar element for {requestUrl}");
                                break; 
                            }

                            if (maxIdx == 1000)
                            {
                                var resulttotal = document.DocumentNode
                                        .Descendants("span")
                                        .Where(d => d.Id.Contains("resulttotal"))
                                        .FirstOrDefault();
                                maxIdx = int.Parse(resulttotal.InnerText);
                            }

                            var campground = document.GetElementbyId("cgroundName").InnerText;

                            if (calendarNode != null)
                            {
                                var tableBody = calendarNode.Descendants("tbody").First();

                                var campsites = tableBody.Descendants("tr").ToList();
                                // Remove all <tr class="separator xxxx">
                                campsites.RemoveAll(c => c.Attributes["class"] != null);

                                foreach (var campsite in campsites)
                                {
                                    var div = campsite.Descendants("div").FirstOrDefault();

                                    if (div == null)
                                    {
                                        continue;
                                    }
                                    
                                    var hyperlink = div.Descendants("a").First();

                                    var img = hyperlink.Descendants("img").FirstOrDefault();
                                    if (img == null)
                                    {
                                        continue;
                                    }
                                    var title = img.Attributes["title"].Value;

                                    if (searchCriteria.Filters == null ||
                                        searchCriteria.Filters.Count() == 0 ||
                                        searchCriteria.Filters.Any(f => title.Contains(f)))
                                    {
                                        var contiguous = 0;
                                        var maximumContiguous = 0;
                                        var daysCount = 0;
                                        string reservationUrl = null;

                                        // Now we have a candidate, lets see how many days in a row are available.
                                        foreach (var status in campsite.Descendants("td"))
                                        {
                                            if (daysCount > searchCriteria.MaximumDays)
                                            {
                                                break;
                                            }

                                            if (status.Attributes["class"] != null
                                                && status.Attributes["class"].Value.StartsWith("status"))
                                            {
                                                if (status.InnerText == "A")
                                                {
                                                    contiguous++;
                                                    if (contiguous > maximumContiguous)
                                                    {
                                                        maximumContiguous = contiguous;
                                                    }
                                                    if (string.IsNullOrEmpty(reservationUrl))
                                                    {
                                                        reservationUrl = status.Descendants("a").First().Attributes["href"].Value;
                                                    }
                                                }
                                                else
                                                {
                                                    contiguous = 0;
                                                }
                                            }

                                            daysCount++;
                                        }

                                        if (maximumContiguous >= searchCriteria.StayLength)
                                        {
                                            if (!siteMatches.Any(n => n.Identifier == $"{campground}-{title}")
                                                && (searchCriteria.Ignores == null || searchCriteria.Ignores.Count() == 0 || !searchCriteria.Ignores.Any(i => title.StartsWith(i.Site))))
                                            {
                                                // Now check and see if equipment fits
                                                var siteListLabel = campsite.Descendants("div")
                                                   .Where(d => d.Attributes.Contains("class") && d.Attributes["class"].Value.Contains("siteListLabel"))
                                                   .First();

                                                var siteId = HttpUtility.ParseQueryString(new Uri($"http://www.reserveamerica.com{reservationUrl}").Query).Get("siteId");
                                                var siteDetailsUrl = $"http://www.reserveamerica.com/camping/San_Elijo_Sb/r/campsiteDetails.do?siteId={siteId}&contractCode=CA&parkId={searchCriteria.ParkId}";
                                                string siteDetailsHtml = null;
                                                var siteDetailsDocument = webClient.GetPage(siteDetailsUrl, out siteDetailsHtml);

                                                //var match = Regex.Match(siteDetailsHtml, @"(Max Vehicle Length:&nbsp;<b>)(\d+)");
                                                //var maxVehicleLength = match.Groups[2].Value;
                                               
                                                siteMatches.Add(new SiteMatch() { Campground = campground, Title = title });

                                                // Don't send notification if length isn't long enough but add it to the list so we don't 
                                                // get the siteDetails again.
                                                //if (int.Parse(maxVehicleLength) >= searchCriteria.MaximumVehicleLength)
                                                //{
                                                var subject = $"{campground}-{title}.";
                                                var body = $"{campground}-{title}, {maximumContiguous} nights around {searchCriteria.StartDate} http://www.reserveamerica.com{reservationUrl}#";
                                                _log.Debug(body);

                                                foreach (var notification in settings.Notifications)
                                                {
                                                    if (!string.IsNullOrEmpty(notification.Sms))
                                                    {
                                                        SendSms(notification.Sms, body);
                                                    }
                                                    if (!string.IsNullOrEmpty(notification.Email))
                                                    {
                                                        SendEmail(notification.Email, subject, body);
                                                    }
                                                }
                                                //}
                                                //else
                                                //{
                                                //    _log.Debug($"Found {maximumContiguous} nights at {campground}-{title} but trailer length was {maxVehicleLength} < {searchCriteria.MaximumVehicleLength}.");
                                                //}
                                            }
                                        }
                                    }
                                }
                            }

                            startIdx += 25;
                        }
                    }
                    Thread.Sleep(settings.IntervalSeconds * 1000);
                }
                catch (Exception ex)
                {
                    _log.Error("Error occured:", ex);
                    if (html != null)
                    {
                        _log.Debug(html);
                    }
                }
            }
        }

        public static void SendEmail(string destination, string subject, string body)
        {
            _emailsSent++;

            if (_emailsSent > 50)
            {
                Environment.Exit(0);
            }
            var myMessage = new SendGridMessage();
            myMessage.AddTo(destination);
            myMessage.From = new System.Net.Mail.MailAddress(
                                "matt@gieselman.com", "National Park");
            myMessage.Subject = subject;
            myMessage.Text = body;
            myMessage.Html = body;

            var credentials = new NetworkCredential(
                       "azure_d6c1e6279cb5cee5bb98f22fa412a2c6@azure.com",
                       "Cobra305!"
                       );

            var transportWeb = new Web(credentials);

            transportWeb.DeliverAsync(myMessage);
        }

        public static void SendSms(string destination, string body)
        {
            var twilio = new TwilioRestClient("AC8e9c65518bdd0ace9950f740bececfb2", "bdca8ad3d638710ff454c92daeb821f3");
            var msg = twilio.SendMessage("+17606213549", destination, body);
        }
    }

  
}
