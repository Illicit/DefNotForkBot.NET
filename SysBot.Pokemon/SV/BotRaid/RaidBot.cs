using Discord;
using PKHeX.Core;
using SysBot.Base;
using SysBot.Pokemon.SV;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static SysBot.Base.SwitchButton;
using static SysBot.Pokemon.RaidSettingsSV;

namespace SysBot.Pokemon
{
    public class RaidBotSV : PokeRoutineExecutor9SV, ICountBot
    {
        private readonly PokeTradeHub<PK9> Hub;
        private readonly RaidSettingsSV Settings;
        public ICountSettings Counts => Settings;
        public static ConcurrentQueue<(byte[]?, EmbedBuilder)> EmbedQueue { get; set; } = new();
        private RemoteControlAccessList RaiderBanList => Settings.RaiderBanList;

        public RaidBotSV(PokeBotState cfg, PokeTradeHub<PK9> hub) : base(cfg)
        {
            Hub = hub;
            Settings = hub.Config.RaidSV;
        }

        private const string RaidBotVersion = "Version 0.4.20";
        private int RaidsAtStart;
        private int RaidCount;
        private int WinCount;
        private int LossCount;
        private int SeedIndexToReplace;
        private int RotationCount;
        private readonly Dictionary<ulong, int> RaidTracker = new();
        private SAV9SV HostSAV = new();
        private DateTime StartTime = DateTime.Now;

        private ulong TodaySeed;
        private ulong OverworldOffset;
        private ulong ConnectedOffset;
        private ulong TeraRaidBlockOffset;
        private readonly ulong[] TeraNIDOffsets = new ulong[3];
        private string TeraRaidCode { get; set; } = string.Empty;

