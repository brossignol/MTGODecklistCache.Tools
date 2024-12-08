﻿using MTGODecklistCache.Updater.Model;
using System;
using System.Collections.Generic;
using System.Text;

namespace MTGODecklistCache.Updater.MtgMelee.Client.Model
{
    public class MtgMeleeTournamentInfo
    {
        public int? ID { get; set; }
        public string[]? Formats { get; set; }
        public int[]? RoundIDs { get; set; }
    }
}
