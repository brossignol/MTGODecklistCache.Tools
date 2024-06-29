﻿using MTGODecklistCache.Updater.Topdeck.Client.Constants;
using Newtonsoft.Json;

namespace MTGODecklistCache.Updater.Topdeck.Client.Model
{
    public class TopdeckTournament : NormalizableObject
    {
        [JsonProperty("TID")]
        public string? TID { get; set; }
        [JsonProperty("tournamentName")]
        public string? Name { get; set; }
        [JsonProperty("startDate")]
        public long? StartDate { get; set; }

        public void Normalize()
        {
        }
    }
}
