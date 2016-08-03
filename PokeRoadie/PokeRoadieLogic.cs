﻿#region " Imports "

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using PokemonGo.RocketAPI;
using PokemonGo.RocketAPI.Enums;
using PokemonGo.RocketAPI.Extensions;
using PokemonGo.RocketAPI.Helpers;
using PokemonGo.RocketAPI.Logging;
using PokemonGo.RocketAPI.Exceptions;

using POGOProtos.Inventory.Item;
using POGOProtos.Networking.Responses;
using POGOProtos.Data;
using POGOProtos.Enums;
using POGOProtos.Map.Fort;
using POGOProtos.Map.Pokemon;

using PokeRoadie.Extensions;

#endregion


namespace PokeRoadie
{
    public class PokeRoadieLogic
    {
        #region " Events "

        public event Func<bool> OnPromptForCredentials;

        public event Action<MapPokemon, double> OnEncounter;
        public event Action<MapPokemon, CatchPokemonResponse, double> OnCatchAttempt;
        public event Action<MapPokemon, CatchPokemonResponse, double> OnCatch;

        public event Action<LocationData, PokemonData, DiskEncounterResponse> OnLureEncounter;
        public event Action<LocationData, PokemonData, CatchPokemonResponse> OnLureCatchAttempt;
        public event Action<LocationData, PokemonData, CatchPokemonResponse> OnLureCatch;

        public event Action<LocationData, int> OnChangeDestination;
        public event Action<LocationData> OnChangeWaypoint;
        public event Action<LocationData> OnChangeLocation;

        public event Action<LocationData, List<FortData>> OnGetAllNearbyPokestops;
        public event Action<LocationData, List<FortData>> OnGetAllNearbyGyms;
        public event Action<LocationData, List<FortData>> OnVisitPokestops;
        public event Action<LocationData, FortDetailsResponse> OnTravelingToPokestop;
        public event Action<LocationData, FortDetailsResponse, FortSearchResponse> OnVisitPokestop;
        public event Action<FortData, FortSearchResponse> OnTravelingToGym;
        public event Action<GetGymDetailsResponse> OnVisitGym;


        #endregion
        #region " Static Members "

        private static string configsDir = Path.Combine(Directory.GetCurrentDirectory(), "Configs");
        private static string pokestopsDir = Path.Combine(Directory.GetCurrentDirectory(), "Pokestops");
        private static string gymDir = Path.Combine(Directory.GetCurrentDirectory(), "Gyms");
        private static object xloLock = new object();
        private static bool inFlight = false;
        private static bool isRunning;

        #endregion
        #region " Future Singleton "

        private readonly PokeRoadieClient _client;
        private readonly PokeRoadieInventory _inventory;
        private readonly Statistics _stats;
        private readonly PokeRoadieNavigation _navigation;

        #endregion
        #region " Members "

        private DateTime? _lastLuckyEggTime;
        private DateTime? _lastIncenseTime;
        private DateTime? _lastExportTime;
        private GetPlayerResponse _playerProfile;
        private int recycleCounter = 0;
        private bool IsInitialized = false;
        private int fleeCounter = 0;
        private DateTime? fleeLast;
        private DateTime? fleeStart;
        private bool softBan = false;
        private bool hasDisplayedTransferSettings;

        #endregion

        public PokeRoadieLogic()
        {
            _client = PokeRoadieClient.Current;
            _inventory = new PokeRoadieInventory(_client);
            _stats = new Statistics();
            _navigation = new PokeRoadieNavigation(_client);
            _navigation.OnChangeLocation += RelayLocation;
        }

        public void Stop()
        {
            isRunning = false;
        }
        public async Task CloseApplication(int exitCode)
        {
            for (int i = 3; i > 0; i--)
            {
                Logger.Write($"PokeRoadie will be closed in {i * 5} seconds!", LogLevel.Warning);
                await Task.Delay(5000);
            }
            await Task.Delay(15000);
            System.Environment.Exit(exitCode);
        }

        private void DeleteOldFiles(string dir)
        {
            if (!Directory.Exists(dir)) return;
            var files = Directory.GetFiles(dir).Where(x=>x.EndsWith(".txt")).ToList();
            foreach (var file in files)
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    //ignore
                }
        }


        public async Task Execute()
        {
            Git.CheckVersion();

            if (!isRunning)
                isRunning = true;

            //check pokestop dir
            if (!Directory.Exists(pokestopsDir)) Directory.CreateDirectory(pokestopsDir);
            DeleteOldFiles(pokestopsDir);

            //check pokestop dir
            if (!Directory.Exists(gymDir)) Directory.CreateDirectory(gymDir);
            DeleteOldFiles(gymDir);

            //run xlo on own thread
            Task.Run(new Action(Xlo));


            //if (PokeRoadieSettings.Current.CurrentLatitude == 0 || PokeRoadieSettings.Current.CurrentLongitude == 0)
            //{
            //    Logger.Write($"Please change first Latitude and/or Longitude because currently your using default values!", LogLevel.Error);
            //}
            //else
            //{
            //    Logger.Write($"Make sure Lat & Lng is right. Exit Program if not! Lat: {_client.CurrentLatitude} Lng: {_client.CurrentLongitude}", LogLevel.Warning);
            //    for (int i = 3; i > 0; i--)
            //    {
            //        Logger.Write($"Script will continue in {i * 5} seconds!", LogLevel.Warning);
            //        await Task.Delay(5000);
            //    }
            //}

            Logger.Write($"Logging in via: {PokeRoadieSettings.Current.AuthType}", LogLevel.Info);
            while (isRunning)
            {
                try
                {
                    switch (PokeRoadieSettings.Current.AuthType)
                    {
                        case AuthType.Ptc:
                            await _client.Login.DoPtcLogin(PokeRoadieSettings.Current.Username, PokeRoadieSettings.Current.Password);
                            break;
                        case AuthType.Google:
                            await _client.Login.DoGoogleLogin(PokeRoadieSettings.Current.Username, PokeRoadieSettings.Current.Password);
                            break;
                        default:
                            Logger.Write("wrong AuthType");
                            Environment.Exit(0);
                            break;
                    }

                    await PostLoginExecute();
                }
                catch(PtcOfflineException e)
                {
                    Logger.Write("(LOGIN ERROR) The Ptc servers are currently offline. Waiting 30 seconds... ", LogLevel.None, ConsoleColor.Red);
                    await Task.Delay(30000);;
                }
                catch (Exception e)
                {
                    if (e.Message.Contains("NeedsBrowser"))
                    {
                        Logger.Write("(LOGIN ERROR) Please login to your google account and turn off 'Two-Step Authentication' under security settings. If you do NOT want to disable your two-factor auth, please visit the following link and setup an app password. This is the only way of using the bot without disabling two-factor authentication: https://security.google.com/settings/security/apppasswords. Trying automatic restart in 15 seconds...", LogLevel.None, ConsoleColor.Red);
                        await Task.Delay(15000);
                    }
                    else if (e.Message.Contains("BadAuthentication"))
                    {
                        Logger.Write("(LOGIN ERROR) The username and password provided failed. " + e.Message, LogLevel.None, ConsoleColor.Red);
                        if (OnPromptForCredentials != null)
                        {
                            var result = OnPromptForCredentials.Invoke();
                            if (!result)
                            {
                                Logger.Write("Username and password for login not provided. Login screen closed.");
                                await CloseApplication(0);
                            }
                        }
                    }
                    else
                    {
                        Logger.Write($"(FATAL ERROR) Unhandled exception encountered: {e.Message.ToString()}.", LogLevel.Error);
                        Logger.Write("Restarting the application due to error...", LogLevel.Warning);
                    }
                    await Execute();
                }          
            }
            isRunning = false;
        }

        private async Task WriteStats()
        {
            if (!_client.RefreshEndDate.HasValue || _client.RefreshEndDate.Value <= DateTime.Now)
            {
                await PokeRoadieInventory.getCachedInventory(_client);
                _playerProfile = await _client.Player.GetPlayer();
                var playerName = Statistics.GetUsername(_client, _playerProfile);
                _stats.UpdateConsoleTitle(_client, _inventory);
                var currentLevelInfos = await Statistics._getcurrentLevelInfos(_inventory);

                Logger.Write("====== User Info ======", LogLevel.None, ConsoleColor.Yellow);
                if (PokeRoadieSettings.Current.AuthType == AuthType.Ptc)
                    Logger.Write($"PTC Account: {playerName}\n", LogLevel.None, ConsoleColor.White);
                Logger.Write($"Name: {playerName}", LogLevel.None, ConsoleColor.White);
                Logger.Write($"Team: {_playerProfile.PlayerData.Team}", LogLevel.None, ConsoleColor.White);
                Logger.Write($"Level: {currentLevelInfos}", LogLevel.None, ConsoleColor.White);
                Logger.Write($"Stardust: {_playerProfile.PlayerData.Currencies.ToArray()[1].Amount}", LogLevel.None, ConsoleColor.White);
                var items = await _inventory.GetItems();
                Logger.Write($"====== Items ({items.Select(x=>x.Count).Sum()}) ======", LogLevel.None, ConsoleColor.Yellow);
                foreach (var item in items)
                {
                    Logger.Write($"{(item.ItemId).ToString().Replace("Item","")} x {item.Count}", LogLevel.None, ConsoleColor.White);
                }
                await DisplayHighests();
                _client.RefreshEndDate = DateTime.Now.AddMinutes(PokeRoadieSettings.Current.DisplayRefreshMinutes);
            }

        } 

        public async Task PostLoginExecute()
        {
            Logger.Write($"Client logged in", LogLevel.Info);

            while (true)
            {

                if (!isRunning) break;

                if (!IsInitialized)
                {
                    //write stats
                    await WriteStats();

                    //get ignore lists
                    var PokemonsNotToTransfer = PokeRoadieSettings.Current.PokemonsNotToTransfer;
                    var PokemonsNotToCatch = PokeRoadieSettings.Current.PokemonsNotToCatch;
                    var PokemonsToEvolve = PokeRoadieSettings.Current.PokemonsToEvolve;

                    //revive
                    if (PokeRoadieSettings.Current.UseRevives) await RevivePokemon();

                    //heal
                    if (PokeRoadieSettings.Current.UsePotions) await HealPokemon();

                    //evolve
                    if (PokeRoadieSettings.Current.EvolvePokemon) await EvolvePokemon();

                    //transfer
                    if (PokeRoadieSettings.Current.TransferPokemon) await TransferPokemon();

                    //export
                    await Export();

                    //incense
                    if (PokeRoadieSettings.Current.UseIncense) await UseIncense();

                    //incense
                    if (PokeRoadieSettings.Current.UseIncense) await UseLuckyEgg();

                    //recycle
                    await RecycleItems();
                }
                IsInitialized = true;
                await ExecuteFarmingPokestopsAndPokemons(PokeRoadieSettings.Current.UseGPXPathing);

                /*
                * Example calls below
                *
                var profile = await _client.GetProfile();
                var settings = await _client.GetSettings();
                var mapObjects = await _client.GetMapObjects();
                var inventory = await _client.GetInventory();
                var pokemons = inventory.InventoryDelta.InventoryItems.Select(i => i.InventoryItemData?.Pokemon).Where(p => p != null && p?.PokemonId > 0);
                */

                //await Task.Delay(100);
            }
        }

