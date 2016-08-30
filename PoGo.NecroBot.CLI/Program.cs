﻿#region using directives

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using PoGo.NecroBot.CLI.Resources;
using PoGo.NecroBot.Logic;
using PoGo.NecroBot.Logic.Common;
using PoGo.NecroBot.Logic.Event;
using PoGo.NecroBot.Logic.Logging;
using PoGo.NecroBot.Logic.Model.Google;
using PoGo.NecroBot.Logic.Model.Settings;
using PoGo.NecroBot.Logic.State;
using PoGo.NecroBot.Logic.Tasks;
using PoGo.NecroBot.Logic.Utils;
using System.Collections.Generic;

#endregion

namespace PoGo.NecroBot.CLI
{
    internal class Program
    {
        private static readonly ManualResetEvent QuitEvent = new ManualResetEvent(false);
        private static string subPath = "";
        private static Uri strKillSwitchUri = new Uri("https://raw.githubusercontent.com/NoxxDev/NecroBot/master/KillSwitch.txt");
        private static Session session = null;
        private static double Lat, Lng;
        private static bool LocUpdate = false;
        [System.Runtime.InteropServices.DllImport("Kernel32")]
        private static extern bool SetConsoleCtrlHandler(EventHandler handler, bool add);

        private delegate bool EventHandler(CtrlType sig);
        static EventHandler _handler;
        enum CtrlType
        {
            CTRL_C_EVENT = 0,
            CTRL_BREAK_EVENT = 1,
            CTRL_CLOSE_EVENT = 2,
            CTRL_LOGOFF_EVENT = 5,
            CTRL_SHUTDOWN_EVENT = 6
        }

