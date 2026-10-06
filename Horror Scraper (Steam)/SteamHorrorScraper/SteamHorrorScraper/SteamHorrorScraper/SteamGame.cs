using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SteamHorrorScraper
{
    public class SteamGame
    {
        public int AppId { get; set; }

        public string Name { get; set; } = "";

        public string ReleaseDate { get; set; } = "";

        public string Developer { get; set; } = "";

        public string Publisher { get; set; } = "";

        public string Genres { get; set; } = "";

        public double Price { get; set; }

        public int PositiveReviews { get; set; }

        public int NegativeReviews { get; set; }

        public int TotalReviews
        {
            get
            {
                return PositiveReviews + NegativeReviews;
            }
        }
    }
}