        private async Task ExecuteFarmingPokestopsAndPokemons(bool path)
        {

            if (!path)
                await ExecuteFarmingPokestopsAndPokemons();
            else
            {
                var tracks = GetGpxTracks();
                var curTrkPt = 0;
                var curTrk = 0;
                var maxTrk = tracks.Count - 1;
                var curTrkSeg = 0;
                while (curTrk <= maxTrk)
                {
                    if (!isRunning) break;
                    var track = tracks.ElementAt(curTrk);
                    var trackSegments = track.Segments;
                    var maxTrkSeg = trackSegments.Count - 1;
                    while (curTrkSeg <= maxTrkSeg)
                    {
                        if (!isRunning) break;
                        var trackPoints = track.Segments.ElementAt(0).TrackPoints;
                        var maxTrkPt = trackPoints.Count - 1;
                        while (curTrkPt <= maxTrkPt)
                        {
                            if (!isRunning) break;
                            var nextPoint = trackPoints.ElementAt(curTrkPt);
                            var distance_check = LocationUtils.CalculateDistanceInMeters(_client.CurrentLatitude,
                                _client.CurrentLongitude, Convert.ToDouble(nextPoint.Lat), Convert.ToDouble(nextPoint.Lon));

                            //if (distance_check > 5000)
                            //{
                            //    Logger.Write(
                            //        $"Your desired destination of {nextPoint.Lat}, {nextPoint.Lon} is too far from your current position of {_client.CurrentLatitude}, {_client.CurrentLongitude}",
                            //        LogLevel.Error);
                            //    break;
                            //}

                            //Logger.Write(
                            //    $"Your desired destination is {nextPoint.Lat}, {nextPoint.Lon} your location is {_client.CurrentLatitude}, {_client.CurrentLongitude}",
                            //    LogLevel.Warning);

                            await CatchNearbyStops(true);
                            await _navigation.HumanPathWalking(
                                trackPoints.ElementAt(curTrkPt),
                                PokeRoadieSettings.Current.MinSpeed,
                                GetShortWalkingTask());


                            if (curTrkPt >= maxTrkPt)
                                curTrkPt = 0;
                            else
                                curTrkPt++;
                        } //end trkpts
                        if (curTrkSeg >= maxTrkSeg)
                            curTrkSeg = 0;
                        else
                            curTrkSeg++;
                    } //end trksegs
                    if (curTrk >= maxTrkSeg)
                        curTrk = 0;
                    else
                        curTrk++;
                } //end tracks
            }
        }

        private async Task ExecuteFarmingPokestopsAndPokemons()
        {

            if (!PokeRoadieSettings.Current.VisitGyms && !PokeRoadieSettings.Current.VisitPokestops)
            {
                Logger.Write("Both VisitGyms and VisitPokestops settings are false... This is boring.");
                
                await RandomHelper.RandomDelay(2500);

                if (PokeRoadieSettings.Current.CatchPokemon && !softBan)
                    await CatchNearbyPokemons();

            }

            var wayPointGeo = GetWaypointGeo();

            var distanceFromStart = LocationUtils.CalculateDistanceInMeters(
            _client.CurrentLatitude, _client.CurrentLongitude,
            wayPointGeo.Latitude, wayPointGeo.Longitude);

            // Edge case for when the client somehow ends up outside the defined radius
            if (PokeRoadieSettings.Current.MaxDistance != 0 &&
                distanceFromStart > PokeRoadieSettings.Current.MaxDistance)
            {

                if (PokeRoadieSettings.Current.FlyingEnabled && (distanceFromStart > 2000 || PokeRoadieSettings.Current.FlyLikeCaptKirk))
                { 
                    if (PokeRoadieSettings.Current.FlyLikeCaptKirk)
                    {
                        Logger.Write($"Scotty, warm up the engines, were headed back...", LogLevel.Info);

                        PokeRoadieSettings.Current.CurrentLatitude = wayPointGeo.Latitude;
                        PokeRoadieSettings.Current.CurrentLongitude = wayPointGeo.Longitude;
                        PokeRoadieSettings.Current.CurrentAltitude = wayPointGeo.Altitude;
                        PokeRoadieSettings.Current.DestinationEndDate = DateTime.Now.AddSeconds(distanceFromStart / (PokeRoadieSettings.Current.MinSpeed / 3.6));
                        PokeRoadieSettings.Current.Save();

                        _client.SetLocation(wayPointGeo.Latitude, wayPointGeo.Longitude, wayPointGeo.Altitude);
                        await _client.Player.UpdatePlayerLocation(wayPointGeo.Latitude, wayPointGeo.Longitude, wayPointGeo.Altitude);
                        Logger.Write($"(WARP) You've been beamed! ({distanceFromStart}m instantly) ", LogLevel.None, ConsoleColor.Green);
                        gymTries.Clear();
                    }
                    else
                    {
                        Logger.Write($"Preparing for long distance travel - Boarded flight #{RandomHelper.RandomNumber(101, 501)}", LogLevel.Navigation, ConsoleColor.White);
                        inFlight = true;
                        await _navigation.HumanLikeWalking(wayPointGeo, PokeRoadieSettings.Current.FlyingSpeed, GetFlyingTask());
                        inFlight = false;
                        gymTries.Clear();
                    }

                }
                else
                {
                    Logger.Write("Preparing for long distance travel...", LogLevel.Navigation, ConsoleColor.White);
                    await _navigation.HumanLikeWalking(wayPointGeo, PokeRoadieSettings.Current.MinSpeed, GetLongWalkingTask());
                    gymTries.Clear();
                }
                Logger.Write($"Arrived at destination.", LogLevel.Navigation);
            }

            //if destinations are enabled
            if (PokeRoadieSettings.Current.DestinationsEnabled)
            {
                if (PokeRoadieSettings.Current.DestinationEndDate.HasValue)
                {
                    if (DateTime.Now > PokeRoadieSettings.Current.DestinationEndDate.Value)
                    {

                        if (PokeRoadieSettings.Current.Destinations != null && PokeRoadieSettings.Current.Destinations.Count > 1)
                        {
                            //get new destination index
                            var newIndex = PokeRoadieSettings.Current.DestinationIndex + 1 >= PokeRoadieSettings.Current.Destinations.Count ? 0 : PokeRoadieSettings.Current.DestinationIndex + 1;
                            //get coords
                            var destination = PokeRoadieSettings.Current.Destinations[newIndex];

                            //set new index and default location
                            PokeRoadieSettings.Current.DestinationIndex = newIndex;
                            PokeRoadieSettings.Current.CurrentLatitude = destination.Latitude;
                            PokeRoadieSettings.Current.CurrentLongitude = destination.Longitude;
                            PokeRoadieSettings.Current.CurrentAltitude = destination.Altitude;
                            PokeRoadieSettings.Current.DestinationEndDate = DateTime.Now.AddSeconds(distanceFromStart / (PokeRoadieSettings.Current.MinSpeed / 3.6));
                            PokeRoadieSettings.Current.Save();

                            OnChangeDestination?.Invoke(destination, newIndex);

                            if (PokeRoadieSettings.Current.FlyingEnabled)
                            {
                                if (PokeRoadieSettings.Current.FlyLikeCaptKirk)
                                {
                                    Logger.Write($"Scotty, warm up the engines, were headed back...", LogLevel.Info);
                                    _client.SetLocation(wayPointGeo.Latitude, destination.Longitude, destination.Altitude);
                                    await _client.Player.UpdatePlayerLocation(destination.Latitude, destination.Longitude, destination.Altitude);
                                    Logger.Write($"(WARP) You've been beamed! ({distanceFromStart}m instantly) ", LogLevel.None, ConsoleColor.Green);
                                    gymTries.Clear();
                                }
                                else
                                {
                                    Logger.Write($"Preparing for long distance travel - Boarded flight #{RandomHelper.RandomNumber(101, 501)}", LogLevel.Navigation, ConsoleColor.White);

                                    inFlight = true;
                                    await _navigation.HumanLikeWalking(destination.GetGeo(), PokeRoadieSettings.Current.FlyingSpeed, GetFlyingTask());
                                    inFlight = false;
                                    gymTries.Clear();
                                }

                            } 
                            else
                            {
                                Logger.Write("Preparing for long distance travel...", LogLevel.None, ConsoleColor.White);
                                await _navigation.HumanLikeWalking(destination.GetGeo(), PokeRoadieSettings.Current.MinSpeed, GetLongWalkingTask());
                                gymTries.Clear();
                            }
                            Logger.Write($"Moving to new destination - {destination.Name} - {destination.Latitude}:{destination.Longitude}", LogLevel.Navigation, ConsoleColor.White);

                            //reset destination timer
                            PokeRoadieSettings.Current.DestinationEndDate = DateTime.Now.AddMinutes(PokeRoadieSettings.Current.MinutesPerDestination);

                            Logger.Write($"Arrived at destination - {destination.Name}!", LogLevel.Navigation, ConsoleColor.White);
                        }
                        else
                        {
                            PokeRoadieSettings.Current.DestinationEndDate = DateTime.Now.AddMinutes(PokeRoadieSettings.Current.MinutesPerDestination);
                        }
                    }
                }
                else
                {
                    PokeRoadieSettings.Current.DestinationEndDate = DateTime.Now.AddMinutes(PokeRoadieSettings.Current.MinutesPerDestination);
                }
            }
            //await CheckDestinations();
            var location = new LocationData(_client.CurrentLatitude, _client.CurrentLongitude, _client.CurrentAltitude);

            var mapObjects = await _client.Map.GetMapObjects();

            var pokeStopList =
                PokeRoadieNavigation.pathByNearestNeighbour(
                mapObjects.MapCells.SelectMany(i => i.Forts)
                    .Where(i =>
                        i.CooldownCompleteTimestampMs < DateTime.UtcNow.ToUnixTime() &&
                        (PokeRoadieSettings.Current.MaxDistance == 0 ||
                        LocationUtils.CalculateDistanceInMeters(_client.CurrentLatitude, _client.CurrentLongitude, i.Latitude, i.Longitude) < PokeRoadieSettings.Current.MaxDistance))
                    .OrderBy(i =>
                        LocationUtils.CalculateDistanceInMeters(_client.CurrentLatitude, _client.CurrentLongitude, i.Latitude, i.Longitude)).ToArray()).ToList();

            //var fullPokestopList = PokeRoadieNavigation.pathByNearestNeighbour(
            //    mapObjects.MapCells.SelectMany(i => i.Forts)
            //        .Where(i => 
            //            (PokeRoadieSettings.Current.MaxDistance == 0 ||
            //            LocationUtils.CalculateDistanceInMeters(_client.CurrentLatitude, _client.CurrentLongitude, i.Latitude, i.Longitude) < PokeRoadieSettings.Current.MaxDistance))
            //        .OrderBy(i =>
            //            LocationUtils.CalculateDistanceInMeters(_client.CurrentLatitude, _client.CurrentLongitude, i.Latitude, i.Longitude)).ToArray()).ToList();

            //OnGetAllNearbyPokestops?.Invoke(location, fullPokestopList.Where(x => x.Type != FortType.Gym).ToList());
            //OnGetAllNearbyGyms?.Invoke(location, fullPokestopList.Where(x => x.Type == FortType.Gym).ToList());

            //var pokeStopList = fullPokestopList
            //        .Where(i => i.CooldownCompleteTimestampMs < DateTime.UtcNow.ToUnixTime()).ToList();

            if (!PokeRoadieSettings.Current.VisitGyms)
                pokeStopList = pokeStopList.Where(x => x.Type != FortType.Gym).ToList();

            if (!PokeRoadieSettings.Current.VisitPokestops)
                pokeStopList = pokeStopList.Where(x => x.Type == FortType.Gym).ToList();

            if (pokeStopList.Where(x => x.Type != FortType.Gym).Count() == 0) 
            {
                    if (pokeStopList.Where(x => x.Type == FortType.Gym).Count() > 0)
                    {
                        await ProcessPokeStopList(pokeStopList, mapObjects);
                     }
                    Logger.Write("No usable PokeStops found in your area.",
                                    LogLevel.Warning);
                    await Task.Delay(5000);

                if (PokeRoadieSettings.Current.MoveWhenNoStops && _client != null && PokeRoadieSettings.Current.DestinationEndDate.HasValue && PokeRoadieSettings.Current.DestinationEndDate.Value > DateTime.Now)
                    PokeRoadieSettings.Current.DestinationEndDate = DateTime.Now;
            }
            else
            {
                await ProcessPokeStopList(pokeStopList, mapObjects);
            }

            
        }