        private static void Main(string[] args)
        {

            string strCulture = Thread.CurrentThread.CurrentCulture.TwoLetterISOLanguageName;

            var culture = CultureInfo.CreateSpecificCulture("en");
            CultureInfo.DefaultThreadCurrentCulture = culture;
            Thread.CurrentThread.CurrentCulture = culture;

            AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionEventHandler;
            AppDomain.CurrentDomain.ProcessExit += OnExitHandler;
            _handler += new EventHandler(OnExit);
            SetConsoleCtrlHandler(_handler, true);

            Console.Title = "NecroBot";
            Console.CancelKeyPress += (sender, eArgs) =>
            {
                QuitEvent.Set();
                eArgs.Cancel = true;
            };
            if (args.Length > 0)
                subPath = args[0];

            Logger.SetLogger(new ConsoleLogger(LogLevel.LevelUp), subPath);

            if (CheckKillSwitch())
                return;

            var profilePath = Path.Combine(Directory.GetCurrentDirectory(), subPath);
            var profileConfigPath = Path.Combine(profilePath, "config");
            var configFile = Path.Combine(profileConfigPath, "config.json");

            GlobalSettings settings;
            Boolean boolNeedsSetup = false;

            if (File.Exists(configFile))
            {
                // Load the settings from the config file
                // If the current program is not the latest version, ensure we skip saving the file after loading
                // This is to prevent saving the file with new options at their default values so we can check for differences
                settings = GlobalSettings.Load(subPath, !VersionCheckState.IsLatest());
            }
            else
            {
                settings = new GlobalSettings();
                settings.ProfilePath = profilePath;
                settings.ProfileConfigPath = profileConfigPath;
                settings.GeneralConfigPath = Path.Combine(Directory.GetCurrentDirectory(), "config");
                settings.ConsoleConfig.TranslationLanguageCode = strCulture;

                boolNeedsSetup = true;
            }

            if (args.Length > 1)
            {
                string[] crds = args[1].Split(',');
                double lat, lng;
                try
                {
                    lat = Double.Parse(crds[0]);
                    lng = Double.Parse(crds[1]);
                    settings.LocationConfig.DefaultLatitude = lat;
                    settings.LocationConfig.DefaultLongitude = lng;
                }
                catch (Exception) { }
            }

            var lastPosFile = Path.Combine(profileConfigPath, "LastPos.ini");
            if (File.Exists(lastPosFile) && settings.LocationConfig.StartFromLastPosition)
            {
                var text = File.ReadAllText(lastPosFile);
                string[] crds = text.Split(':');
                double lat, lng;
                try
                {
                    lat = Double.Parse(crds[0]);
                    lng = Double.Parse(crds[1]);
                    settings.LocationConfig.DefaultLatitude = lat;
                    settings.LocationConfig.DefaultLongitude = lng;
                }
                catch (Exception) { }
            }

            var logicSettings = new LogicSettings(settings);
            var translation = Translation.Load(logicSettings);

            if (settings.GPXConfig.UseGpxPathing)
            {
                var xmlString = File.ReadAllText(settings.GPXConfig.GpxFile);
                var readgpx = new GpxReader(xmlString, translation);
                var nearestPt = readgpx.Tracks.SelectMany(
                    (trk, trkindex) =>
                    trk.Segments.SelectMany(
                        (seg, segindex) =>
                            seg.TrackPoints.Select(
                                (pt, ptindex) =>
                                    new
                                    {
                                        TrackPoint = pt,
                                        TrackIndex = trkindex,
                                        SegIndex = segindex,
                                        PtIndex = ptindex,
                                        Latitude = Convert.ToDouble(pt.Lat, CultureInfo.InvariantCulture),
                                        Longitude = Convert.ToDouble(pt.Lon, CultureInfo.InvariantCulture),
                                        Distance = LocationUtils.CalculateDistanceInMeters(
                                            settings.LocationConfig.DefaultLatitude,
                                            settings.LocationConfig.DefaultLongitude,
                                            Convert.ToDouble(pt.Lat, CultureInfo.InvariantCulture),
                                            Convert.ToDouble(pt.Lon, CultureInfo.InvariantCulture)
                                        )
                                    }
                            )
                    )
                ).OrderBy(pt => pt.Distance).FirstOrDefault(pt => pt.Distance <= 5000);

                if (nearestPt != null)
                {
                    settings.LocationConfig.DefaultLatitude = nearestPt.Latitude;
                    settings.LocationConfig.DefaultLongitude = nearestPt.Longitude;
                    settings.LocationConfig.ResumeTrack = nearestPt.TrackIndex;
                    settings.LocationConfig.ResumeTrackSeg = nearestPt.SegIndex;
                    settings.LocationConfig.ResumeTrackPt = nearestPt.PtIndex;
                }
            }

            session = new Session(new ClientSettings(settings), logicSettings, translation);

            Teste.Testar(session);
            if (boolNeedsSetup)
            {
                if (GlobalSettings.PromptForSetup(session.Translation))
                {
                    session = GlobalSettings.SetupSettings(session, settings, configFile);

                    var fileName = Assembly.GetExecutingAssembly().Location;
                    System.Diagnostics.Process.Start(fileName);
                    Environment.Exit(0);
                }
                else
                {
                    GlobalSettings.Load(subPath);

                    Logger.Write("Press a Key to continue...",
                        LogLevel.Warning);
                    Console.ReadKey();
                    return;
                }

            }

            ProgressBar.start("NecroBot is starting up", 10);

            session.Client.ApiFailure = new ApiFailureStrategy(session);
            ProgressBar.fill(20);

            /*SimpleSession session = new SimpleSession
            {
                _client = new PokemonGo.RocketAPI.Client(new ClientSettings(settings)),
                _dispatcher = new EventDispatcher(),
                _localizer = new Localizer()
            };

            BotService service = new BotService
            {
                _session = session,
                _loginTask = new Login(session)
            };

            service.Run();
            */

            var machine = new StateMachine();
            var stats = new Statistics();

            ProgressBar.fill(30);
            string strVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            stats.DirtyEvent +=
                () =>
                    Console.Title = $"[Necrobot v{strVersion}] " +
                        stats.GetTemplatedStats(
                            session.Translation.GetTranslation(TranslationString.StatsTemplateString),
                            session.Translation.GetTranslation(TranslationString.StatsXpTemplateString));
            ProgressBar.fill(40);

            var aggregator = new StatisticsAggregator(stats);
            ProgressBar.fill(50);
            var listener = new ConsoleEventListener();
            ProgressBar.fill(60);
            var snipeEventListener = new SniperEventListener();

            session.EventDispatcher.EventReceived += evt => listener.Listen(evt, session);
            session.EventDispatcher.EventReceived += evt => aggregator.Listen(evt, session);
            session.EventDispatcher.EventReceived += evt => snipeEventListener.Listen(evt, session);

            if (settings.WebsocketsConfig.UseWebsocket)
            {
                var websocket = new WebSocketInterface(settings.WebsocketsConfig.WebSocketPort, session);
                session.EventDispatcher.EventReceived += evt => websocket.Listen(evt, session);
            }

            ProgressBar.fill(70);

            machine.SetFailureState(new LoginState());
            ProgressBar.fill(80);

            Logger.SetLoggerContext(session);
            ProgressBar.fill(90);

            session.Navigation.WalkStrategy.UpdatePositionEvent += (lat, lng) => session.EventDispatcher.Send(new UpdatePositionEvent { Latitude = lat, Longitude = lng });
            session.Navigation.WalkStrategy.UpdatePositionEvent += (lat, lng) => { LocUpdate = true; Lat = lat; Lng = lng; };

            ProgressBar.fill(100);

            machine.AsyncStart(new VersionCheckState(), session, subPath);

            try
            { Console.Clear(); }
            catch (IOException) { }
            
            if (settings.TelegramConfig.UseTelegramAPI)
                session.Telegram = new Logic.Service.TelegramService(settings.TelegramConfig.TelegramAPIKey, session);

            if (session.LogicSettings.UseSnipeLocationServer)
                SnipePokemonTask.AsyncStart(session);

            settings.checkProxy(session.Translation);

            QuitEvent.WaitOne();
            OnExit(CtrlType.CTRL_CLOSE_EVENT);
        }

