using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace JitHub.Services.GitHub.Contributions
{
    public class ContributionService
    {
        private const string _baseUrl = "https://github.com";
        private HttpClient _client = new HttpClient();
        
        public async Task<List<Contribution>> GetContribution(string username)
        {
            var url = $"{_baseUrl}/users/{username}/contributions";
            var html = await _client.GetStringAsync(url);
            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(html);
            var nodes = doc.DocumentNode.Descendants("svg")
                .Where(x => x.Attributes["class"] != null && x.Attributes["class"].Value == "js-calendar-graph-svg")
                .First()
                .Descendants("rect")
                .Where(x => x.Attributes["class"] != null && x.Attributes["class"].Value == "ContributionCalendar-day")
                .ToList();
            var contributions = new List<Contribution>();
            foreach (var node in nodes)
            {
                var contribution = new Contribution();
                contribution.Date = DateTime.Parse(node.Attributes["data-date"].Value);
                contribution.Level = node.Attributes["data-level"].Value;
                int.TryParse(node.InnerText.Split(' ')[0], out int level);
                contribution.Count = level;
                contributions.Add(contribution);
            }
            return contributions;
        }
    }

    public class Contribution
    {
        public DateTime Date { get; set; }
        public int Count { get; set; }
        public string Level { get; set; }
    }
}
