﻿using MathNet.Numerics.Distributions;
using Newtonsoft.Json;
using System.Data;
using SysBot.Base;

namespace SysBot.Pokemon.SV
{
    public class BanService : BanListSerialization
    {
        private static string jsonContent = "";
        private static List<LanguageData> Languages = new();
        private static List<BannedRaider> BannedList = new();
        private static readonly string languageResource = "SysBot.Pokemon.SV.BotRaid.BanList.Resources.languages.json";
        public static string GlobalBanReason = string.Empty;

        public static async Task<bool> IsRaiderBanned(string raiderName, string url, string connectionLabel, bool updateJson)
        {
            //Gets banned list
            var bannedRaiders = new List<BannedRaider>();
            try
            {
                if (updateJson)
                {
                    var client = new HttpClient();
                    jsonContent = await client.GetStringAsync(url).ConfigureAwait(false);
                    BannedList = JsonConvert.DeserializeObject<List<BannedRaider>>(jsonContent)!;
                    bannedRaiders = BannedList.Where(x => x.Enabled).ToList();
                }
            }
            catch (Exception e)
            {
                LogUtil.LogError($"Error retrieving ban list from PA: {e.Message}", connectionLabel);
                return false;
            }

            if (Languages.Count == 0)
            {
                var languages = languageResource.DeserializeResource<List<LanguageData>>(connectionLabel);
                if (languages is null)
                {
                    LogUtil.LogError("Failed to deserialize languages.", connectionLabel);
                    return false;
                }
                else Languages = languages;
            }

            var result = CheckRaider(raiderName, bannedRaiders, Languages, connectionLabel);
            if (result.IsBanned)            
                GlobalBanReason = $"\nBanned user {raiderName} found from global banlist." + "\nReason: " + result.BanReason + $"\nLog10p: {result.Log10p}";
            
            return (result.IsBanned);
        }

        private static int CalculateLevenshteinDistance(string normRaider, string normBanned)
        {
            var normRaiderLength = normRaider.Length;
            var normBannedLength = normBanned.Length;

            var matrix = new int[normRaiderLength + 1, normBannedLength + 1];

            // First calculation, if one entry is empty return full length
            if (normRaiderLength == 0)
                return normBannedLength;

            if (normBannedLength == 0)
                return normRaiderLength;

            // Initialization of matrix with row size normRaiderLength and columns size normBannedLength
            for (var i = 0; i <= normRaiderLength; matrix[i, 0] = i++) { }
            for (var j = 0; j <= normBannedLength; matrix[0, j] = j++) { }

            // Calculate rows and collumns distances
            for (var i = 1; i <= normRaiderLength; i++)
            {
                for (var j = 1; j <= normBannedLength; j++)
                {
                    var cost = (normBanned[j - 1] == normRaider[i - 1]) ? 0 : 1;

                    matrix[i, j] = Math.Min(
                        Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                        matrix[i - 1, j - 1] + cost);
                }
            }
            // return result
            return matrix[normRaiderLength, normBannedLength];
        }
    
        private static BanCheckResult CheckRaider(string raiderName, IReadOnlyList<BannedRaider> banList, IReadOnlyList<LanguageData> languages, string connectionLabel)
        {
            foreach (BannedRaider bannedUser in banList)
            {
                var levDistance = CalculateLevenshteinDistance(raiderName.NormalizeAndClean(connectionLabel), bannedUser.Name.NormalizeAndClean(connectionLabel));           
                if (levDistance == 0)
                {
                    return new()
                    {
                        RaiderName = raiderName,
                        IsBanned = true,
                        MatchType = ResultType.IS_EXACTMATCH,
                        LevenshteinDistance = levDistance,
                        BannedUserName = bannedUser.Name,
                        BanReason = bannedUser.Notes,
                    };
                }

                var lang = languages.FirstOrDefault(x => x.Language == bannedUser.Language);
                if (lang is null)
                    throw new Exception($"No language in table matches with banned user. Banned User Language: {bannedUser.Language}.");

                var dt = new DataTable();
                var weight = (double)dt.Compute(lang.Weight, "");

                //double weight = 1 / 5d;
                int N = bannedUser.Name.NormalizeAndClean(connectionLabel).Length;
                double K = N - levDistance;
                var nc2 = Binomial.CDF(weight, N, K-1);
                var log10p = Math.Log10(1 - nc2);

                if (log10p <= bannedUser.Log10p)
                {
                    return new()
                    {
                        RaiderName = raiderName,
                        IsBanned = true,
                        MatchType = ResultType.IS_SIMILAR_MATCH,
                        LevenshteinDistance = levDistance,
                        BannedUserName = bannedUser.Name,
                        BanReason = bannedUser.Notes,
                        Log10p = log10p,
                    };
                }
            }

            return new()
            {
                RaiderName = raiderName,
                IsBanned = false,
                MatchType = ResultType.NO_MATCH,
            };
        }
    }
}