        private async Task ProcessPokeStopList(List<FortData> pokeStopList, GetMapObjectsResponse mapObjects)
        {

            if (pokeStopList.Count == 0) return;
            var pokestopCount = pokeStopList.Where(x => x.Type != FortType.Gym).Count();
            var gymCount = pokeStopList.Where(x => x.Type == FortType.Gym).Count();
            var lureCount = pokeStopList.Where(x => x.LureInfo != null).Count();

            Logger.Write($"Found{(inFlight ? " from the sky" : "")} {pokestopCount} {(pokestopCount == 1 ? "Pokestop" : "Pokestops")} | {gymCount} {(gymCount == 1 ? "Gym" : "Gyms")}", LogLevel.Info);
            if (lureCount > 0) Logger.Write($"(INFO) Found {lureCount} with lure!", LogLevel.None, ConsoleColor.DarkMagenta);

            if (lureCount > 0)
            {
                var pokestopListWithLures = pokeStopList.Where(x => x.LureInfo != null).ToList();
                if (pokestopListWithLures.Count > 0)
                {
            
                    //if we are prioritizing stops with lures
                    if (PokeRoadieSettings.Current.PrioritizeStopsWithLures)
                    {
                        int counter = 0;
                        for (int i = 0; i < 3; i++)
                        {
                            for (int x = 0; x < pokestopListWithLures.Count; x++)
                            {
                                pokeStopList.Insert(counter, pokestopListWithLures[x]);
                                counter++;
                            }
                        }
                    }
                }
            }


            OnVisitPokestops?.Invoke(new LocationData(_client.CurrentLatitude, _client.CurrentLongitude, _client.CurrentAltitude), pokeStopList);

            while (pokeStopList.Any())
            {
                if (!isRunning) break;

                if (!inFlight && PokeRoadieSettings.Current.DestinationsEnabled && PokeRoadieSettings.Current.DestinationEndDate.HasValue && DateTime.Now > PokeRoadieSettings.Current.DestinationEndDate.Value)
                {
                    break;
                }

                await WriteStats();
                await Export();

                var pokeStop = pokeStopList[0];
                pokeStopList.RemoveAt(0);
                if (pokeStop.Type != FortType.Gym)
                {
                    await ProcessPokeStop(pokeStop, mapObjects);
                }
                else
                {
                    await ProcessGym(pokeStop, mapObjects);
                }
                if (pokestopCount == 0 && gymCount > 0)
                    await RandomHelper.RandomDelay(1000, 2000);
                //else
                    //await RandomHelper.RandomDelay(50, 200);
            }

        }

        private static List<string> gymTries = new List<string>();

        private async Task ProcessGym(FortData pokeStop, GetMapObjectsResponse mapObjects)
        {
            if (!gymTries.Contains(pokeStop.Id))
            {
                var fortInfo = await _client.Fort.GetFort(pokeStop.Id, pokeStop.Latitude, pokeStop.Longitude);
                if (fortInfo != null)
                {
                    bool inRange = false;
                    int attempts = 0;

                    do
                    {
                        if (!isRunning) break;
                        var distance = LocationUtils.CalculateDistanceInMeters(_client.CurrentLatitude, _client.CurrentLongitude, pokeStop.Latitude, pokeStop.Longitude);
                        var fortDetails = await _client.Fort.GetGymDetails(pokeStop.Id, pokeStop.Latitude, pokeStop.Longitude);
                        if (fortDetails.Result == GetGymDetailsResponse.Types.Result.Success)
                        {
                            inRange = true;
                            var fortString = $"{ fortDetails.Name} | { fortDetails.GymState.FortData.OwnedByTeam } | { pokeStop.GymPoints} | { fortDetails.GymState.Memberships.Count}";
                            if (fortDetails.Result == GetGymDetailsResponse.Types.Result.Success)
                            {
                                var gymDir = Path.Combine(Directory.GetCurrentDirectory(), "Gyms");
                                fortDetails.Save(fortInfo, Path.Combine(gymDir, fortInfo.FortId + ".xml"));

                                if (fortDetails.GymState.FortData.OwnedByTeam == _playerProfile.PlayerData.Team &&
                                    ((pokeStop.GymPoints < 2001 && fortDetails.GymState.Memberships.Count < 2) ||
                                    (pokeStop.GymPoints > 2000 && pokeStop.GymPoints < 7001 && fortDetails.GymState.Memberships.Count < 2) ||
                                    (pokeStop.GymPoints > 7000 && pokeStop.GymPoints < 10001 && fortDetails.GymState.Memberships.Count < 3) ||
                                    (pokeStop.GymPoints > 10000 && fortDetails.GymState.Memberships.Count < 4)))
                                {

                                    Logger.Write($"(GYM) Casing out {fortDetails.Name} in {distance:0.##} m distance", LogLevel.None, ConsoleColor.Cyan);
                                    var update = await _navigation.HumanLikeWalking(new GeoCoordinate(pokeStop.Latitude, pokeStop.Longitude), PokeRoadieSettings.Current.MinSpeed, GetShortWalkingTask());

                                    fortInfo = await _client.Fort.GetFort(pokeStop.Id, pokeStop.Latitude, pokeStop.Longitude);
                                    fortDetails = await _client.Fort.GetGymDetails(pokeStop.Id, pokeStop.Latitude, pokeStop.Longitude);
                                    fortString = $"{ fortDetails.Name} | { fortDetails.GymState.FortData.OwnedByTeam } | { pokeStop.GymPoints} | { fortDetails.GymState.Memberships.Count}";


                                    if (fortDetails.GymState.FortData.OwnedByTeam == _playerProfile.PlayerData.Team)
                                    {

                                        await PokeRoadieInventory.getCachedInventory(_client, true);
                                        var pokemonList = await _inventory.GetHighestsVNotDeployed(1);
                                        var pokemon = pokemonList.FirstOrDefault();
                                        if (pokemon != null)
                                        {

                                            var response = await _client.Fort.FortDeployPokemon(fortInfo.FortId, pokemon.Id);
                                            if (response.Result == FortDeployPokemonResponse.Types.Result.Success)
                                            {
                                                Logger.Write($"(GYM) Deployed {pokemon.GetMinStats()} to {fortDetails.Name}", LogLevel.None, ConsoleColor.Green);
                                            }
                                            else if (response.Result == FortDeployPokemonResponse.Types.Result.ErrorPokemonNotFullHp)
                                            {
                                                var figureThisShitOut = pokemon;
                                            }
                                            else
                                            {
                                                //Logger.Write($"(GYM) Deployment Failed at {fortString} - {response.Result}", LogLevel.None, ConsoleColor.Green);
                                            }
                                        }
                                    }
                                    else
                                    {
                                        Logger.Write($"(GYM) Wasted walk on {fortString}", LogLevel.None, ConsoleColor.Cyan);
                                    }
                                }
                                else
                                {
                                    Logger.Write($"(GYM) Ignoring {fortString}", LogLevel.None, ConsoleColor.Cyan);
                                }
                            }
                        }
                        else if (fortDetails.Result == GetGymDetailsResponse.Types.Result.ErrorNotInRange)
                        {
                            attempts++;
                            Logger.Write($"(GYM) Moving closer to {fortInfo.Name} - {fortDetails.Result}", LogLevel.None, ConsoleColor.Cyan);
                            var ToStart = await _navigation.HumanLikeWalkingGetCloser(
                                new GeoCoordinate(pokeStop.Latitude, pokeStop.Longitude, _client.CurrentAltitude),
                                PokeRoadieSettings.Current.FlyingEnabled ? PokeRoadieSettings.Current.FlyingSpeed : PokeRoadieSettings.Current.MinSpeed, GetShortWalkingTask(), 0.20);

                        }
                        else
                        {
                            Logger.Write($"(GYM) Ignoring {fortInfo.Name} - {fortDetails.Result}", LogLevel.None, ConsoleColor.Cyan);
                            inRange = true;
                        }

                    } while (!inRange && attempts < 6);
                }
                gymTries.Add(pokeStop.Id);
            }
           
        }

        private async Task ProcessPokeStop(FortData pokeStop, GetMapObjectsResponse mapObjects)
        {

            var pokestopsDir = Path.Combine(Directory.GetCurrentDirectory(), "Pokestops");
            if (PokeRoadieSettings.Current.CatchPokemon && !softBan)
                await CatchNearbyPokemons();

            var distance = LocationUtils.CalculateDistanceInMeters(_client.CurrentLatitude, _client.CurrentLongitude, pokeStop.Latitude, pokeStop.Longitude);
            
            //get fort info
            var fortInfo = await _client.Fort.GetFort(pokeStop.Id, pokeStop.Latitude, pokeStop.Longitude);
            fortInfo.Save(Path.Combine(pokestopsDir, pokeStop.Id + ".xml"));

            OnTravelingToPokestop?.Invoke(new LocationData(_client.CurrentLatitude, _client.CurrentLongitude, _client.CurrentAltitude), fortInfo);

            var name = $"{(inFlight ? "Set target lock on " : "")}{fortInfo.Name}{(pokeStop.LureInfo == null ? "" : " WITH LURE")} in {distance:0.##} m distance";
            Logger.Write(name, LogLevel.Pokestop);

            if (inFlight)
            {
                await _navigation.HumanLikeWalking(new GeoCoordinate(pokeStop.Latitude, pokeStop.Longitude), PokeRoadieSettings.Current.FlyingSpeed, null);
            }
            else
            {
                await _navigation.HumanLikeWalking(new GeoCoordinate(pokeStop.Latitude, pokeStop.Longitude), PokeRoadieSettings.Current.MinSpeed, GetShortWalkingTask());
            }

            //search fort
            var fortSearch = await _client.Fort.SearchFort(pokeStop.Id, pokeStop.Latitude, pokeStop.Longitude);

            OnVisitPokestop?.Invoke(new LocationData(_client.CurrentLatitude, _client.CurrentLongitude, _client.CurrentAltitude), fortInfo, fortSearch);

            if (fortSearch.ExperienceAwarded > 0)
            {
                _stats.AddExperience(fortSearch.ExperienceAwarded);
                _stats.UpdateConsoleTitle(_client, _inventory);
                string EggReward = fortSearch.PokemonDataEgg != null ? "1" : "0";

                //a little comedy never hurt
                if (inFlight)
                {
                    var rnd = RandomHelper.RandomNumber(0, 5);
                    switch (rnd)
                    {
                        case 0:
                            Logger.Write("Taking it into a dive...", LogLevel.None, ConsoleColor.Blue);
                            break;
                        case 1:
                            Logger.Write("Barrel roll pickup!", LogLevel.None, ConsoleColor.Blue);
                            break;
                        case 2:
                            Logger.Write("That was a close one...", LogLevel.None, ConsoleColor.Blue);
                            break;
                        case 3:
                            Logger.Write("Bringing us down...", LogLevel.None, ConsoleColor.Blue);
                            break;
                        case 4:
                            Logger.Write("Totally inverted!", LogLevel.None, ConsoleColor.Blue);
                            break;
                        default:
                            Logger.Write("Tower, Requesting a fly-by...", LogLevel.None, ConsoleColor.Blue);
                            break;
                    }
                }

                if (!softBan) Logger.Write($"XP: {fortSearch.ExperienceAwarded}, Gems: {fortSearch.GemsAwarded}, Eggs: {EggReward}, Items: {StringUtils.GetSummedFriendlyNameOfItemAwardList(fortSearch.ItemsAwarded)}", LogLevel.Pokestop);
                recycleCounter++;

                //reset ban
                if (softBan)
                {
                    var diff = DateTime.Now.Subtract(fleeStart.Value).ToString();
                    softBan = false;
                    fleeCounter = 0;
                    fleeLast = null;
                    fleeStart = null;
                    Logger.Write($"(SOFT BAN) The ban was lifted after {diff}!", LogLevel.None, ConsoleColor.DarkRed);
                }

            }
            else if (fortSearch.Result == FortSearchResponse.Types.Result.Success)
            {
                fleeCounter++;
                if (fleeLast.HasValue && fleeLast.Value.AddMinutes(3) > DateTime.Now && fleeCounter > 3 && !softBan)
                {
                    softBan = true;
                    fleeStart = DateTime.Now;
                    Logger.Write("(SOFT BAN) Detected a soft ban, let's chill out a moment.", LogLevel.None, ConsoleColor.DarkRed);
                }

                fleeLast = DateTime.Now;
                fleeLast = DateTime.Now;
            }

            //catch lure pokemon 8)
            if (PokeRoadieSettings.Current.LoiteringActive && pokeStop.LureInfo != null)
            {
                var encounter = await _client.Encounter.EncounterLurePokemon(pokeStop.LureInfo.EncounterId, pokeStop.LureInfo.FortId);
                if (encounter.Result == DiskEncounterResponse.Types.Result.Success)
                    await LureCatchEncounter(pokeStop.LureInfo, encounter, pokeStop.LureInfo.EncounterId);
            }

            if (PokeRoadieSettings.Current.LoiteringActive && pokeStop.LureInfo != null)
            {
                Logger.Write($"Loitering: {fortInfo.Name} has a lure we can milk!", LogLevel.Info);
                while (PokeRoadieSettings.Current.LoiteringActive && pokeStop.LureInfo != null)
                {
                    if (PokeRoadieSettings.Current.CatchPokemon && !softBan)
                        await CatchNearbyPokemons();

                    //catch lure pokemon 8)
                    if (PokeRoadieSettings.Current.LoiteringActive && pokeStop.LureInfo != null)
                    {
                        var encounter = await _client.Encounter.EncounterLurePokemon(pokeStop.LureInfo.EncounterId, pokeStop.LureInfo.FortId);
                        if (encounter.Result == DiskEncounterResponse.Types.Result.Success)
                            await LureCatchEncounter(pokeStop.LureInfo, encounter, pokeStop.LureInfo.EncounterId);
                    }

                    var fortSearch2 = await _client.Fort.SearchFort(pokeStop.Id, pokeStop.Latitude, pokeStop.Longitude);
                    if (fortSearch2.ExperienceAwarded > 0)
                    {
                        _stats.AddExperience(fortSearch2.ExperienceAwarded);
                        _stats.UpdateConsoleTitle(_client, _inventory);
                        string EggReward = fortSearch2.PokemonDataEgg != null ? "1" : "0";
                        Logger.Write($"XP: {fortSearch2.ExperienceAwarded}, Gems: {fortSearch2.GemsAwarded}, Eggs: {EggReward}, Items: {StringUtils.GetSummedFriendlyNameOfItemAwardList(fortSearch2.ItemsAwarded)}", LogLevel.Pokestop);
                        recycleCounter++;
                    }

                    if (recycleCounter >= 5)
                        await RecycleItems();

                    await RandomHelper.RandomDelay(15000, 30000);
                    pokeStop = mapObjects.MapCells.SelectMany(i => i.Forts).Where(x => x.Id == pokeStop.Id).FirstOrDefault();
                    if (pokeStop.LureInfo != null) Logger.Write($"Loitering: {fortInfo.Name} still has a lure, chillin out!", LogLevel.Info);
                }
            }

            //await RandomHelper.RandomDelay(50, 200);
            if (recycleCounter >= 5)
                await RecycleItems();
        }

