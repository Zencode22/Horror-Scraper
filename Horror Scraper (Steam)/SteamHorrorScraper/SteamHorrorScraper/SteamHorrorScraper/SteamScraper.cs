using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SteamHorrorScraper
{
    public class SteamScraper
    {
        private readonly HttpClient client;

        public SteamScraper()
        {
            client = new HttpClient();
        }

        public async Task GetGameDetails(int appId)
        {
            string url =
                $"https://store.steampowered.com/api/appdetails?appids={appId}";

            try
            {
                string response = await client.GetStringAsync(url);

                using JsonDocument json = JsonDocument.Parse(response);

                JsonElement root = json.RootElement;
                JsonElement app = root.GetProperty(appId.ToString());

                if (!app.GetProperty("success").GetBoolean())
                {
                    Console.WriteLine($"Could not find App ID {appId}");
                    return;
                }

                JsonElement data = app.GetProperty("data");

                string name =
                    data.GetProperty("name").GetString() ?? "Unknown";

                Console.WriteLine($"App ID: {appId}");
                Console.WriteLine($"Name: {name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting {appId}: {ex.Message}");
            }
        }

        public async Task<List<int>> FindHorrorGames()
        {
            List<int> appIds = new List<int>();

            string url =
                "https://store.steampowered.com/search/results/" +
                "?query&start=0&count=50&tags=1667&infinite=1";

            try
            {
                string response = await client.GetStringAsync(url);

                using JsonDocument json = JsonDocument.Parse(response);

                JsonElement root = json.RootElement;

                string html = root.GetProperty("results_html").GetString() ?? "";

                Console.WriteLine($"Received {html.Length} characters from Steam.");

                HtmlDocument document = new HtmlDocument();

                document.LoadHtml(html);

                HtmlNodeCollection gameNodes = document.DocumentNode.SelectNodes("//a[@data-ds-appid]");

                if (gameNodes == null)
                {
                    Console.WriteLine("No games found.");
                    return appIds;
                }

                foreach (HtmlNode gameNode in gameNodes)
                {
                    string appIdText =
                        gameNode.GetAttributeValue("data-ds-appid", "");

                    if (int.TryParse(appIdText, out int appId))
                    {
                        appIds.Add(appId);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error searching Steam: {ex.Message}");
            }

            return appIds;
        }
    }
}