        private static void SaveLocationToDisk(double lat, double lng)
        {
            var coordsPath = Path.Combine(session.LogicSettings.ProfileConfigPath, "LastPos.ini");

            File.WriteAllText(coordsPath, $"{lat}:{lng}");
        }

        private static void SaveTimeStampsToDisk()
        {
            if (session == null) return;

            var path = Path.Combine(session.LogicSettings.ProfileConfigPath, "PokestopTS.txt");
            List<string> fileContent = new List<string>();

            foreach (var t in session.Stats.PokeStopTimestamps)
                fileContent.Add(t.ToString());

            if (fileContent.Count > 0)
                File.WriteAllLines(path, fileContent.ToArray());

            path = Path.Combine(session.LogicSettings.ProfileConfigPath, "PokemonTS.txt");
            fileContent = new List<string>();
            foreach (var t in session.Stats.PokemonTimestamps)
                fileContent.Add(t.ToString());

            if (fileContent.Count > 0)
                File.WriteAllLines(path, fileContent.ToArray());
        }

        private static bool CheckKillSwitch()
        {
            using (var wC = new WebClient())
            {
                try
                {
                    string strResponse = WebClientExtensions.DownloadString(wC, strKillSwitchUri);

                    if (strResponse == null)
                        return false;

                    string[] strSplit = strResponse.Split(';');

                    if (strSplit.Length > 1)
                    {
                        string strStatus = strSplit[0];
                        string strReason = strSplit[1];

                        if (strStatus.ToLower().Contains("disable"))
                        {
                            Console.WriteLine(strReason + "\n");

                            Logger.Write("The bot will now close, please press enter to continue", LogLevel.Error);
                            Console.ReadLine();
                            return true;
                        }
                    }
                    else
                        return false;
                }
                catch (WebException)
                {
                }
            }

            return false;
        }

        private static void UnhandledExceptionEventHandler(object obj, UnhandledExceptionEventArgs args)
        {
            Logger.Write("Exception caught, writing LogBuffer.", force: true);
            throw new Exception();
        }

        // Lets save my SSD...
        private static bool OnExit(CtrlType sig)
        {
            if (LocUpdate)
                SaveLocationToDisk(Lat, Lng);

            SaveTimeStampsToDisk();
            return true;
        }

        private static void OnExitHandler(object s, EventArgs e)
        {
            OnExit(CtrlType.CTRL_CLOSE_EVENT);
        }
    }
}