        private async Task CatchEncounter(EncounterResponse encounter, MapPokemon pokemon)
        {
            CatchPokemonResponse caughtPokemonResponse;
            var attemptCounter = 1;
            OnEncounter?.Invoke(pokemon, _client.CurrentAltitude);
            do
            {
                if (!isRunning) break;
                //if there has not been a consistent flee, reset
                if (fleeCounter > 0 && fleeLast.HasValue && fleeLast.Value.AddMinutes(3) < DateTime.Now && !softBan)
                {
                    fleeStart = null;
                    fleeCounter = 0;
                    fleeLast = null;
                }

                var probability = encounter?.CaptureProbability?.CaptureProbability_?.FirstOrDefault();
                var bestPokeball = await GetBestBall(encounter);
                if (bestPokeball == ItemId.ItemUnknown)
                {
                    Logger.Write($"You don't own any Pokeballs :( - We missed a {pokemon.PokemonId} with CP {encounter?.WildPokemon?.PokemonData?.Cp}", LogLevel.Warning);
                    return;
                }

                //only use crappy pokeballs when they are fleeing
                if (fleeCounter > 1) bestPokeball = ItemId.ItemPokeBall;

                var bestBerry = await GetBestBerry(encounter);
                //only use berries when they are fleeing
                if (fleeCounter == 0)
                {
                    var inventoryBerries = await _inventory.GetItems();
                    var berries = inventoryBerries.Where(p => p.ItemId == bestBerry).FirstOrDefault();
                    if (bestBerry != ItemId.ItemUnknown && probability.HasValue && probability.Value < 0.35)
                    {
                        await _client.Encounter.UseCaptureItem(pokemon.EncounterId, bestBerry, pokemon.SpawnPointId);
                        berries.Count--;
                        Logger.Write($"{bestBerry} used, remaining: {berries.Count}", LogLevel.Berry);
                        await RandomHelper.RandomDelay(50, 200);
                    }
                }

                var distance = LocationUtils.CalculateDistanceInMeters(_client.CurrentLatitude, _client.CurrentLongitude, pokemon.Latitude, pokemon.Longitude);
                caughtPokemonResponse = await _client.Encounter.CatchPokemon(pokemon.EncounterId, pokemon.SpawnPointId, bestPokeball);

                if (caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchSuccess)
                {
                    fleeCounter = 0;
                    fleeLast = null;
                    fleeStart = null;
                    //reset soft ban info
                    if (softBan)
                    {
                        var diff = DateTime.Now.Subtract(fleeStart.Value).ToString();
                        softBan = false;
                        Logger.Write($"(SOFT BAN) The ban was lifted after {diff}!", LogLevel.None, ConsoleColor.DarkRed);
                    }

                    foreach (var xp in caughtPokemonResponse.CaptureAward.Xp)
                        _stats.AddExperience(xp);
                    _stats.IncreasePokemons();
                    var profile = await _client.Player.GetPlayer();
                    _stats.GetStardust(profile.PlayerData.Currencies.ToArray()[1].Amount);

                    if (OnCatch != null)
                    {
                        OnCatch(pokemon, caughtPokemonResponse, _client.CurrentAltitude);
                    }
                }
                else if (caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchFlee)
                {
                    fleeCounter++;
                    if (fleeLast.HasValue && fleeLast.Value.AddMinutes(3) > DateTime.Now && fleeCounter > 3 && !softBan)
                    {
                        softBan = true;
                        fleeStart = DateTime.Now;
                        Logger.Write("(SOFT BAN) Detected a soft ban, let's chill out a moment.", LogLevel.None, ConsoleColor.DarkRed);

                    }
                    fleeLast = DateTime.Now;
                    OnCatchAttempt?.Invoke(pokemon, caughtPokemonResponse, _client.CurrentAltitude);
                }
                else 
                {
                    OnCatchAttempt?.Invoke(pokemon, caughtPokemonResponse, _client.CurrentAltitude);
                }


                if (encounter?.CaptureProbability?.CaptureProbability_ != null)
                {
                    Func<ItemId, string> returnRealBallName = a =>
                    {
                        switch (a)
                        {
                            case ItemId.ItemPokeBall:
                                return "Poke";
                            case  ItemId.ItemGreatBall:
                                return "Great";
                            case ItemId.ItemUltraBall:
                                return "Ultra";
                            case ItemId.ItemMasterBall:
                                return "Master";
                            default:
                                return "Unknown";
                        }
                    };
                    var catchStatus = attemptCounter > 1
                        ? $"{caughtPokemonResponse.Status} Attempt #{attemptCounter}"
                        : $"{caughtPokemonResponse.Status}";

                    string receivedXP = catchStatus == "CatchSuccess" 
                        ? $"and received XP {caughtPokemonResponse.CaptureAward.Xp.Sum()}" 
                        : $"";

                    Logger.Write($"({catchStatus}) | {encounter?.WildPokemon?.PokemonData.GetMinStats()} | Chance: {(float)((int)(encounter?.CaptureProbability?.CaptureProbability_.First() * 100)) / 100} | {Math.Round(distance)}m dist | with a {returnRealBallName(bestPokeball)}Ball {receivedXP}", LogLevel.Pokemon);
                }

                attemptCounter++;
                await RandomHelper.RandomDelay(300, 400);
            }
            while (caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchMissed || caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchEscape);
        }

        private async Task LureCatchEncounter(FortLureInfo lureInfo, DiskEncounterResponse encounter, ulong encounterId)
        {
            CatchPokemonResponse caughtPokemonResponse;
            var attemptCounter = 1;
            var pokemon = encounter.PokemonData;
            OnLureEncounter?.Invoke(new LocationData(_client.CurrentLatitude, _client.CurrentLongitude, _client.CurrentAltitude), pokemon, encounter);
            do
            {
                if (!isRunning) break;
                //if there has not been a consistent flee, reset
                if (fleeCounter > 0 && fleeLast.HasValue && fleeLast.Value.AddMinutes(3) < DateTime.Now && !softBan)
                {
                    fleeStart = null;
                    fleeCounter = 0;
                    fleeLast = null;
                }

                var probability = encounter?.CaptureProbability?.CaptureProbability_?.FirstOrDefault();
                var bestPokeball = await GetBestBall(encounter?.PokemonData, encounter?.CaptureProbability?.CaptureProbability_?.First());
                if (bestPokeball == ItemId.ItemUnknown)
                {
                    Logger.Write($"You don't own any Pokeballs :( - We missed a {pokemon.PokemonId} with CP {encounter?.PokemonData?.Cp}", LogLevel.Warning);
                    return;
                }

                //only use crappy pokeballs when they are fleeing
                if (fleeCounter > 1) bestPokeball = ItemId.ItemPokeBall;

                var bestBerry = await GetBestBerry(encounter?.PokemonData, encounter?.CaptureProbability?.CaptureProbability_?.First());
                //only use berries when they are fleeing
                if (fleeCounter == 0)
                {
                    var inventoryBerries = await _inventory.GetItems();
                    var berries = inventoryBerries.Where(p => p.ItemId == bestBerry).FirstOrDefault();
                    if (bestBerry != ItemId.ItemUnknown && probability.HasValue && probability.Value < 0.35)
                    {
                        await _client.Encounter.UseCaptureItem(encounterId, bestBerry, lureInfo.FortId);
                        berries.Count--;
                        Logger.Write($"{bestBerry} used, remaining: {berries.Count}", LogLevel.Berry);
                        await RandomHelper.RandomDelay(50, 200);
                    }
                }

                caughtPokemonResponse = await _client.Encounter.CatchPokemon(encounterId, lureInfo.FortId, bestPokeball);

                if (caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchSuccess)
                {
                    fleeCounter = 0;
                    fleeLast = null;
                    fleeStart = null;
                    //reset soft ban info
                    if (softBan)
                    {
                        var diff = DateTime.Now.Subtract(fleeStart.Value).ToString();
                        softBan = false;
                        Logger.Write($"(SOFT BAN) The ban was lifted after {diff}!", LogLevel.None, ConsoleColor.DarkRed);
                    }

                    foreach (var xp in caughtPokemonResponse.CaptureAward.Xp)
                        _stats.AddExperience(xp);
                    _stats.IncreasePokemons();
                    var profile = await _client.Player.GetPlayer();
                    _stats.GetStardust(profile.PlayerData.Currencies.ToArray()[1].Amount);

                    if (OnCatch != null)
                    {
                        OnLureCatch(new LocationData(_client.CurrentLatitude, _client.CurrentLongitude, _client.CurrentAltitude), pokemon, caughtPokemonResponse);
                    }
                }
                else if (caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchFlee)
                {
                    fleeCounter++;
                    if (fleeLast.HasValue && fleeLast.Value.AddMinutes(3) > DateTime.Now && fleeCounter > 3 && !softBan)
                    {
                        softBan = true;
                        fleeStart = DateTime.Now;
                        Logger.Write("(SOFT BAN) Detected a soft ban, let's chill out a moment.", LogLevel.None, ConsoleColor.DarkRed);

                    }
                    fleeLast = DateTime.Now;
                    OnLureCatchAttempt?.Invoke(new LocationData(_client.CurrentLatitude, _client.CurrentLongitude, _client.CurrentAltitude), pokemon, caughtPokemonResponse);
                }
                else
                {
                    OnLureCatchAttempt?.Invoke(new LocationData(_client.CurrentLatitude, _client.CurrentLongitude, _client.CurrentAltitude), pokemon, caughtPokemonResponse);
                }


                if (encounter?.CaptureProbability?.CaptureProbability_ != null)
                {
                    Func<ItemId, string> returnRealBallName = a =>
                    {
                        switch (a)
                        {
                            case ItemId.ItemPokeBall:
                                return "Poke";
                            case ItemId.ItemGreatBall:
                                return "Great";
                            case ItemId.ItemUltraBall:
                                return "Ultra";
                            case ItemId.ItemMasterBall:
                                return "Master";
                            default:
                                return "Unknown";
                        }
                    };
                    var catchStatus = attemptCounter > 1
                        ? $"{caughtPokemonResponse.Status} Attempt #{attemptCounter}"
                        : $"{caughtPokemonResponse.Status}";

                    string receivedXP = catchStatus == "CatchSuccess"
                        ? $"and received XP {caughtPokemonResponse.CaptureAward.Xp.Sum()}"
                        : $"";

                    Logger.Write($"({catchStatus}) | {encounter?.PokemonData.GetMinStats()} | Chance: {(float)((int)(encounter?.CaptureProbability?.CaptureProbability_.First() * 100)) / 100} | with a {returnRealBallName(bestPokeball)}Ball {receivedXP}", LogLevel.Pokemon);
                }

                attemptCounter++;
                await RandomHelper.RandomDelay(300, 400);
            }
            while (caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchMissed || caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchEscape);
        }

