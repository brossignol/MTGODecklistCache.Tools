﻿using HtmlAgilityPack;
using MTGODecklistCache.Updater.Model;
using MTGODecklistCache.Updater.MtgMelee.Client.Model;
using MTGODecklistCache.Updater.Tools;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace MTGODecklistCache.Updater.MtgMelee.Client
{
    public class MtgMeleeClient
    {
        public MtgMeleeTournamentInfo GetTournament(Uri uri)
        {
            int tournamentId = int.Parse(uri.AbsolutePath.Trim('/').Split('/').Last());

            string pageContent = GetClient().DownloadString(uri);

            HtmlDocument doc = new HtmlDocument();
            doc.LoadHtml(pageContent);

            var roundNodes = doc.DocumentNode.SelectNodes("//button[@class='btn btn-gray round-selector' and @data-is-completed='True']");
            var roundIds = roundNodes?.Select(r => r.Attributes["data-id"].Value).Select(r => int.Parse(r)).ToArray();

            var tournamentInfoText = doc.DocumentNode.SelectSingleNode("//p[@id='tournament-headline-registration']").InnerText;
            var tournamentFormats = tournamentInfoText.Split("|").Skip(2).First().Replace("Format: ", "").Split(",").Select(f => f.Trim()).ToArray();

            return new MtgMeleeTournamentInfo()
            {
                ID = tournamentId,
                RoundIDs = roundIds,
                Formats = tournamentFormats
            };
        }

        public MtgMeleePlayerInfo[] GetPlayers(MtgMeleeTournamentInfo tournament, int? maxPlayers = null)
        {
            List<MtgMeleePlayerInfo> result = new List<MtgMeleePlayerInfo>();

            if (tournament.RoundIDs == null || tournament.RoundIDs.Length == 0) return null;

            var roundIds = tournament.RoundIDs;
            var roundId = roundIds.Last();

            bool hasData;
            int offset = 0;
            do
            {
                hasData = false;

                string roundParameters = MtgMeleeConstants.RoundPageParameters
                    .Replace("{start}", offset.ToString())
                    .Replace("{roundId}", roundId.ToString());
                string roundUrl = MtgMeleeConstants.RoundPage;

                string json = Encoding.UTF8.GetString(GetClient().UploadValues(roundUrl, "POST", HttpUtility.ParseQueryString(roundParameters)));
                var round = JsonConvert.DeserializeObject<dynamic>(json);

                if (round.data.Count == 0 && offset == 0)
                {
                    if (roundIds.Length > 1)
                    {
                        roundIds = roundIds.Take(roundIds.Length - 1).ToArray();
                        roundId = roundIds.Last();

                        hasData = true;
                        continue;
                    }
                    else
                    {
                        break;
                    }
                }

                foreach (var entry in round.data)
                {
                    hasData = true;
                    string playerName = entry.Team.Players[0].DisplayName;
                    if (playerName == null) continue;

                    playerName = NormalizeSpaces(playerName);
                    string userName = entry.Team.Players[0].Username;

                    int playerPoints = entry.Points;
                    double omwp = entry.OpponentMatchWinPercentage;
                    double gwp = entry.TeamGameWinPercentage;
                    double ogwp = entry.OpponentGameWinPercentage;
                    int playerPosition = entry.Rank;
                    int wins = entry.MatchWins;
                    int losses = entry.MatchLosses;
                    int draws = entry.MatchDraws;

                    Standing standing = new Standing()
                    {
                        Player = playerName,
                        Rank = playerPosition,
                        Points = playerPoints,
                        OMWP = omwp,
                        GWP = gwp,
                        OGWP = ogwp,
                        Wins = wins,
                        Losses = losses,
                        Draws = draws
                    };

                    List<MtgMeleePlayerDeck> playerDecks = new List<MtgMeleePlayerDeck>();
                    foreach (var decklist in entry.Decklists)
                    {
                        string deckListId = decklist.DecklistId;
                        if (string.IsNullOrWhiteSpace(deckListId)) continue;

                        string decklistFormat = decklist.Format;
                        playerDecks.Add(new MtgMeleePlayerDeck()
                        {
                            ID = deckListId,
                            Format = decklistFormat,
                            Uri = new Uri(MtgMeleeConstants.DeckPage.Replace("{deckId}", deckListId))
                        });
                    }

                    result.Add(new MtgMeleePlayerInfo()
                    {
                        UserName = userName,
                        PlayerName = playerName,
                        Result = $"{wins}-{losses}-{draws}",
                        Standing = standing,
                        Decks = playerDecks.Count > 0 ? playerDecks.ToArray() : null
                    });
                }

                offset += 25;
            } while (hasData && (!maxPlayers.HasValue || offset < maxPlayers.Value));

            return result.ToArray();
        }

        public MtgMeleeDeckInfo GetDeck(Uri uri, MtgMeleePlayerInfo[] players, bool skipRoundData = false)
        {
            string deckPageContent = GetClient().DownloadString(uri);

            HtmlDocument deckDoc = new HtmlDocument();
            deckDoc.LoadHtml(deckPageContent);

            HtmlNode decklist = deckDoc.DocumentNode.SelectSingleNode("//pre[@id='decklist-text' and @class='d-none']");
            var cardList = decklist.InnerHtml.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).ToArray();

            string playerUrl = deckDoc.DocumentNode.SelectSingleNode("//span[@class='decklist-card-title-author']/a")?.Attributes["href"]?.Value;
            string playerRaw = deckDoc.DocumentNode.SelectSingleNode("//span[@class='decklist-card-title-author']/a")?.InnerHtml;

            var playerName = GetPlayerName(playerRaw, playerUrl, players);

            HtmlNodeCollection detailsNodes = deckDoc.DocumentNode.SelectNodes("//div[contains(@class, 'decklist-details-row')]//span[contains(@class, 'text-nowrap')]");
            var format = detailsNodes[2].InnerText.Trim();

            List<DeckItem> mainBoard = new List<DeckItem>();
            List<DeckItem> sideBoard = new List<DeckItem>();
            bool insideSideboard = false;
            bool insideCompanion = false;

            foreach (var card in cardList)
            {
                if (card == "Deck" || card == "Companion" || card == "Sideboard" || card == "" || card == "MainDeck")
                {
                    if (card == "Companion")
                    {
                        insideCompanion = true;
                        insideSideboard = false;
                    }
                    else
                    {
                        if (card == "Sideboard")
                        {
                            insideCompanion = false;
                            insideSideboard = true;
                        }
                        else
                        {
                            insideCompanion = false;
                            insideSideboard = false;
                        }
                    }
                }
                else
                {
                    if (insideCompanion) continue; // Companion is listed in its own section *and* in the sideboard as well

                    int splitPosition = card.IndexOf(" ");
                    int count = Convert.ToInt32(new String(card.Take(splitPosition).ToArray()));
                    string name = new String(card.Skip(splitPosition + 1).ToArray());
                    name = CardNameNormalizer.Normalize(name);

                    if (insideSideboard) sideBoard.Add(new DeckItem() { CardName = name, Count = count });
                    else mainBoard.Add(new DeckItem() { CardName = name, Count = count });
                }
            }

            List<MtgMeleeRoundInfo> rounds = new List<MtgMeleeRoundInfo>();
            if (!skipRoundData)
            {
                var roundsDiv = deckDoc.DocumentNode.SelectSingleNode("//div[@id='tournament-path-grid-item']");
                if (roundsDiv != null)
                {
                    foreach (var roundDiv in roundsDiv.SelectNodes("div/div/div/table/tbody/tr"))
                    {
                        var round = GetRound(roundDiv, playerName, players);
                        if (round != null) rounds.Add(round);
                    }
                }
            }

            return new MtgMeleeDeckInfo()
            {
                DeckUri = uri,
                Format = format,
                Mainboard = mainBoard.ToArray(),
                Sideboard = sideBoard.ToArray(),
                Rounds = rounds.Count() == 0 ? null : rounds.ToArray()
            };
        }

        private static MtgMeleeRoundInfo GetRound(HtmlNode roundNode, string playerName, MtgMeleePlayerInfo[] players)
        {
            var roundColumns = roundNode.SelectNodes("td");
            if (roundColumns.First().InnerText.Trim() == "No results found") return null;

            string roundName = roundColumns.First().InnerHtml;
            roundName = NormalizeSpaces(WebUtility.HtmlDecode(roundName));

            string roundOpponentUrl = roundColumns.Skip(1).First().SelectSingleNode("a")?.Attributes["href"]?.Value;
            string roundOpponentRaw = roundColumns.Skip(1).First().SelectSingleNode("a")?.InnerHtml;
            string roundOpponent = GetPlayerName(roundOpponentRaw, roundOpponentUrl, players);

            string roundResult = roundColumns.Skip(3).First().InnerHtml;
            roundResult = NormalizeSpaces(WebUtility.HtmlDecode(roundResult));

            RoundItem item = null;
            if (roundResult.StartsWith($"{playerName} won", StringComparison.InvariantCultureIgnoreCase))
            {
                // Victory
                item = new RoundItem()
                {
                    Player1 = playerName,
                    Player2 = roundOpponent,
                    Result = roundResult.Split(" ").Last()
                };
            }
            if (roundResult.StartsWith($"{roundOpponent} won", StringComparison.InvariantCultureIgnoreCase))
            {
                // Defeat
                item = new RoundItem()
                {
                    Player1 = roundOpponent,
                    Player2 = playerName,
                    Result = roundResult.Split(" ").Last()
                };
            }
            if (roundResult.EndsWith("Draw"))
            {
                // Draw
                if (String.Compare(playerName, roundOpponent) < 0)
                {
                    item = new RoundItem()
                    {
                        Player1 = playerName,
                        Player2 = roundOpponent,
                        Result = roundResult.Split(" ").First()
                    };
                }
                else
                {
                    item = new RoundItem()
                    {
                        Player1 = roundOpponent,
                        Player2 = playerName,
                        Result = roundResult.Split(" ").First()
                    };
                }
            }
            if (roundResult.EndsWith(" bye") || roundResult.Contains("was awarded a bye"))
            {
                // Victory by bye
                item = new RoundItem()
                {
                    Player1 = playerName,
                    Player2 = "-",
                    Result = "2-0-0"
                };
            }
            if (roundResult.StartsWith("won "))
            {
                // Victory by unknown opponent
                item = new RoundItem()
                {
                    Player1 = "-",
                    Player2 = playerName,
                    Result = "2-0-0"
                };
            }
            if (roundResult.StartsWith($"{playerName} forfeited", StringComparison.InvariantCultureIgnoreCase))
            {
                // Victory by concession
                item = new RoundItem()
                {
                    Player1 = playerName,
                    Player2 = roundOpponent,
                    Result = "0-2-0"
                };
            }
            if (roundResult.StartsWith($"Not reported") || roundResult.EndsWith($"[FORMAT EXCEPTION]"))
            {
                // Missing or broken data
                if (String.Compare(playerName, roundOpponent) < 0)
                {
                    item = new RoundItem()
                    {
                        Player1 = playerName,
                        Player2 = roundOpponent,
                        Result = "0-0-0"
                    };
                }
                else
                {
                    item = new RoundItem()
                    {
                        Player1 = roundOpponent,
                        Player2 = playerName,
                        Result = "0-0-0"
                    };
                }
            }
            // Assuming this means a draw?
            if (roundResult.Contains($"{playerName} forfeited") && roundResult.Contains($"{roundOpponent} forfeited"))
            {
                item = new RoundItem()
                {
                    Player1 = playerName,
                    Player2 = roundOpponent,
                    Result = "0-0-1"
                };
            }


            if (item == null) throw new FormatException($"Cannot parse round data for player {playerName} and opponent {roundOpponent}");

            // Sometimes the result is displayed incorrectly without draws
            if (item.Result.Split("-").Length == 2) item.Result += "-0";

            return new MtgMeleeRoundInfo
            {
                RoundName = roundName,
                Match = item
            };
        }

        public MtgMeleeListTournamentInfo[] GetTournaments(DateTime startDate, DateTime endDate)
        {
            int offset = 0;
            int limit = -1;

            List<MtgMeleeListTournamentInfo> result = new List<MtgMeleeListTournamentInfo>();
            do
            {
                string tournamentListParameters = MtgMeleeConstants.TournamentListParameters
                    .Replace("{offset}", offset.ToString())
                    .Replace("{startDate}", startDate.ToString("yyyy-MM-dd"))
                    .Replace("{endDate}", endDate.ToString("yyyy-MM-dd"));
                string tournamentListUrl = MtgMeleeConstants.TournamentListPage;

                string json = Encoding.UTF8.GetString(GetClient().UploadValues(tournamentListUrl, "POST", HttpUtility.ParseQueryString(tournamentListParameters)));
                var list = JsonConvert.DeserializeObject<dynamic>(json);

                limit = list.recordsTotal;

                foreach (var item in list.data)
                {
                    offset++;

                    int id = item.ID;
                    DateTime date = item.StartDate;
                    string name = item.Name;
                    string organization = item.OrganizationName;
                    string format = item.FormatDescription;
                    string status = item.StatusDescription;
                    int decklists = item.Decklists;

                    name = NormalizeSpaces(name);
                    organization = NormalizeSpaces(organization);
                    format = NormalizeSpaces(format);
                    status = NormalizeSpaces(status);

                    if (status == "Ended" || (DateTime.Now - date).TotalDays > MtgMeleeConstants.MaxDaysBeforeTournamentMarkedAsEnded)
                    {
                        result.Add(new MtgMeleeListTournamentInfo()
                        {
                            ID = id,
                            Date = new DateTime(date.Year, date.Month, date.Day, date.Hour, date.Minute, date.Second, DateTimeKind.Utc),
                            Name = name,
                            Organizer = organization,
                            Uri = new Uri(MtgMeleeConstants.TournamentPage.Replace("{tournamentId}", id.ToString())),
                            Decklists = decklists
                        });
                    }
                }
            } while (offset < limit);

            return result.ToArray();
        }

        private static string GetPlayerName(string playerNameRaw, string profileUrl, MtgMeleePlayerInfo[] players)
        {
            string playerId = profileUrl?.Split("/").Last();
            if (playerId != null)
            {
                var playerInfo = players?.FirstOrDefault(p => p.UserName == playerId);
                if (playerInfo != null)
                {
                    return playerInfo.PlayerName;
                }
                else
                {
                    if (playerNameRaw != null)
                    {
                        return NormalizeSpaces(WebUtility.HtmlDecode(playerNameRaw));
                    }
                }
            }
            return "-";
        }

        private static WebClient GetClient()
        {
            var client = new WebClient();
            client.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:130.0) Gecko/20100101 Firefox/130.0");
            return client;
        }

        private static string NormalizeSpaces(string data)
        {
            return Regex.Replace(data, "\\s+", " ").Trim();
        }
    }
}
