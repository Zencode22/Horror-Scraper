namespace SteamHorrorScraper
{
    using System;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading.Tasks;
    using System.Collections.Generic;

    class Program
    {
        static async Task Main()
        {
            SteamScraper scraper = new SteamScraper();

            List<int> horrorGameIds = await scraper.FindHorrorGames();

            Console.WriteLine($"Found {horrorGameIds.Count} horror games.");

            foreach (int appId in horrorGameIds)
            {
                await scraper.GetGameDetails(appId);

                Console.WriteLine("---------------------");

                // Don't hammer Steam with requests.
                await Task.Delay(1000);
            }
        }
    }
}