        private async Task CatchNearbyPokemonsAndStops()
        {
            await CatchNearbyPokemonsAndStops(false);
        }
        private async Task GpxCatchNearbyPokemonsAndStops()
        {
            await CatchNearbyPokemons();
            await CatchNearbyStops(true);
        }
        private async Task CatchNearbyPokemonsAndStops(bool path)
        {
            await CatchNearbyPokemons();
            await CatchNearbyStops(path);
        }
        private async Task CatchNearbyStops()
        {
            await CatchNearbyStops(false);
        }
        private async Task GpxCatchNearbyStops()
        {
            await CatchNearbyStops(true);
        }
        private async Task CatchNearbyStops(bool path)
        {
            //if (path)
            //{
            //    var i = 1;
            //}
            var mapObjects = await _client.Map.GetMapObjects();

            var pokeStopList =
                PokeRoadieNavigation.pathByNearestNeighbour(
                mapObjects.MapCells.SelectMany(i => i.Forts)
                    
                .Where(i =>

                    (i.CooldownCompleteTimestampMs < DateTime.UtcNow.ToUnixTime()) &&
                    path ? LocationUtils.CalculateDistanceInMeters(_client.CurrentLatitude, _client.CurrentLongitude, i.Latitude, i.Longitude) < 40 :
                    PokeRoadieSettings.Current.MaxDistance == 0 || LocationUtils.CalculateDistanceInMeters(_client.CurrentLatitude, _client.CurrentLongitude, i.Latitude, i.Longitude) < PokeRoadieSettings.Current.MaxDistance)
                    
                .OrderBy(i =>

                    LocationUtils.CalculateDistanceInMeters(_client.CurrentLatitude, _client.CurrentLongitude, i.Latitude, i.Longitude)).ToArray()).ToList();

            if (!PokeRoadieSettings.Current.VisitGyms)
                pokeStopList = pokeStopList.Where(x => x.Type != FortType.Gym).ToList();

            if (!PokeRoadieSettings.Current.VisitPokestops)
                pokeStopList = pokeStopList.Where(x => x.Type == FortType.Gym).ToList();
            if (pokeStopList.Count > 0) await ProcessPokeStopList(pokeStopList, mapObjects);
        }

        private async Task CatchNearbyPokemons()
        {
            var mapObjects = await _client.Map.GetMapObjects();

            var pokemons =
                mapObjects.MapCells.SelectMany(i => i.CatchablePokemons)
                .OrderBy(
                    i =>
                    LocationUtils.CalculateDistanceInMeters(_client.CurrentLatitude, _client.CurrentLongitude, i.Latitude, i.Longitude));

            if (PokeRoadieSettings.Current.UsePokemonToNotCatchList)
            {
                ICollection<PokemonId> filter = PokeRoadieSettings.Current.PokemonsNotToCatch;
                pokemons = mapObjects.MapCells.SelectMany(i => i.CatchablePokemons).Where(p => !filter.Contains(p.PokemonId)).OrderBy(i => LocationUtils.CalculateDistanceInMeters(_client.CurrentLatitude, _client.CurrentLongitude, i.Latitude, i.Longitude));
            }

            if (pokemons != null && pokemons.Any())
                Logger.Write($"Found {pokemons.Count()} catchable Pokemon", LogLevel.Info);
            else
                return;

            foreach (var pokemon in pokemons)
            {
                if (!isRunning) break;
                var distance = LocationUtils.CalculateDistanceInMeters(_client.CurrentLatitude, _client.CurrentLongitude, pokemon.Latitude, pokemon.Longitude);

                await RandomHelper.RandomDelay(220, 320);

                var encounter = await _client.Encounter.EncounterPokemon(pokemon.EncounterId, pokemon.SpawnPointId);

                if (encounter.Status == EncounterResponse.Types.Status.EncounterSuccess)
                    await CatchEncounter(encounter, pokemon);
                else if (encounter.Status == EncounterResponse.Types.Status.PokemonInventoryFull)
                {

                    if (PokeRoadieSettings.Current.TransferPokemon && PokeRoadieSettings.Current.TransferTrimFatCount > 0)
                    {
                        Logger.Write($"Pokemon inventory full, trimming the fat...", LogLevel.Info);
                        var query = (await _inventory.GetPokemons()).Where(x => string.IsNullOrWhiteSpace(x.DeployedFortId));

                        //ordering
                        switch (PokeRoadieSettings.Current.TransferPriorityType)
                        {
                            case PriorityTypes.CP:
                                query = query.OrderBy(x => x.Cp)
                                             .ThenBy(x => x.Stamina);
                                break;
                            case PriorityTypes.IV:
                                query = query.OrderBy(PokemonInfo.CalculatePokemonPerfection)
                                             .ThenBy(n => n.StaminaMax);
                                break;
                            default:
                                query = query.OrderBy(x => x.CalculatePokemonValue())
                                             .ThenBy(n => n.StaminaMax);
                                break;
                        }

                        await TransferPokemon(query.Take(PokeRoadieSettings.Current.TransferTrimFatCount).ToList());
                    }
                }
               
                else if (encounter.Status == EncounterResponse.Types.Status.EncounterPokemonFled)
                {
                    fleeCounter++;
                    if (fleeLast.HasValue && fleeLast.Value.AddMinutes(3) > DateTime.Now && fleeCounter > 3 && !softBan)
                    {
                        softBan = true;
                        fleeStart = DateTime.Now;
                        Logger.Write("(SOFT BAN) Detected a soft ban, let's chill out a moment.", LogLevel.None, ConsoleColor.DarkRed);

                    }
                    fleeLast = DateTime.Now;
                }
                else Logger.Write($"Encounter problem: {encounter.Status}", LogLevel.Warning);

                if (!Equals(pokemons.ElementAtOrDefault(pokemons.Count() - 1), pokemon))
                    // If pokemon is not last pokemon in list, create delay between catches, else keep moving.
                    await RandomHelper.RandomDelay(220, 320);
            }

            //revive
            if (PokeRoadieSettings.Current.UseRevives) await RevivePokemon();

            //heal
            if (PokeRoadieSettings.Current.UsePotions) await HealPokemon();

            //evolve
            if (PokeRoadieSettings.Current.EvolvePokemon) await EvolvePokemon();

            //trasnfer
            if (PokeRoadieSettings.Current.TransferPokemon) await TransferPokemon();
        }

        private async Task EvolvePokemon()
        {
            await PokeRoadieInventory.getCachedInventory(_client, true);
            var pokemonToEvolve = await _inventory.GetPokemonToEvolve();
            if (pokemonToEvolve != null && pokemonToEvolve.Any())
            {
                Logger.Write($"Found {pokemonToEvolve.Count()} Pokemon for Evolve:", LogLevel.Info);
                if (PokeRoadieSettings.Current.UseLuckyEggs)
                    await UseLuckyEgg();
            }

            foreach (var pokemon in pokemonToEvolve)
            {
                var evolvePokemonOutProto = await _client.Inventory.EvolvePokemon((ulong)pokemon.Id);

                Logger.Write(
                    evolvePokemonOutProto.Result == EvolvePokemonResponse.Types.Result.Success
                        ? $"{pokemon.PokemonId} successfully for {evolvePokemonOutProto.ExperienceAwarded} xp"
                        : $"Failed: {pokemon.PokemonId}. EvolvePokemonOutProto.Result was {evolvePokemonOutProto.Result}, stopping evolving {pokemon.PokemonId}"
                    , LogLevel.Evolve);

                await RandomHelper.RandomDelay(220, 320);
            }
        }

        private async Task TransferPokemon(IEnumerable<PokemonData> duplicatePokemons)
        {
            if (duplicatePokemons != null && duplicatePokemons.Any())
            {
                Logger.Write($"Found {duplicatePokemons.Count()} pokemon to transfer...", LogLevel.Info);
                foreach (var duplicatePokemon in duplicatePokemons)
                {
                    if (!isRunning) break;
                    await _client.Inventory.TransferPokemon(duplicatePokemon.Id);

                    await PokeRoadieInventory.getCachedInventory(_client, true);
                    var myPokemonSettings = await _inventory.GetPokemonSettings();
                    var pokemonSettings = myPokemonSettings.ToList();
                    var myPokemonFamilies = await _inventory.GetPokemonFamilies();
                    var pokemonFamilies = myPokemonFamilies.ToArray();
                    var settings = pokemonSettings.Single(x => x.PokemonId == duplicatePokemon.PokemonId);
                    var familyCandy = pokemonFamilies.Single(x => settings.FamilyId == x.FamilyId);
                    var FamilyCandies = $"{familyCandy.Candy_}";

                    _stats.IncreasePokemonsTransfered();
                    _stats.UpdateConsoleTitle(_client, _inventory);

                    PokemonData bestPokemonOfType = null;
                    switch (PokeRoadieSettings.Current.TransferPriorityType)
                    {
                        case PriorityTypes.CP:
                            bestPokemonOfType = await _inventory.GetHighestPokemonOfTypeByCP(duplicatePokemon);
                            break;
                        case PriorityTypes.IV:
                            bestPokemonOfType = await _inventory.GetHighestPokemonOfTypeByIV(duplicatePokemon);
                            break;
                        default:
                            bestPokemonOfType = await _inventory.GetHighestPokemonOfTypeByV(duplicatePokemon);
                            break;
                    }

                    string bestPokemonInfo = "NONE";
                    if (bestPokemonOfType != null)
                        bestPokemonInfo = $"CP: {bestPokemonOfType.Cp}/{PokemonInfo.CalculateMaxCP(bestPokemonOfType)} | IV: {PokemonInfo.CalculatePokemonPerfection(bestPokemonOfType).ToString("0.00")}% perfect";
                    Logger.Write($"{duplicatePokemon.PokemonId} [CP {duplicatePokemon.Cp}/{PokemonInfo.CalculateMaxCP(duplicatePokemon)} | IV: { PokemonInfo.CalculatePokemonPerfection(duplicatePokemon).ToString("0.00")}% perfect] | Best: [{bestPokemonInfo}] | Family Candies: {FamilyCandies}", LogLevel.Transfer);
                }
            }
        }

        private async Task TransferPokemon()
        {
            await PokeRoadieInventory.getCachedInventory(_client, true);
            var duplicatePokemons = await _inventory.GetPokemonToTransfer();
            await TransferPokemon(duplicatePokemons);

        }

        private async Task RecycleItems()
        {
            await PokeRoadieInventory.getCachedInventory(_client, true);
            var items = await _inventory.GetItemsToRecycle(PokeRoadieSettings.Current);
            if (items != null && items.Any())
                Logger.Write($"Found {items.Count()} Recyclable {(items.Count() == 1 ? "Item" : "Items")}:", LogLevel.Info);

            foreach (var item in items)
            {
                if (!isRunning) break;
                await _client.Inventory.RecycleItem(item.ItemId, item.Count);
                Logger.Write($"{(item.ItemId).ToString().Replace("Item", "")} x {item.Count}", LogLevel.Recycling);

                _stats.AddItemsRemoved(item.Count);
                _stats.UpdateConsoleTitle(_client, _inventory);

                //await RandomHelper.RandomDelay(220, 320);
            }
            recycleCounter = 0;
        }