        public override async Task MainLoop(CancellationToken token)
        {
            if (Settings.ConfigureRolloverCorrection)
            {
                await RolloverCorrectionSV(token).ConfigureAwait(false);
                return;
            }

            if (Settings.RaidEmbedParameters.Count < 1)
            {
                Log("RaidEmbedParameters cannot be 0. Please setup your parameters for the raid(s) you are hosting.");
                return;
            }

            if (Settings.TimeToWait is < 0 or > 180)
            {
                Log("Time to wait must be between 0 and 180 seconds.");
                return;
            }

            if (Settings.RaidsBetweenUpdate == 0 || Settings.RaidsBetweenUpdate < -1)
            {
                Log("Raids between updating the global ban list must be greater than 0, or -1 if you want it off.");
                return;
            }

            try
            {
                Log("Identifying trainer data of the host console.");
                HostSAV = await IdentifyTrainer(token).ConfigureAwait(false);
                await InitializeHardware(Settings, token).ConfigureAwait(false);

                Log("Starting main RaidBot loop.");
                await InnerLoop(token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log(e.Message);
            }

            Log($"Ending {nameof(RaidBot)} loop.");
            await HardStop().ConfigureAwait(false);
        }

        private async Task InnerLoop(CancellationToken token)
        {
            bool partyReady;
            List<(ulong, TradeMyStatus)> lobbyTrainers;
            StartTime = DateTime.Now;
            var dayRoll = 0;
            RotationCount = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {

                    // Initialize offsets at the start of the routine and cache them.
                    await InitializeSessionOffsets(token).ConfigureAwait(false);
                    if (RaidCount == 0)
                    {
                        TodaySeed = BitConverter.ToUInt64(await SwitchConnection.ReadBytesAbsoluteAsync(TeraRaidBlockOffset, 8, token).ConfigureAwait(false), 0);
                        Log($"Today Seed: {TodaySeed:X8}");
                    }

                    var currentSeed = BitConverter.ToUInt64(await SwitchConnection.ReadBytesAbsoluteAsync(TeraRaidBlockOffset, 8, token).ConfigureAwait(false), 0);
                    if (TodaySeed != currentSeed)
                    {
                        var msg = $"Current Today Seed {currentSeed:X8} does not match Starting Today Seed: {TodaySeed:X8} after rolling back 1 day. ";
                        if (dayRoll != 0)
                        {
                            Log(msg + "Stopping routine for lost raid.");
                            return;
                        }
                        Log(msg);
                        await CloseGame(Hub.Config, token).ConfigureAwait(false);
                        await RolloverCorrectionSV(token).ConfigureAwait(false);
                        await StartGameRaid(Hub.Config, token).ConfigureAwait(false);

                        dayRoll++;
                        continue;
                    }

                    // Get initial raid counts for comparison later.
                    await CountRaids(null, false, token).ConfigureAwait(false);

                    // Clear NIDs.
                    await SwitchConnection.WriteBytesAbsoluteAsync(new byte[32], TeraNIDOffsets[0], token).ConfigureAwait(false);

                    // Connect online and enter den.
                    if (!await PrepareForRaid(token).ConfigureAwait(false))
                    {
                        Log("Failed to prepare the raid, rebooting the game.");
                        await ReOpenGame(Hub.Config, token).ConfigureAwait(false);
                        continue;
                    }

                    // Wait until we're in lobby.
                    if (!await GetLobbyReady(token).ConfigureAwait(false))
                        continue;

                    // Read trainers until someone joins.
                    (partyReady, lobbyTrainers) = await ReadTrainers(token).ConfigureAwait(false);
                    if (!partyReady)
                    {
                        if (Settings.RaidEmbedParameters.Count is 1)
                        {
                            // Should add overworld recovery with a game restart fallback.
                            await RegroupFromBannedUser(token).ConfigureAwait(false);

                            if (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                            {
                                Log("Something went wrong, attempting to recover.");
                                await ReOpenGame(Hub.Config, token).ConfigureAwait(false);
                                continue;
                            }

                            // Clear trainer OTs.
                            Log("Clearing stored OTs");
                            for (int i = 0; i < 3; i++)
                            {
                                List<long> ptr = new(Offsets.Trader2MyStatusPointer);
                                ptr[2] += i * 0x30;
                                await SwitchConnection.PointerPoke(new byte[16], ptr, token).ConfigureAwait(false);
                            }
                        }

                        else if (Settings.RaidEmbedParameters.Count > 1)
                        {
                            Log("Lobby was empty.. Resetting game and moving on to next rotation.");
                            RotationCount++;
                            await CloseGame(Hub.Config, token).ConfigureAwait(false);
                            await StartGameRaid(Hub.Config, token).ConfigureAwait(false);
                        }

                        continue;
                    }

                    await CompleteRaid(lobbyTrainers, token).ConfigureAwait(false);
                }
                catch
                {
                    var attempts = Hub.Config.Timings.ReconnectAttempts;
                    var delay = Hub.Config.Timings.ExtraReconnectDelay;
                    var protocol = Config.Connection.Protocol;
                    if (!await TryReconnect(attempts, delay, protocol, token).ConfigureAwait(false))
                        return;

                    Log($"Successful reconnect on lost connection. Attempting full recovery.");

                    await ReOpenGame(Hub.Config, token).ConfigureAwait(false); // Reset game to resync 
                }
            }
        }

        public override async Task HardStop()
        {
            await CleanExit(CancellationToken.None).ConfigureAwait(false);
        }

        private async Task CompleteRaid(List<(ulong, TradeMyStatus)> trainers, CancellationToken token)
        {
            List<(ulong, TradeMyStatus)> lobbyTrainersFinal = new();
            if (await IsConnectedToLobby(token).ConfigureAwait(false))
            {
                int b = 0;
                Log("Preparing for battle!");
                while (!await IsInRaid(token).ConfigureAwait(false))
                    await Click(A, 1_000, token).ConfigureAwait(false);

                if (await IsInRaid(token).ConfigureAwait(false))
                {
                    // Clear NIDs to refresh player check.
                    await SwitchConnection.WriteBytesAbsoluteAsync(new byte[32], TeraNIDOffsets[0], token).ConfigureAwait(false);
                    await Task.Delay(5_000, token).ConfigureAwait(false);

                    // Loop through trainers again in case someone disconnected.
                    for (int i = 0; i < 3; i++)
                    {
                        var player = i + 2;
                        var nidOfs = TeraNIDOffsets[i];
                        var data = await SwitchConnection.ReadBytesAbsoluteAsync(nidOfs, 8, token).ConfigureAwait(false);
                        var nid = BitConverter.ToUInt64(data, 0);

                        if (nid == 0)
                            continue;

                        List<long> ptr = new(Offsets.Trader2MyStatusPointer);
                        ptr[2] += i * 0x30;
                        var trainer = await GetTradePartnerMyStatus(ptr, token).ConfigureAwait(false);

                        if (string.IsNullOrWhiteSpace(trainer.OT))
                            continue;

                        lobbyTrainersFinal.Add((nid, trainer));
                        var tr = trainers.FirstOrDefault(x => x.Item2.OT == trainer.OT);
                        if (tr != default)
                            Log($"Player {i + 2} matches lobby check for {trainer.OT}.");
                        else Log($"New Player {i + 2}: {trainer.OT} | TID: {trainer.DisplayTID} | NID: {nid}.");
                    }
                    var nidDupe = lobbyTrainersFinal.Select(x => x.Item1).ToList();
                    var dupe = lobbyTrainersFinal.Count > 1 && nidDupe.Distinct().Count() == 1;
                    if (dupe)
                    {
                        // We read bad data, reset game to end early and recover.
                        var msg = "Oops! Something went wrong, resetting to recover.";
                        await EnqueueEmbed(null, msg, false, false, false, token).ConfigureAwait(false);
                        await ReOpenGame(Hub.Config, token).ConfigureAwait(false);
                        return;
                    }

                    var names = lobbyTrainersFinal.Select(x => x.Item2.OT).ToList();
                    bool hatTrick = lobbyTrainersFinal.Count == 3 && names.Distinct().Count() == 1;

                    await Task.Delay(15_000, token).ConfigureAwait(false);
                    await EnqueueEmbed(names, "", hatTrick, false, false, token).ConfigureAwait(false);
                }

                while (await IsConnectedToLobby(token).ConfigureAwait(false))
                {
                    b++;
                    await Click(A, 3_500, token).ConfigureAwait(false);
                    if (b % 10 == 0)
                        Log("Still in battle...");
                }
            }

            Log("Raid lobby disbanded!");
            await Click(B, 0_500, token).ConfigureAwait(false);
            await Click(B, 0_500, token).ConfigureAwait(false);
            await Click(DDOWN, 0_500, token).ConfigureAwait(false);

            Log("Returning to overworld...");
            while (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                await Click(A, 1_000, token).ConfigureAwait(false);

            await CountRaids(lobbyTrainersFinal, true, token).ConfigureAwait(false);

            await CloseGame(Hub.Config, token).ConfigureAwait(false);
            await StartGameRaid(Hub.Config, token).ConfigureAwait(false);

            if (Settings.KeepDaySeed)
                OverrideTodaySeed();
        }

        private void ApplyPenalty(List<(ulong, TradeMyStatus)> trainers)
        {
            for (int i = 0; i < trainers.Count; i++)
            {
                var nid = trainers[i].Item1;
                var name = trainers[i].Item2.OT;
                if (RaidTracker.ContainsKey(nid) && nid != 0)
                {
                    var entry = RaidTracker[nid];
                    var Count = entry + 1;
                    RaidTracker[nid] = Count;
                    Log($"Player: {name} completed the raid with catch count: {Count}.");

                    if (Settings.CatchLimit != 0 && Count == Settings.CatchLimit)
                        Log($"Player: {name} has met the catch limit {Count}/{Settings.CatchLimit}, adding to the block list for this session for {Settings.RaidEmbedParameters[RotationCount].Species}.");
                }
            }
        }

        private async void OverrideTodaySeed()
        {
            var todayoverride = BitConverter.GetBytes(TodaySeed);
            List<long> ptr = new(Offsets.TeraRaidBlockPointer);
            ptr[2] += 0x8;
            await SwitchConnection.PointerPoke(todayoverride, ptr, CancellationToken.None).ConfigureAwait(false);           
        }

        private async void OverrideSeedIndex(int index)
        {
            var token = CancellationToken.None;
            List<long> ptr = new(Offsets.TeraRaidBlockPointer);
            ptr[2] = 0x40 + ((index + 1) * 0x20);
            if (RotationCount >= Settings.RaidEmbedParameters.Count)
                RotationCount = 0;
            byte[] inj = BitConverter.GetBytes(Settings.RaidEmbedParameters[RotationCount].Seed);
            var currseed = await SwitchConnection.PointerPeek(4, ptr, token).ConfigureAwait(false);
            Log($"Replacing {BitConverter.ToString(currseed)} with {BitConverter.ToString(inj)}.");            
            await SwitchConnection.PointerPoke(inj, ptr, token).ConfigureAwait(false);

            var ptr2 = ptr;
            ptr2[2] += 0x08;
            var crystal = BitConverter.GetBytes((int)Settings.RaidEmbedParameters[RotationCount].CrystalType);
            var currcrystal = await SwitchConnection.PointerPeek(1, ptr2, token).ConfigureAwait(false);
            if (currcrystal != crystal)
            {                
                Log($"Replacing Raid Crystal type from {BitConverter.ToString(currcrystal)} to {BitConverter.ToString(crystal)}.");
                await SwitchConnection.PointerPoke(crystal, ptr2, token).ConfigureAwait(false);
            }
        }

        private async Task CountRaids(List<(ulong, TradeMyStatus)>? trainers, bool rotate, CancellationToken token)
        {
            List<uint> seeds = new();
            var data = await SwitchConnection.ReadBytesAbsoluteAsync(TeraRaidBlockOffset, 2304, token).ConfigureAwait(false);
            for (int i = 0; i < 69; i++)
            {
                var seed = BitConverter.ToUInt32(data.Slice(32 + (i * 32), 4));
                if (seed != 0)                
                    seeds.Add(seed);                
                if (seed == 0)
                {
                    Log($"Seed rotation will occur at index {i}");
                    SeedIndexToReplace = i;
                }
            }

            Log($"Active raid count: {seeds.Count}");
            if (RaidCount == 0)
            {
                RaidsAtStart = seeds.Count;
                if (Settings.KeepDaySeed)
                    OverrideTodaySeed();
                return;
            }

            if (trainers is not null)
            {
                Log("Back in the overworld, checking if we won or lost.");
                Settings.AddCompletedRaids();
                if (RaidsAtStart > seeds.Count)
                {
                    Log("We defeated the raid boss!");
                    WinCount++;
                    if (trainers.Count > 0 || TodaySeed != BitConverter.ToUInt64(data.Slice(0, 8)) && RaidsAtStart == seeds.Count)
                        ApplyPenalty(trainers);

                    if (rotate && Settings.RaidEmbedParameters.Count > 1)
                    {
                        Log($"Seed index to replace found at location {SeedIndexToReplace}");
                        RotationCount++;
                        if (RotationCount >= Settings.RaidEmbedParameters.Count)
                        {
                            RotationCount = 0;
                            Log($"Rotation Count = Raid Parameter Count. Resetting Rotation Count to {RotationCount}");
                        }

                        await EnqueueEmbed(null, "", false, false, true, token).ConfigureAwait(false);
                    }

                    return;
                }

                Log("We lost the raid...");
                LossCount++;
            }
        }

        private async void InjectPartyPk(string battlepk)
        {
            var set = new ShowdownSet(battlepk);
            var template = AutoLegalityWrapper.GetTemplate(set);
            PK9 pk = (PK9)HostSAV.GetLegal(template, out _);
            pk.ResetPartyStats();
            var offset = await SwitchConnection.PointerAll(Offsets.BoxStartPokemonPointer, CancellationToken.None).ConfigureAwait(false);
            await SwitchConnection.WriteBytesAbsoluteAsync(pk.EncryptedBoxData, offset, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task<bool> PrepareForRaid(CancellationToken token)
        {
            Log("Preparing lobby...");

            if (Settings.RaidEmbedParameters[RotationCount].PartyPK.Length > 0)
            {
                await SetCurrentBox(0, token).ConfigureAwait(false);
                var res = string.Join("\n", Settings.RaidEmbedParameters[RotationCount].PartyPK);
                if (res.Length > 4096)
                    res = res[..4096];
                InjectPartyPk(res);

                await Click(X, 2_000, token).ConfigureAwait(false);
                await Click(DRIGHT, 0_500, token).ConfigureAwait(false);
                Log("Scrolling through menus...");
                await SetStick(SwitchStick.LEFT, 0, -32000, 1_000, token).ConfigureAwait(false);
                await SetStick(SwitchStick.LEFT, 0, 0, 0, token).ConfigureAwait(false);
                Log("Tap tap...");
                for (int i = 0; i < 2; i++)
                    await Click(DDOWN, 0_500, token).ConfigureAwait(false);
                await Click(A, 3_500, token).ConfigureAwait(false);
                await Click(Y, 0_500, token).ConfigureAwait(false);
                await Click(DLEFT, 0_800, token).ConfigureAwait(false);
                await Click(Y, 0_500, token).ConfigureAwait(false);
                for (int i = 0; i < 2; i++)
                    await Click(B, 1_500, token).ConfigureAwait(false);
                Log("Battle PK is ready!");
            }

            // Make sure we're connected.
            while (!await IsConnectedOnline(ConnectedOffset, token).ConfigureAwait(false))
            {
                Log("Connecting...");
                await RecoverToOverworld(token).ConfigureAwait(false);
                if (!await ConnectToOnline(Hub.Config, token).ConfigureAwait(false))
                    return false;
            }

            for (int i = 0; i < 5; i++)
                await Click(B, 0_500, token).ConfigureAwait(false);

            await Task.Delay(1_500, token).ConfigureAwait(false);

            // If not in the overworld, we've been attacked so quit earlier.
            if (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                return false;

            await Click(A, 3_000, token).ConfigureAwait(false);
            await Click(A, 3_000, token).ConfigureAwait(false);

            if (!Settings.RaidEmbedParameters[RotationCount].IsCoded)
                await Click(DDOWN, 1_000, token).ConfigureAwait(false);

            await Click(A, 8_000, token).ConfigureAwait(false);
            return true;
        }

        private async Task<bool> GetLobbyReady(CancellationToken token)
        {
            var x = 0;
            Log("Connecting to lobby...");
            while (!await IsConnectedToLobby(token).ConfigureAwait(false))
            {
                await Click(A, 1_000, token).ConfigureAwait(false);
                x++;
                if (x == 45)
                {
                    Log("Failed to connect to lobby, restarting game incase we were in battle/bad connection.");
                    await ReOpenGame(Hub.Config, token).ConfigureAwait(false);
                    Log("Attempting to restart routine!");
                    return false;
                }
            }
            return true;
        }

        private async Task<string> GetRaidCode(CancellationToken token)
        {
            var data = await SwitchConnection.PointerPeek(6, Offsets.TeraRaidCodePointer, token).ConfigureAwait(false);
            TeraRaidCode = Encoding.ASCII.GetString(data);
            Log($"Raid Code: {TeraRaidCode}");
            return $"\n{TeraRaidCode}\n";
        }

        private async Task<bool> CheckIfTrainerBanned(TradeMyStatus trainer, ulong nid, int player, bool updateBanList, CancellationToken token)
        {
            Log($"Player {player}: {trainer.OT} | TID: {trainer.DisplayTID} | NID: {nid}");
            if (!RaidTracker.ContainsKey(nid))
                RaidTracker.Add(nid, 0);

            int val = 0;
            var msg = string.Empty;
            var banResultCC = Settings.RaidsBetweenUpdate == -1 ? (false, "") : await BanService.IsRaiderBanned(trainer.OT, Settings.BanListURL, Connection.Label, updateBanList).ConfigureAwait(false);
            var banResultCFW = RaiderBanList.List.FirstOrDefault(x => x.ID == nid);
            bool isBanned = banResultCC.Item1 || banResultCFW != default;

            bool blockResult = false;
            var blockCheck = RaidTracker.ContainsKey(nid);
            if (blockCheck)
            {
                RaidTracker.TryGetValue(nid, out val);
                if (val >= Settings.CatchLimit && Settings.CatchLimit != 0) // Soft pity - block user
                {
                    blockResult = true;
                    RaidTracker[nid] = val + 1;
                    Log($"Player: {trainer.OT} current penalty count: {val}.");
                }
                if (val == Settings.CatchLimit + 2 && Settings.CatchLimit != 0) // Hard pity - ban user
                {
                    msg = $"{trainer.OT} is now banned for repeatedly attempting to go beyond the catch limit for {Settings.RaidEmbedParameters[RotationCount].Species} on {DateTime.Now}.";
                    Log(msg);
                    RaiderBanList.List.Add(new() { ID = nid, Name = trainer.OT, Comment = msg });
                    blockResult = false;
                    await EnqueueEmbed(null, $"Penalty #{val}\n" + msg, false, true, false, token).ConfigureAwait(false);
                    return true;
                }
                if (blockResult && !isBanned)
                {
                    msg = $"Penalty #{val}\n{trainer.OT} has already reached the catch limit.\nPlease do not join again.\nRepeated attempts to join like this will result in a ban from future raids.";
                    Log(msg);
                    await EnqueueEmbed(null, msg, false, true, false, token).ConfigureAwait(false);
                    return true;
                }
            }

            if (isBanned)
            {
                msg = banResultCC.Item1 ? banResultCC.Item2 : $"Penalty #{val}\n{banResultCFW!.Name} was found in the host's ban list.\n{banResultCFW.Comment}";
                Log(msg);
                await EnqueueEmbed(null, msg, false, true, false, token).ConfigureAwait(false);
                return true;
            }
            return false;
        }

        // This is messy, needs a way to check if player X is ready, and when we're in a raid, in order to avoid adding players that may have disconnected or quit. Players get shifted down as they leave.
        private async Task<(bool, List<(ulong, TradeMyStatus)>)> ReadTrainers(CancellationToken token)
        {
            await EnqueueEmbed(null, "", false, false, false, token).ConfigureAwait(false);

            List<(ulong, TradeMyStatus)> lobbyTrainers = new();
            var wait = TimeSpan.FromSeconds(Settings.TimeToWait);
            var endTime = DateTime.Now + wait;
            bool full = false;
            bool updateBanList = Settings.RaidsBetweenUpdate != -1 && (RaidCount == 0 || RaidCount % Settings.RaidsBetweenUpdate == 0);

            while (!full && (DateTime.Now < endTime))
            {
                // Loop through trainers
                for (int i = 0; i < 3; i++)
                {
                    var player = i + 2;
                    Log($"Waiting for Player {player} to load...");

                    var nidOfs = TeraNIDOffsets[i];
                    var data = await SwitchConnection.ReadBytesAbsoluteAsync(nidOfs, 8, token).ConfigureAwait(false);
                    var nid = BitConverter.ToUInt64(data, 0);
                    while (nid == 0 && (DateTime.Now < endTime))
                    {
                        await Task.Delay(0_500, token).ConfigureAwait(false);
                        data = await SwitchConnection.ReadBytesAbsoluteAsync(nidOfs, 8, token).ConfigureAwait(false);
                        nid = BitConverter.ToUInt64(data, 0);
                    }

                    List<long> ptr = new(Offsets.Trader2MyStatusPointer);
                    ptr[2] += i * 0x30;
                    var trainer = await GetTradePartnerMyStatus(ptr, token).ConfigureAwait(false);

                    while (trainer.OT.Length == 0 && (DateTime.Now < endTime))
                    {
                        await Task.Delay(0_500, token).ConfigureAwait(false);
                        trainer = await GetTradePartnerMyStatus(ptr, token).ConfigureAwait(false);
                    }

                    if (nid != 0 && !string.IsNullOrWhiteSpace(trainer.OT))
                    {
                        if (await CheckIfTrainerBanned(trainer, nid, player, updateBanList, token).ConfigureAwait(false))
                            return (false, lobbyTrainers);

                        updateBanList = false;
                    }

                    if (lobbyTrainers.FirstOrDefault(x => x.Item1 == nid) != default && trainer.OT.Length > 0)
                        lobbyTrainers[i] = (nid, trainer);
                    else if (nid > 0 && trainer.OT.Length > 0)
                        lobbyTrainers.Add((nid, trainer));

                    full = lobbyTrainers.Count == 3;
                    if (full || (DateTime.Now >= endTime))
                        break;
                }
            }

            await Task.Delay(5_000, token).ConfigureAwait(false);

            RaidCount++;
            if (lobbyTrainers.Count == 0)
            {
                Log("Nobody joined the raid, recovering...");
                return (false, lobbyTrainers);
            }
            Log($"Raid #{RaidCount} is starting!");
            return (true, lobbyTrainers);
        }

        private async Task<bool> IsConnectedToLobby(CancellationToken token)
        {
            var data = await SwitchConnection.ReadBytesMainAsync(Offsets.TeraLobby, 1, token).ConfigureAwait(false);
            return data[0] != 0x00; // 0 when in lobby but not connected
        }

        private async Task<bool> IsInRaid(CancellationToken token)
        {
            var data = await SwitchConnection.ReadBytesMainAsync(Offsets.LoadedIntoDesiredState, 1, token).ConfigureAwait(false);
            return data[0] == 0x02; // 2 when in raid, 1 when not
        }

        private async Task RolloverCorrectionSV(CancellationToken token)
        {
            var scrollroll = Settings.DateTimeFormat switch
            {
                DTFormat.DDMMYY => 0,
                DTFormat.YYMMDD => 2,
                _ => 1,
            };

            for (int i = 0; i < 2; i++)
                await Click(B, 0_150, token).ConfigureAwait(false);

            for (int i = 0; i < 2; i++)
                await Click(DRIGHT, 0_150, token).ConfigureAwait(false);
            await Click(DDOWN, 0_150, token).ConfigureAwait(false);
            await Click(DRIGHT, 0_150, token).ConfigureAwait(false);
            await Click(A, 1_250, token).ConfigureAwait(false); // Enter settings

            await PressAndHold(DDOWN, 2_000, 0_250, token).ConfigureAwait(false); // Scroll to system settings
            await Click(A, 1_250, token).ConfigureAwait(false);

            await PressAndHold(DDOWN, Settings.HoldTimeForRollover, 1_000, token).ConfigureAwait(false);
            await Click(DUP, 0_500, token).ConfigureAwait(false);

            await Click(A, 1_250, token).ConfigureAwait(false);
            for (int i = 0; i < 2; i++)
                await Click(DDOWN, 0_150, token).ConfigureAwait(false);
            await Click(A, 0_500, token).ConfigureAwait(false);
            for (int i = 0; i < scrollroll; i++) // 0 to roll day for DDMMYY, 1 to roll day for MMDDYY, 3 to roll hour
                await Click(DRIGHT, 0_200, token).ConfigureAwait(false);

            await Click(DDOWN, 0_200, token).ConfigureAwait(false);

            for (int i = 0; i < 8; i++) // Mash DRIGHT to confirm
                await Click(DRIGHT, 0_200, token).ConfigureAwait(false);

            await Click(A, 0_200, token).ConfigureAwait(false); // Confirm date/time change
            await Click(HOME, 1_000, token).ConfigureAwait(false); // Back to title screen
        }

        private async Task RegroupFromBannedUser(CancellationToken token)
        {
            Log("Attempting to remake lobby..");
            await Click(B, 2_000, token).ConfigureAwait(false);
            await Click(A, 3_000, token).ConfigureAwait(false);
            await Click(A, 3_000, token).ConfigureAwait(false);
            await Click(B, 1_000, token).ConfigureAwait(false);
        }

        private async Task InitializeSessionOffsets(CancellationToken token)
        {
            Log("Caching session offsets...");
            OverworldOffset = await SwitchConnection.PointerAll(Offsets.OverworldPointer, token).ConfigureAwait(false);
            ConnectedOffset = await SwitchConnection.PointerAll(Offsets.IsConnectedPointer, token).ConfigureAwait(false);
            TeraRaidBlockOffset = await SwitchConnection.PointerAll(Offsets.TeraRaidBlockPointer, token).ConfigureAwait(false);

            var nidPointer = new long[] { Offsets.LinkTradePartnerNIDPointer[0], Offsets.LinkTradePartnerNIDPointer[1], Offsets.LinkTradePartnerNIDPointer[2] };
            for (int p = 0; p < TeraNIDOffsets.Length; p++)
            {
                nidPointer[2] = Offsets.LinkTradePartnerNIDPointer[2] + (p * 0x8);
                TeraNIDOffsets[p] = await SwitchConnection.PointerAll(nidPointer, token).ConfigureAwait(false);
            }
            Log("Caching offsets complete!");
        }

        private async Task EnqueueEmbed(List<string>? names, string message, bool hatTrick, bool disband, bool upnext, CancellationToken token)
        {
            // Title can only be up to 256 characters.
            var title = hatTrick && names is not null ? $"**🪄🎩✨ {names[0]} with the Hat Trick! ✨🎩🪄**" : Settings.RaidEmbedParameters[RotationCount].Title.Length > 0 ? Settings.RaidEmbedParameters[RotationCount].Title : "Tera Raid Notification";
            if (title.Length > 256)
                title = title[..256];

            // Description can only be up to 4096 characters.
            var description = Settings.RaidEmbedParameters[RotationCount].Description.Length > 0 ? string.Join("\n", Settings.RaidEmbedParameters[RotationCount].Description) : "";
            if (description.Length > 4096)
                description = description[..4096];

            string code = string.Empty;
            if (names is null)
                code = $"**{(Settings.RaidEmbedParameters[RotationCount].IsCoded ? await GetRaidCode(token).ConfigureAwait(false) : "Free For All")}**";

            if (disband) // Wait for trainer to load before disband
                await Task.Delay(5_000, token).ConfigureAwait(false);

            byte[]? bytes = Array.Empty<byte>();
            if (Settings.TakeScreenshot && !upnext)
                bytes = await SwitchConnection.PixelPeek(token).ConfigureAwait(false) ?? Array.Empty<byte>();

            string disclaimer = Settings.RaidEmbedParameters.Count > 1 ? "Disclaimer: Raids are on rotation via seed injection.\n" : "";

            if (upnext)
                title = "Preparing next raid...";

            var embed = new EmbedBuilder()
            {
                Title = disband ? $"**Raid canceled: [{TeraRaidCode}]**" : title,
                Description = disband ? message : description,
                Color = disband ? Color.Red : hatTrick ? Color.Purple : Color.Green,
                ImageUrl = bytes.Length > 0 ? "attachment://zap.jpg" : default,
            }.WithFooter(new EmbedFooterBuilder()
            {
                Text = $"Host: {HostSAV.OT} | Uptime: {StartTime - DateTime.Now:d\\.hh\\:mm\\:ss}\n" +
                       $"Raids: {RaidCount} | Wins: {WinCount} | Losses: {LossCount}\n" + disclaimer +
                       $"Powered By: RaidBotSV - {RaidBotVersion}",
            });

            if (!disband && names is null && !upnext)
            {
                embed.AddField("**Waiting in lobby!**", $"Raid code: {code}");
            }

            if (!disband && names is not null && !upnext)
            {
                var players = string.Empty;
                if (names.Count == 0)
                    players = "Though our party did not make it :(";
                else
                {
                    int i = 2;
                    names.ForEach(x =>
                    {
                        players += $"Player {i} - **{x}**\n";
                        i++;
                    });
                }

                embed.AddField($"**Raid #{RaidCount} is starting!**", players);
            }

            var turl = string.Empty;
            var form = string.Empty;

            Log($"Rotation Count: {RotationCount} | Species is {Settings.RaidEmbedParameters[RotationCount].Species}");
            PK9 pk = new()
            {
                Species = (ushort)Settings.RaidEmbedParameters[RotationCount].Species,
                Form = (byte)Settings.RaidEmbedParameters[RotationCount].SpeciesForm
            };
            if (pk.Form != 0)
                form = $"-{pk.Form}";
            if (Settings.RaidEmbedParameters[RotationCount].IsShiny == true)
                CommonEdits.SetIsShiny(pk, true);
            else
                CommonEdits.SetIsShiny(pk, false);

            if (Settings.RaidEmbedParameters[RotationCount].SpriteAlternateArt && Settings.RaidEmbedParameters[RotationCount].IsShiny)
                turl = AltPokeImg(pk);
            else
                turl = TradeExtensions<PK9>.PokeImg(pk, false, false);

            var fileName = $"raidecho{RotationCount}.jpg";
            embed.ThumbnailUrl = turl;
            embed.WithImageUrl($"attachment://{fileName}");
            EchoUtil.RaidEmbed(bytes, fileName, embed);
        }

        // From PokeTradeBotSV, modified.
        private async Task<bool> ConnectToOnline(PokeTradeHubConfig config, CancellationToken token)
        {
            if (await IsConnectedOnline(ConnectedOffset, token).ConfigureAwait(false))
                return true;

            await Click(X, 3_000, token).ConfigureAwait(false);
            await Click(L, 5_000 + config.Timings.ExtraTimeConnectOnline, token).ConfigureAwait(false);

            // Try one more time.
            if (!await IsConnectedOnline(ConnectedOffset, token).ConfigureAwait(false))
            {
                Log("Failed to connect the first time, trying again...");
                await RecoverToOverworld(token).ConfigureAwait(false);
                await Click(X, 3_000, token).ConfigureAwait(false);
                await Click(L, 5_000 + config.Timings.ExtraTimeConnectOnline, token).ConfigureAwait(false);
            }

            var wait = 0;
            while (!await IsConnectedOnline(ConnectedOffset, token).ConfigureAwait(false))
            {
                await Task.Delay(0_500, token).ConfigureAwait(false);
                if (++wait > 30) // More than 15 seconds without a connection.
                    return false;
            }

            // There are several seconds after connection is established before we can dismiss the menu.
            await Task.Delay(3_000 + config.Timings.ExtraTimeConnectOnline, token).ConfigureAwait(false);
            await Click(A, 1_000, token).ConfigureAwait(false);
            return true;
        }

        // From PokeTradeBotSV.
        private async Task<bool> RecoverToOverworld(CancellationToken token)
        {
            if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                return true;

            Log("Attempting to recover to overworld.");
            var attempts = 0;
            while (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
            {
                attempts++;
                if (attempts >= 30)
                    break;

                await Click(B, 1_300, token).ConfigureAwait(false);
                if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                    break;

                await Click(B, 2_000, token).ConfigureAwait(false);
                if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                    break;

                await Click(A, 1_300, token).ConfigureAwait(false);
                if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                    break;
            }

            // We didn't make it for some reason.
            if (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
            {
                Log("Failed to recover to overworld, rebooting the game.");
                await ReOpenGame(Hub.Config, token).ConfigureAwait(false);
            }
            await Task.Delay(1_000, token).ConfigureAwait(false);
            return true;
        }

        public async Task StartGameRaid(PokeTradeHubConfig config, CancellationToken token)
        {
            var timing = config.Timings;
            // Open game.
            await Click(A, 1_000 + timing.ExtraTimeLoadProfile, token).ConfigureAwait(false);

            // Menus here can go in the order: Update Prompt -> Profile -> DLC check -> Unable to use DLC.
            //  The user can optionally turn on the setting if they know of a breaking system update incoming.
            if (timing.AvoidSystemUpdate)
            {
                await Click(DUP, 0_600, token).ConfigureAwait(false);
                await Click(A, 1_000 + timing.ExtraTimeLoadProfile, token).ConfigureAwait(false);
            }

            await Click(A, 1_000 + timing.ExtraTimeCheckDLC, token).ConfigureAwait(false);
            // If they have DLC on the system and can't use it, requires an UP + A to start the game.
            // Should be harmless otherwise since they'll be in loading screen.
            await Click(DUP, 0_600, token).ConfigureAwait(false);
            await Click(A, 0_600, token).ConfigureAwait(false);

            Log("Restarting the game!");

            // Switch Logo and game load screen
            await Task.Delay(16_000 + timing.ExtraTimeLoadGame, token).ConfigureAwait(false);

            if (Settings.RaidEmbedParameters.Count > 1 && Settings.RaidEmbedParameters[RotationCount].Seed != default)
            {
                OverrideSeedIndex(SeedIndexToReplace);
                Log("Seed override completed.");
            }

            await Task.Delay(1_000, token).ConfigureAwait(false);

            for (int i = 0; i < 8; i++)
                await Click(A, 1_000, token).ConfigureAwait(false);

            var timer = 60_000;
            while (!await IsOnOverworldTitle(token).ConfigureAwait(false))
            {
                await Task.Delay(1_000, token).ConfigureAwait(false);
                timer -= 1_000;
                // We haven't made it back to overworld after a minute, so press A every 6 seconds hoping to restart the game.
                // Don't risk it if hub is set to avoid updates.
                if (timer <= 0 && !timing.AvoidSystemUpdate)
                {
                    Log("Still not in the game, initiating rescue protocol!");
                    while (!await IsOnOverworldTitle(token).ConfigureAwait(false))
                        await Click(A, 6_000, token).ConfigureAwait(false);
                    break;
                }
            }

            await Task.Delay(5_000 + timing.ExtraTimeLoadOverworld, token).ConfigureAwait(false);
            Log("Back in the overworld!");
        }

        private async Task<bool> IsOnOverworldTitle(CancellationToken token)
        {
            var (valid, offset) = await ValidatePointerAll(Offsets.OverworldPointer, token).ConfigureAwait(false);
            if (!valid)
                return false;
            return await IsOnOverworld(offset, token).ConfigureAwait(false);
        }

        private static string AltPokeImg(PKM pkm)
        {
            string pkmform = string.Empty;
            if (pkm.Form != 0)
                pkmform = $"-{pkm.Form}";

            return _ = $"https://raw.githubusercontent.com/zyro670/PokeTextures/main/Placeholder_Sprites/scaled_up_sprites/Shiny/AlternateArt/" + $"{pkm.Species}{pkmform}" + ".png";
        }
    }
}