        private async Task<ItemId> GetBestPotion(PokemonData pokemon)
        {
            if (pokemon == null) return ItemId.ItemUnknown;
            if (pokemon.Stamina == pokemon.StaminaMax) return ItemId.ItemUnknown;

            var items = await _inventory.GetItems();
            if (pokemon.Stamina < 1)
            {
                var revive = items.Where(x => x.ItemId == ItemId.ItemRevive).FirstOrDefault();
                var maxRevive = items.Where(x => x.ItemId == ItemId.ItemMaxRevive).FirstOrDefault();

                var totalCount =
                    (revive == null ? 0 : revive.Count) +
                    (maxRevive == null ? 0 : maxRevive.Count);

                //count check
                if (totalCount == 0) return ItemId.ItemUnknown;

                //percentage check
                double perc = ((double)pokemon.Stamina / (double)pokemon.StaminaMax) * 100;
                if (perc >= 90) return ItemId.ItemUnknown;

                //any
                if (maxRevive != null && maxRevive.Count > 0)
                    return ItemId.ItemMaxRevive;
                if (revive != null && revive.Count > 0)
                    return ItemId.ItemRevive;

                //none
                return ItemId.ItemUnknown;
            }
            else
            {
                var potion = items.Where(x => x.ItemId == ItemId.ItemPotion).FirstOrDefault();
                var superPotion = items.Where(x => x.ItemId == ItemId.ItemSuperPotion).FirstOrDefault();
                var hyperPotion = items.Where(x => x.ItemId == ItemId.ItemHyperPotion).FirstOrDefault();
                var maxPotion = items.Where(x => x.ItemId == ItemId.ItemMaxPotion).FirstOrDefault();

                var totalCount =
                    (potion == null ? 0 : potion.Count) +
                    (superPotion == null ? 0 : superPotion.Count) +
                    (hyperPotion == null ? 0 : hyperPotion.Count) +
                    (maxPotion == null ? 0 : maxPotion.Count);

                //count check
                if (totalCount == 0) return ItemId.ItemUnknown;

                //percentage check
                double perc = ((double)pokemon.Stamina / (double)pokemon.StaminaMax) * 100;
                if (perc >= 90) return ItemId.ItemUnknown;

                //get difference
                var diff = pokemon.StaminaMax - pokemon.Stamina;

                //get best potion
                if (potion != null && potion.Count > 0 && diff < 21)
                    return ItemId.ItemPotion;
                if (superPotion != null && superPotion.Count > 0 && diff < 51)
                    return ItemId.ItemSuperPotion;
                if (hyperPotion != null && hyperPotion.Count > 0 && diff < 201)
                    return ItemId.ItemHyperPotion;
                if (maxPotion != null && maxPotion.Count > 0)
                    return ItemId.ItemMaxPotion;

                //upgrade
                if (superPotion != null && superPotion.Count > 0 && diff < 21)
                    return ItemId.ItemSuperPotion;
                if (hyperPotion != null && hyperPotion.Count > 0 && diff < 51)
                    return ItemId.ItemHyperPotion;
                if (maxPotion != null && maxPotion.Count > 0 && diff < 201)
                    return ItemId.ItemMaxPotion;

                //downgrade
                if (potion != null && potion.Count > 0 && diff < 51)
                    return ItemId.ItemPotion;
                if (superPotion != null && superPotion.Count > 0 && diff < 201)
                    return ItemId.ItemSuperPotion;
                if (hyperPotion != null && hyperPotion.Count > 0)
                    return ItemId.ItemHyperPotion;

                //any
                if (maxPotion != null && maxPotion.Count > 0) return ItemId.ItemMaxPotion;
                if (hyperPotion != null && hyperPotion.Count > 0) return ItemId.ItemHyperPotion;
                if (superPotion != null && superPotion.Count > 0) return ItemId.ItemSuperPotion;
                if (potion != null && potion.Count > 0) return ItemId.ItemPotion;

                //none
                return ItemId.ItemUnknown;
            }

        }

        private async Task<ItemId> GetBestBall(PokemonData pokemon, float? captureProbability)
        {
            var pokemonCp = pokemon.Cp;
            var iV = Math.Round(PokemonInfo.CalculatePokemonPerfection(pokemon));
            var proba = captureProbability; // encounter?.CaptureProbability?.CaptureProbability_.First();
            var balance = PokeRoadieSettings.Current.PokeBallBalancing;

            var items = await _inventory.GetItems();
            var pokeBalls = items.Where(x => x.ItemId == ItemId.ItemPokeBall).FirstOrDefault();
            var greatBalls = items.Where(x => x.ItemId == ItemId.ItemGreatBall).FirstOrDefault();
            var ultraBalls = items.Where(x => x.ItemId == ItemId.ItemUltraBall).FirstOrDefault();
            var masterBalls = items.Where(x => x.ItemId == ItemId.ItemMasterBall).FirstOrDefault();

            var totalCount = (pokeBalls == null ? 0 : pokeBalls.Count) +
                             (greatBalls == null ? 0 : greatBalls.Count) +
                             (ultraBalls == null ? 0 : ultraBalls.Count) +
                             (masterBalls == null ? 0 : masterBalls.Count);

            if (totalCount == 0) return ItemId.ItemUnknown;

            ///var pokeBalls = balls.Any(g => g.Key == MiscEnums.Item.ITEM_POKE_BALL);
            //var greatBalls = balls.Any(g => g.Key == MiscEnums.Item.ITEM_GREAT_BALL);
            //var ultraBalls = balls.Any(g => g.Key == MiscEnums.Item.ITEM_ULTRA_BALL);
            //var masterBalls = balls.Any(g => g.Key == MiscEnums.Item.ITEM_MASTER_BALL);

            if (masterBalls != null && pokemonCp >= 1500)
            {
                //substitute when low (Downgrade)
                if (balance && ultraBalls != null && masterBalls.Count * 3 < ultraBalls.Count)
                    return ItemId.ItemUltraBall;
                //return the default
                return ItemId.ItemMasterBall;
            }
            if (ultraBalls != null && (pokemonCp >= 1000 || (iV >= PokeRoadieSettings.Current.KeepAboveIV && proba < 0.40)))
            {
                //substitute when low (Upgrade)
                if (balance && masterBalls != null && ultraBalls.Count * 3 < masterBalls.Count)
                    return ItemId.ItemMasterBall;
                //substitute when low (Downgrade)
                if (balance && greatBalls != null && ultraBalls.Count * 3 < greatBalls.Count)
                    return ItemId.ItemGreatBall;
                //return the default
                return ItemId.ItemUltraBall;
            }
            if (greatBalls != null && (pokemonCp >= 300 || (iV >= PokeRoadieSettings.Current.KeepAboveIV && proba < 0.50)))
            {
                //substitute when low (Upgrade)
                if (balance && ultraBalls != null && greatBalls.Count * 3 < ultraBalls.Count)
                    return ItemId.ItemUltraBall;
                //substitute when low (Downgrade)
                if (balance && pokeBalls != null && greatBalls.Count * 3 < pokeBalls.Count)
                    return ItemId.ItemPokeBall;
                //return the default
                return ItemId.ItemGreatBall;
            }
            if (pokeBalls != null)
            {
                //substitute when low (Upgrade)
                if (balance && greatBalls != null && pokeBalls.Count * 3 < greatBalls.Count)
                    return ItemId.ItemGreatBall;
                //return the default
                return ItemId.ItemPokeBall;
            }
            //default to highest possible
            if (pokeBalls != null) return ItemId.ItemPokeBall;
            if (greatBalls != null) return ItemId.ItemGreatBall;
            if (ultraBalls != null) return ItemId.ItemUltraBall;
            if (masterBalls != null) return ItemId.ItemMasterBall;

            return ItemId.ItemUnknown;
        }

        private async Task<ItemId> GetBestBall(EncounterResponse encounter)
        {
            return await GetBestBall(encounter?.WildPokemon?.PokemonData, encounter?.CaptureProbability?.CaptureProbability_.First());
        }

        private async Task<ItemId> GetBestBerry(EncounterResponse encounter)
        {
            var pokemonCp = encounter?.WildPokemon?.PokemonData?.Cp;
            var iV = Math.Round(PokemonInfo.CalculatePokemonPerfection(encounter?.WildPokemon?.PokemonData));
            var proba = encounter?.CaptureProbability?.CaptureProbability_.First();

            var items = await _inventory.GetItems();
            var berries = items.Where(i => (i.ItemId == ItemId.ItemRazzBerry
                                        || i.ItemId == ItemId.ItemBlukBerry
                                        || i.ItemId == ItemId.ItemNanabBerry
                                        || i.ItemId == ItemId.ItemWeparBerry
                                        || i.ItemId == ItemId.ItemPinapBerry) && i.Count > 0).GroupBy(i => (i.ItemId)).ToList();
            if (berries.Count == 0 || pokemonCp <= 350) return ItemId.ItemUnknown;

            var razzBerryCount = await _inventory.GetItemAmountByType(ItemId.ItemRazzBerry);
            var blukBerryCount = await _inventory.GetItemAmountByType(ItemId.ItemBlukBerry);
            var nanabBerryCount = await _inventory.GetItemAmountByType(ItemId.ItemNanabBerry);
            var weparBerryCount = await _inventory.GetItemAmountByType(ItemId.ItemWeparBerry);
            var pinapBerryCount = await _inventory.GetItemAmountByType(ItemId.ItemPinapBerry);

            if (pinapBerryCount > 0 && pokemonCp >= 2000)
                return ItemId.ItemPinapBerry;

            if (weparBerryCount > 0 && pokemonCp >= 1500)
                return ItemId.ItemWeparBerry;

            if (nanabBerryCount > 0 && (pokemonCp >= 1000 || (iV >= PokeRoadieSettings.Current.KeepAboveIV && proba < 0.40)))
                return ItemId.ItemNanabBerry;

            if (blukBerryCount > 0 && (pokemonCp >= 500 || (iV >= PokeRoadieSettings.Current.KeepAboveIV && proba < 0.50)))
                return ItemId.ItemBlukBerry;

            if (razzBerryCount > 0 && pokemonCp >= 150)
                return ItemId.ItemRazzBerry;

            return ItemId.ItemUnknown;
            //return berries.OrderBy(g => g.Key).First().Key;
        }

        private async Task<ItemId> GetBestBerry(PokemonData pokemon, float? captureProbability)
        {
            var pokemonCp = pokemon.Cp;
            var iV = Math.Round(PokemonInfo.CalculatePokemonPerfection(pokemon));
            var proba = captureProbability;

            var items = await _inventory.GetItems();
            var berries = items.Where(i => (i.ItemId == ItemId.ItemRazzBerry
                                        || i.ItemId == ItemId.ItemBlukBerry
                                        || i.ItemId == ItemId.ItemNanabBerry
                                        || i.ItemId == ItemId.ItemWeparBerry
                                        || i.ItemId == ItemId.ItemPinapBerry) && i.Count > 0).GroupBy(i => (i.ItemId)).ToList();
            if (berries.Count == 0 || pokemonCp <= 350) return ItemId.ItemUnknown;

            var razzBerryCount = await _inventory.GetItemAmountByType(ItemId.ItemRazzBerry);
            var blukBerryCount = await _inventory.GetItemAmountByType(ItemId.ItemBlukBerry);
            var nanabBerryCount = await _inventory.GetItemAmountByType(ItemId.ItemNanabBerry);
            var weparBerryCount = await _inventory.GetItemAmountByType(ItemId.ItemWeparBerry);
            var pinapBerryCount = await _inventory.GetItemAmountByType(ItemId.ItemPinapBerry);

            if (pinapBerryCount > 0 && pokemonCp >= 2000)
                return ItemId.ItemPinapBerry;

            if (weparBerryCount > 0 && pokemonCp >= 1500)
                return ItemId.ItemWeparBerry;

            if (nanabBerryCount > 0 && (pokemonCp >= 1000 || (iV >= PokeRoadieSettings.Current.KeepAboveIV && proba < 0.40)))
                return ItemId.ItemNanabBerry;

            if (blukBerryCount > 0 && (pokemonCp >= 500 || (iV >= PokeRoadieSettings.Current.KeepAboveIV && proba < 0.50)))
                return ItemId.ItemBlukBerry;

            if (razzBerryCount > 0 && pokemonCp >= 150)
                return ItemId.ItemRazzBerry;

            return ItemId.ItemUnknown;
            //return berries.OrderBy(g => g.Key).First().Key;
        }


        private async Task DisplayHighests()
        {

            //write transfer settings
            if (!hasDisplayedTransferSettings)
            {
                hasDisplayedTransferSettings = true;
                Logger.Write("====== Transfer Settings ======", LogLevel.None, ConsoleColor.Yellow);
                Logger.Write($"{("Keep Above CP:").PadRight(25)}{PokeRoadieSettings.Current.KeepAboveCp}", LogLevel.None, ConsoleColor.White);
                Logger.Write($"{("Keep Above IV:").PadRight(25)}{PokeRoadieSettings.Current.KeepAboveIV}", LogLevel.None, ConsoleColor.White);
                Logger.Write($"{("Keep Above V:").PadRight(25)}{PokeRoadieSettings.Current.KeepAboveV}", LogLevel.None, ConsoleColor.White);
                Logger.Write($"{("Transfer Below CP:").PadRight(25)}{PokeRoadieSettings.Current.TransferBelowCp}", LogLevel.None, ConsoleColor.White);
                Logger.Write($"{("Transfer Below IV:").PadRight(25)}{PokeRoadieSettings.Current.TransferBelowIV}", LogLevel.None, ConsoleColor.White);
                Logger.Write($"{("Transfer Below V:").PadRight(25)}{PokeRoadieSettings.Current.TransferBelowV}", LogLevel.None, ConsoleColor.White);
                Logger.Write($"{("Transfer Evolvable:").PadRight(25)}{!PokeRoadieSettings.Current.NotTransferPokemonsThatCanEvolve}", LogLevel.None, ConsoleColor.White);
                if (PokeRoadieSettings.Current.PokemonsNotToTransfer.Count > 0)
                {
                    Logger.Write($"{("PokemonsNotToTransfer:").PadRight(25)} {PokeRoadieSettings.Current.PokemonsNotToTransfer.Count}", LogLevel.None, ConsoleColor.White);
                    foreach (PokemonId i in PokeRoadieSettings.Current.PokemonsNotToTransfer)
                    {
                        Logger.Write(i.ToString(), LogLevel.None, ConsoleColor.White);
                    }
                }
            }
 
            //get all ordered by id, then cp
            var allPokemon = (await _inventory.GetPokemons()).OrderBy(x => x.PokemonId).ThenByDescending(x => x.Cp).ToList();

            if (PokeRoadieSettings.Current.DestinationsEnabled && PokeRoadieSettings.Current.Destinations != null && PokeRoadieSettings.Current.Destinations.Count > 0)
            {
                Logger.Write("====== Destinations ======", LogLevel.None, ConsoleColor.Yellow);
                LocationData lastDestination = null;
                for (int i = 0; i < PokeRoadieSettings.Current.Destinations.Count; i++)
                {
                    var destination = PokeRoadieSettings.Current.Destinations[i];
                    var str = $"{i} - {destination.Name} - {destination.Latitude}:{destination.Longitude}:{destination.Altitude}";
                    if (PokeRoadieSettings.Current.DestinationIndex < i)
                    {
                        if (lastDestination != null)
                        {

                            var sourceLocation = new GeoCoordinate(lastDestination.Latitude, lastDestination.Longitude, lastDestination.Altitude);
                            var targetLocation = new GeoCoordinate(destination.Latitude, destination.Longitude, destination.Altitude);
                            var distanceToTarget = LocationUtils.CalculateDistanceInMeters(sourceLocation, targetLocation);
                            var speed = PokeRoadieSettings.Current.FlyingEnabled ? PokeRoadieSettings.Current.FlyingSpeed : PokeRoadieSettings.Current.MaxSpeed;
                            var speedInMetersPerSecond = speed / 3.6;
                            var seconds = distanceToTarget / speedInMetersPerSecond;
                            var action = PokeRoadieSettings.Current.FlyingEnabled ? "flying" : "driving";
                            str += " (";
                            str += StringUtils.GetSecondsDisplay(seconds);
                            str += $" {action} at {speed}kmh)";

                        }
                    }
                    else if (PokeRoadieSettings.Current.DestinationIndex == i)
                    {
                        str += " <-- You Are Here!";
                    }
                    else
                    {
                        str += " (Visited)";
                    }
                    Logger.Write(str, LogLevel.None, PokeRoadieSettings.Current.DestinationIndex == i ? ConsoleColor.Red : PokeRoadieSettings.Current.DestinationIndex < i ? ConsoleColor.White : ConsoleColor.DarkGray);
                    lastDestination = destination;
                }
            }
            Logger.Write("====== Most Valuable ======", LogLevel.None, ConsoleColor.Yellow);
            var highestsPokemonV = await _inventory.GetHighestsV(20);
            foreach (var pokemon in highestsPokemonV)
                Logger.Write(pokemon.GetStats(), LogLevel.None, ConsoleColor.White);
            Logger.Write("====== Highest CP ======", LogLevel.None, ConsoleColor.Yellow);
            var highestsPokemonCp = await _inventory.GetHighestsCP(20);
            foreach (var pokemon in highestsPokemonCp)
                Logger.Write(pokemon.GetStats(), LogLevel.None, ConsoleColor.White);
            Logger.Write("====== Most Perfect Genetics ======", LogLevel.None, ConsoleColor.Yellow);
            var highestsPokemonPerfect = await _inventory.GetHighestsPerfect(20);
            foreach (var pokemon in highestsPokemonPerfect)
            {
                Logger.Write(pokemon.GetStats(), LogLevel.None, ConsoleColor.White);
            }
            if (PokeRoadieSettings.Current.DisplayAllPokemonInLog)
            {
                Logger.Write("====== Full List ======", LogLevel.None, ConsoleColor.Yellow);
                foreach (var pokemon in allPokemon.OrderBy(x => x.PokemonId).ThenByDescending(x => x.Cp))
                {
                    Logger.Write(pokemon.GetStats(), LogLevel.None, ConsoleColor.White);
                }
            }
            if (PokeRoadieSettings.Current.DisplayAggregateLog)
            {
                Logger.Write("====== Aggregate Data ======", LogLevel.None, ConsoleColor.Yellow);
                Logger.Write($"{allPokemon.Count} Total Pokemon", LogLevel.None, ConsoleColor.White);
                Logger.Write("====== Cp ======", LogLevel.None, ConsoleColor.White);
                Logger.Write($"< 100 Cp: {allPokemon.Where(x => x.Cp < 100).Count()}", LogLevel.None, ConsoleColor.White);
                Logger.Write($"100-499 Cp: {allPokemon.Where(x => x.Cp >= 100 && x.Cp < 500).Count()}", LogLevel.None, ConsoleColor.White);
                Logger.Write($"500-999 Cp: {allPokemon.Where(x => x.Cp >= 500 && x.Cp < 1000).Count()}", LogLevel.None, ConsoleColor.White);
                Logger.Write($"1000-1499 Cp: {allPokemon.Where(x => x.Cp >= 1000 && x.Cp < 1500).Count()}", LogLevel.None, ConsoleColor.White);
                Logger.Write($"> 1499 Cp: {allPokemon.Where(x => x.Cp >= 1500).Count()}", LogLevel.None, ConsoleColor.White);
                Logger.Write("====== IV ======", LogLevel.None, ConsoleColor.White);
                Logger.Write($"24% or less: {allPokemon.Where(x => x.GetPerfection() < 25).Count()}", LogLevel.None, ConsoleColor.White);
                Logger.Write($"25%-49%: {allPokemon.Where(x => x.GetPerfection() > 24 && x.GetPerfection() < 50).Count()}", LogLevel.None, ConsoleColor.White);
                Logger.Write($"50%-74%: {allPokemon.Where(x => x.GetPerfection() > 49 && x.GetPerfection() < 75).Count()}", LogLevel.None, ConsoleColor.White);
                Logger.Write($"75%-89%: {allPokemon.Where(x => x.GetPerfection() > 74 && x.GetPerfection() < 90).Count()}", LogLevel.None, ConsoleColor.White);
                Logger.Write($"90%-100%: {allPokemon.Where(x => x.GetPerfection() > 89).Count()}", LogLevel.None, ConsoleColor.White);
            }

        }

        /*
        private async Task LoadAndDisplayGpxFile()
        {
            var xmlString = File.ReadAllText(PokeRoadieSettings.Current.GPXFile);
            var readgpx = new GpxReader(xmlString);
            foreach (var trk in readgpx.Tracks)
            {
                foreach (var trkseg in trk.Segments)
                {
                    foreach (var trpkt in trkseg.TrackPoints)
                    {
                        Console.WriteLine(trpkt.ToString());
                    }
                }
            }
            await Task.Delay(0);
        }
        */

        /*
        private GPXReader.trk GetGPXTrack(string gpxFile)
        {
            string xmlString = File.ReadAllText(PokeRoadieSettings.Current.GPXFile);
            GPXReader Readgpx = new GPXReader(xmlString);
            return Readgpx.Tracks.ElementAt(0);
        }
        */
        private async Task Export()
        {
            if (!_lastExportTime.HasValue || _lastExportTime.Value < DateTime.Now)
            {
                _lastExportTime = DateTime.Now.AddMinutes(5);
                await _inventory.ExportPokemonToCSV(_playerProfile.PlayerData);
            }
        }

        private List<GpxReader.Trk> GetGpxTracks()
        {
            var xmlString = File.ReadAllText(PokeRoadieSettings.Current.GPXFile);
            var readgpx = new GpxReader(xmlString);
            return readgpx.Tracks;
        }


        public async Task UseLuckyEgg()
        {
            if (PokeRoadieSettings.Current.UseLuckyEggs && (!_lastLuckyEggTime.HasValue || _lastLuckyEggTime.Value < DateTime.Now))
            {
                var inventory = await _inventory.GetItems();
                var LuckyEgg = inventory.Where(p => p.ItemId == ItemId.ItemLuckyEgg).FirstOrDefault();
                if (LuckyEgg == null || LuckyEgg.Count <= 0) return;

                var response = await _client.Inventory.UseItemXpBoost();
                if (response.Result == UseItemXpBoostResponse.Types.Result.Success)
                {
                    _lastLuckyEggTime = DateTime.Now.AddMinutes(30);
                    Logger.Write($"(EGG) Used Lucky Egg, remaining: {LuckyEgg.Count - 1}", LogLevel.None, ConsoleColor.Magenta);
                }
                else if (response.Result == UseItemXpBoostResponse.Types.Result.ErrorXpBoostAlreadyActive || response.Result == UseItemXpBoostResponse.Types.Result.Unset)
                {
                    _lastLuckyEggTime = DateTime.Now.AddMinutes(30);
                    Logger.Write($"(EGG) Egg Active", LogLevel.None, ConsoleColor.Magenta);
                }

            }
        }

        private async Task HealPokemon()
        {
            await PokeRoadieInventory.getCachedInventory(_client, true);
            var pokemonList = await _inventory.GetPokemonToHeal();
            if (pokemonList == null || pokemonList.Count() == 0) return;

            Logger.Write($"Found {pokemonList.Count()} pokemon to heal...", LogLevel.Info);

            bool stopHealing = false;
            foreach (var pokemon in pokemonList)
            {
                if (!isRunning) break;
                var potion = await GetBestPotion(pokemon);
                var hp = 0;
                while (potion != ItemId.ItemUnknown && hp < pokemon.StaminaMax)
                {
                    if (potion == ItemId.ItemUnknown)
                    {
                        Logger.Write($"Ran out of healing potions...", LogLevel.Info);
                        stopHealing = true;
                        break;
                    }
                    else
                    {
                        var response = await _client.Inventory.UseItemPotion(potion, pokemon.Id);
                        if (response.Result == UseItemPotionResponse.Types.Result.Success)
                        {
                            Logger.Write($"Healed {pokemon.GetMinStats()} with {potion} - {response.Stamina}/{pokemon.StaminaMax}", LogLevel.Pokemon);
                            hp = response.Stamina;
                        }
                        else
                        {
                            Logger.Write($"Failed to heal {pokemon.GetMinStats()} with {potion} - {response.Result}", LogLevel.Error);
                            stopHealing = true;
                            break;
                        }
                    }
                }
                if (stopHealing) break;
            }
        }

        private async Task RevivePokemon()
        {
            await PokeRoadieInventory.getCachedInventory(_client, true);
            var pokemonList = await _inventory.GetPokemonToRevive();
            if (pokemonList == null || pokemonList.Count() == 0) return;

            Logger.Write($"Found {pokemonList.Count()} pokemon to revive...", LogLevel.Info);

            foreach (var pokemon in pokemonList)
            {
                if (!isRunning) break;
                var potion = await GetBestPotion(pokemon);
                if (potion == ItemId.ItemUnknown)
                {
                    Logger.Write($"Ran out of revive potions...", LogLevel.Info);
                    break;
                }
                else
                {
                    var response = await _client.Inventory.UseItemPotion(potion, pokemon.Id);
                    if (response.Result == UseItemPotionResponse.Types.Result.Success)
                    {
                        Logger.Write($"Revived {pokemon.GetMinStats()} with {potion} ", LogLevel.Pokemon);
                    }
                    else
                    {
                        Logger.Write($"Failed to revive {pokemon.GetMinStats()} with {potion} - {response.Result}", LogLevel.Error);
                    }
                }
            }
        }

        public async Task UseIncense()
        {
            if (PokeRoadieSettings.Current.UseIncense && (!_lastIncenseTime.HasValue || _lastIncenseTime.Value < DateTime.Now))
            {
                var inventory = await _inventory.GetItems();
                var WorstIncense = inventory.FirstOrDefault(p => p.ItemId == ItemId.ItemIncenseOrdinary);
                if (WorstIncense == null || WorstIncense.Count <= 0) return;

                var response = await _client.Inventory.UseIncense(ItemId.ItemIncenseOrdinary);
                if (response.Result == UseIncenseResponse.Types.Result.Success)
                {
                    _lastIncenseTime = DateTime.Now.AddMinutes(30);
                    Logger.Write($"(INCENSE) Used Ordinary Incense, remaining: {WorstIncense.Count - 1}", LogLevel.None, ConsoleColor.Magenta);
                }
                else if (response.Result == UseIncenseResponse.Types.Result.IncenseAlreadyActive)
                {
                    _lastIncenseTime = DateTime.Now.AddMinutes(30);
                    Logger.Write($"(INCENSE) Incense Active", LogLevel.None, ConsoleColor.Magenta);
                }

            }
        }

        private Func<Task> GetLongWalkingTask()
        {
            Func<Task> del = null;
            if (softBan) return del;
            if (!PokeRoadieSettings.Current.CatchPokemon && !PokeRoadieSettings.Current.VisitPokestops) return del;
            if (PokeRoadieSettings.Current.CatchPokemon && PokeRoadieSettings.Current.VisitPokestops) return GpxCatchNearbyPokemonsAndStops;
            if (PokeRoadieSettings.Current.CatchPokemon) return CatchNearbyPokemons;
            if (PokeRoadieSettings.Current.VisitPokestops) return GpxCatchNearbyStops;
            return del;
        }
        private Func<Task> GetFlyingTask()
        {
            Func<Task> del = null;
            if (softBan) return del;
            if (PokeRoadieSettings.Current.VisitPokestops && PokeRoadieSettings.Current.PingStopsWhileFlying) return GpxCatchNearbyStops;
            return del;
        }
        private Func<Task> GetShortWalkingTask()
        {
            Func<Task> del = null;
            if (softBan) return del;
            if (PokeRoadieSettings.Current.CatchPokemon) return CatchNearbyPokemons;
            return del;
        }
        private Func<Task> GetGpxTask()
        {
            Func<Task> del = null;
            if (softBan) return del;
            if (!PokeRoadieSettings.Current.CatchPokemon && !PokeRoadieSettings.Current.VisitPokestops) return del;
            if (PokeRoadieSettings.Current.CatchPokemon && PokeRoadieSettings.Current.VisitPokestops) return GpxCatchNearbyPokemonsAndStops;
            if (PokeRoadieSettings.Current.CatchPokemon) return CatchNearbyPokemons;
            return GpxCatchNearbyStops;
        }

        #region " Waypoint Methods "

        private async Task NextDestination()
        {
            //get current destination
            var currentDestination = PokeRoadieSettings.Current.Destinations[PokeRoadieSettings.Current.DestinationIndex];
            //get new destination index
            var newIndex = PokeRoadieSettings.Current.DestinationIndex + 1 >= PokeRoadieSettings.Current.Destinations.Count ? 0 : PokeRoadieSettings.Current.DestinationIndex + 1;
            //get coords
            var destination = PokeRoadieSettings.Current.Destinations[newIndex];

            //set new index and default location
            PokeRoadieSettings.Current.DestinationIndex = newIndex;

            OnChangeDestination?.Invoke(destination, newIndex);

            //set new waypoint
            SetWaypoint(destination);

            //get result
            await Travel(
                GetCurrentGeo(),
                GetWaypointGeo(),
                true, destination.Name
                );
        }

        private async Task CheckDestinations()
        {
            //if destinations are enabled
            if (PokeRoadieSettings.Current.DestinationsEnabled)
            {
                if (PokeRoadieSettings.Current.DestinationEndDate.HasValue)
                {
                    if (DateTime.Now > PokeRoadieSettings.Current.DestinationEndDate.Value)
                    {

                        if (PokeRoadieSettings.Current.Destinations != null && PokeRoadieSettings.Current.Destinations.Count > 1)
                        {
                            await NextDestination();
                        }
                        else
                        {
                            PokeRoadieSettings.Current.DestinationEndDate = DateTime.Now.AddMinutes(PokeRoadieSettings.Current.MinutesPerDestination);
                        }
                    }
                }
                else
                {
                    PokeRoadieSettings.Current.DestinationEndDate = DateTime.Now.AddMinutes(PokeRoadieSettings.Current.MinutesPerDestination);
                }
            }
        }

        private void SetWaypoint(LocationData destination)
        {
            SetWaypoint(destination.GetGeo());
            OnChangeWaypoint?.Invoke(destination);
        }

        private void SetWaypoint(GeoCoordinate geo)
        {
            PokeRoadieSettings.Current.WaypointLatitude = geo.Latitude;
            PokeRoadieSettings.Current.WaypointLongitude = geo.Longitude;
            PokeRoadieSettings.Current.WaypointAltitude = geo.Altitude;
            PokeRoadieSettings.Current.Save();
            
        }

        private async Task GotoCurrentWaypoint()
        {
            await Travel
            (
                GetCurrentGeo(),
                GetWaypointGeo()
                , true, "waypoint center"
            );
        }

        private async Task CheckWaypoint()
        {
            var distanceFromStart = LocationUtils.CalculateDistanceInMeters(
            _client.CurrentLatitude, _client.CurrentLongitude, PokeRoadieSettings.Current.WaypointLatitude, PokeRoadieSettings.Current.WaypointLongitude);

            // Edge case for when the client somehow ends up outside the defined radius
            if (PokeRoadieSettings.Current.MaxDistance != 0 && distanceFromStart > PokeRoadieSettings.Current.MaxDistance)
            {
                //return back the the waypoint
                Logger.Write($"Returning to the start.", LogLevel.Navigation);
                await GotoCurrentWaypoint();


                //if (PokeRoadieSettings.Current.DestinationsEnabled)
                //{
                //    //return back the the waypoint
                //    Logger.Write($"Returning to the start.", LogLevel.Navigation);
                //    await GotoCurrentWaypoint();
                //}
                //else
                //{
                //    if (travelHistory.Count > 4)
                //    {
                //        Logger.Write($"Returning to the start.", LogLevel.Navigation);
                //        var geo = travelHistory[0];
                //        travelHistory.Clear();
                //        SetWaypoint(geo);
                //        await GotoCurrentWaypoint();
                //    }
                //    else
                //    {
                //        var pokeStopList = await _inventory.GetPokestops(false);
                //        if (pokeStopList != null && pokeStopList.Count() > 5)
                //        {
                //            Logger.Write($"Set current location as new waypoint {pokeStopList.Count()}", LogLevel.Navigation);
                //            SetWaypoint(GetCurrentGeo());
                //        }
                //    }
                //}
                //Logger.Write($"Reached the edge of the waypoint", LogLevel.Navigation);

                ////set current point as new waypoint
                //Logger.Write($"Set the current location as the new waypoint", LogLevel.Navigation);

            }
        }

        private static GeoCoordinate GetWaypointGeo()
        {
            return new GeoCoordinate(PokeRoadieSettings.Current.WaypointLatitude, PokeRoadieSettings.Current.WaypointLongitude, PokeRoadieSettings.Current.WaypointAltitude);
        }

        private GeoCoordinate GetCurrentGeo()
        {
            return new GeoCoordinate(_client.CurrentLatitude, _client.CurrentLongitude, _client.CurrentAltitude);
        }

        private async Task Travel(LocationData source, LocationData destination)
        {
            await Travel(source.GetGeo(), destination.GetGeo(), true, destination.Name);
        }

        private async Task Travel(GeoCoordinate source, GeoCoordinate destination, bool fly, string name = "")
        {
            //get distance
            var distance = LocationUtils.CalculateDistanceInMeters(source, destination);
            if (distance > 5)
            {
                //determine if we fly
                var isFlying = fly && PokeRoadieSettings.Current.FlyingEnabled && distance > 1000;

                //get travel plan
                if (fly && PokeRoadieSettings.Current.FlyingEnabled && distance > 1000)
                {
                    Logger.Write($"Boarded flight #{RandomHelper.RandomNumber(101, 501)}{(string.IsNullOrWhiteSpace(name) ? "" : $" to {name}")}", LogLevel.Navigation, ConsoleColor.White);
                }
                else if (!string.IsNullOrWhiteSpace(name))
                {
                    Logger.Write($"Traveling to {name}", LogLevel.Navigation);
                }

                //go to location
                var response = await _navigation.HumanLikeWalking(destination, isFlying ? PokeRoadieSettings.Current.FlyingSpeed : PokeRoadieSettings.Current.MinSpeed, isFlying ? GetFlyingTask() : GetShortWalkingTask());

                //log arrival
                if (!string.IsNullOrWhiteSpace(name))
                {
                    Logger.Write($"Arrived at {name}!", LogLevel.Navigation, ConsoleColor.White);
                }

            }

        }

        public void Xlo()
        {
            if (!isRunning) return;
            if (Directory.Exists(pokestopsDir))
            {
                var files = Directory.GetFiles(pokestopsDir)
                                .Where(x => x.EndsWith(".xml")).ToList();
                foreach (var filePath in files)
                {
                    if (!isRunning) break;
                    var info = new FileInfo(filePath);
                    if (info.CreationTime.AddSeconds(60) < DateTime.Now)
                    {
                        try
                        {
                            //pull the file
                            var pokestop = (Xml.Pokestop)Xml.Serializer.DeserializeFromFile(filePath, typeof(Xml.Pokestop));
                            var f = Xml.Serializer.Xlo(pokestop);
                            f.Wait();
                            if (f.Status == TaskStatus.RanToCompletion) File.Delete(filePath);
                        }
                        catch (Exception ex)
                        {
                            System.Threading.Thread.Sleep(1500);
                            //do nothing
                            Logger.Write($"Pokestop {info.Name} failed xlo transition. {ex.Message}", LogLevel.Warning);
                        }
                    }
                    System.Threading.Thread.Sleep(500);
                }
            }

            if (Directory.Exists(gymDir))
            {
                var files = Directory.GetFiles(gymDir)
                                .Where(x => x.EndsWith(".xml")).ToList();
                foreach (var filePath in files)
                {
                    if (!isRunning) break;
                    var info = new FileInfo(filePath);
                    if (info.CreationTime.AddSeconds(60) < DateTime.Now)
                    {
                        try
                        {
                            //pull the file
                            var gym = (Xml.Gym)Xml.Serializer.DeserializeFromFile(filePath, typeof(Xml.Gym));
                            var f = Xml.Serializer.Xlo(gym, info.CreationTime);
                            f.Wait();
                            if (f.Status == TaskStatus.RanToCompletion) File.Delete(filePath);
                        }
                        catch (Exception ex)
                        {
                            Logger.Write($"Gym {info.Name} failed xlo transition. {ex.Message}", LogLevel.Warning);
                            System.Threading.Thread.Sleep(1500);
                            //do nothing
                           
                        }
                    }
                    System.Threading.Thread.Sleep(500);
                }
            }

            for (int i = 0;i < 20; i++)
            {
                if (!isRunning) return;
                System.Threading.Thread.Sleep(1000);
            }

            Task.Run(new Action(Xlo));
        }
        #endregion

        private void RelayLocation(LocationData location)
        {
            OnChangeLocation?.Invoke(location);
        }
    }
}
 