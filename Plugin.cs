using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Diagnostics;

namespace SailwindVirtualCrew
{
    [BepInPlugin(PLUGIN_ID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PLUGIN_ID = "com.zorkinian.virtualcrew";
        public const string PLUGIN_NAME = "VirtualCrew";
        public const string PLUGIN_VERSION = "0.1.14";

        //--settings--
        internal static ConfigEntry<bool> exampleSetting;
        internal static ConfigEntry<KeyboardShortcut> ToggleCrewWindow;
        internal static ConfigEntry<KeyboardShortcut> ResetWindowPositions;
        internal static ConfigEntry<KeyboardShortcut> SupercargoSellAtPortKey;
        internal static ConfigEntry<KeyboardShortcut> SupercargoKeepCargoKey;
        internal static ConfigEntry<KeyboardShortcut> CargoControllerGrabPortCargoKey;
        internal static ConfigEntry<bool> RequireCrewForExternalModFeatures;
        internal static ConfigEntry<bool> ExtraWorkingStaminaDrain;
        internal static ConfigEntry<bool> InstrumentationEnabled;
        internal static ConfigEntry<string> InstrumentationOutputDirectory;
        internal static ConfigEntry<float> InstrumentationFlushIntervalSeconds;

        // PID slider ranges
        internal static ConfigEntry<float> PidMaxP;
        internal static ConfigEntry<float> PidMaxI;
        internal static ConfigEntry<float> PidMaxD;

        private float tickTimer = 0f;
        private const float tickInterval = 1f;
        private string _lastScannedVesselKey;
        private bool _vesselScanRequested;

        private ConfigEntry<KeyboardShortcut> BuildShipMap;
        private ConfigEntry<KeyboardShortcut> ScanItems;

        public static Plugin Instance { get; private set; }
        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), PLUGIN_ID);

            exampleSetting = Config.Bind("Section", "Key", true, new ConfigDescription("Information about the config setting"));

            PidMaxP = Config.Bind("Autopilot", "PidMaxP", 0.25f, "Maximum value for the P (proportional) slider.");
            PidMaxI = Config.Bind("Autopilot", "PidMaxI", 0.25f, "Maximum value for the I (integral) slider.");
            PidMaxD = Config.Bind("Autopilot", "PidMaxD", 0.25f, "Maximum value for the D (derivative) slider.");
            ExtraWorkingStaminaDrain = Config.Bind(
                "Crew",
                "ExtraWorkingStaminaDrain",
                false,
                "When enabled, crew assigned to active tasks lose stamina twice as fast. When disabled, working and idle crew use the same baseline stamina drain.");
            InstrumentationEnabled = Config.Bind(
                "Instrumentation",
                "Enabled",
                false,
                "High-level opt-in for VirtualCrew performance instrumentation. When disabled, profiling cannot run.");
            InstrumentationOutputDirectory = Config.Bind(
                "Instrumentation",
                "OutputDirectory",
                "BepInEx\\VirtualCrewProfiles",
                "Directory for profiling TSV and folded-stack files. Relative paths are resolved from the game working directory.");
            InstrumentationFlushIntervalSeconds = Config.Bind(
                "Instrumentation",
                "FlushIntervalSeconds",
                5f,
                new ConfigDescription(
                    "How often active profiling data is flushed to disk.",
                    new AcceptableValueRange<float>(1f, 60f)));
            RequireCrewForExternalModFeatures = Config.Bind(
                "Integrations",
                "RequireCrewForProfitPercentAndCargoController",
                true,
                "When enabled, VirtualCrew gates Profit Percent and Cargo Controller features behind awake Supercargo/Quartermaster crew. Disable to avoid interacting with those mods.");

            ToggleCrewWindow = Config.Bind("CrewHotkeys", "ToggleCrewWindow", new KeyboardShortcut(KeyCode.B));
            ResetWindowPositions = Config.Bind("CrewHotkeys", "ResetWindowPositions", new KeyboardShortcut(KeyCode.Backslash));
            SupercargoSellAtPortKey = Config.Bind("CrewHotkeys", "SupercargoSellAtPort", new KeyboardShortcut(KeyCode.X));
            SupercargoKeepCargoKey = Config.Bind("CrewHotkeys", "SupercargoKeepCargo", new KeyboardShortcut(KeyCode.N));
            CargoControllerGrabPortCargoKey = Config.Bind("CrewHotkeys", "CargoControllerGrabPortCargo", new KeyboardShortcut(KeyCode.Z));
            BuildShipMap = Config.Bind("CrewHotkeys", "BuildShipMap", new KeyboardShortcut(KeyCode.V));

            ScanItems = Config.Bind("CrewHotkeys", "ScanItems", new KeyboardShortcut(KeyCode.P));

            gameObject.AddComponent<CrewSoundPlayer>();
            gameObject.AddComponent<WindowLauncherWindow>();
            gameObject.AddComponent<DeveloperWindow>();
            gameObject.AddComponent<VirtualCrewDebugWindow>();
            gameObject.AddComponent<CrewWindow>();
            gameObject.AddComponent<SailGroupsWindow>();
            gameObject.AddComponent<SailGroupMembersWindow>();
            gameObject.AddComponent<WorkRequestsWindow>();
            gameObject.AddComponent<NavigatorWindow>();
            gameObject.AddComponent<NavigatorMapWindow>();
            gameObject.AddComponent<NavigatorShipLogWindow>();
            gameObject.AddComponent<MaintenanceWindow>();
            gameObject.AddComponent<SupercargoWindow>();
            gameObject.AddComponent<StewardWindow>();
            gameObject.AddComponent<FirstOfficerWindow>();
            gameObject.AddComponent<StandingOrdersWindow>();
            gameObject.AddComponent<PilotingWindow>();
            gameObject.AddComponent<CrewRosterWindow>();
            gameObject.AddComponent<LookoutWindow>();
            gameObject.AddComponent<WorkstationCustomizerWindow>();
            gameObject.AddComponent<FavoriteActionsWindow>();
        }

        private void Start()
        {
            ModIntegrations.Initialize(_harmony);
            CrewApiProbe.Run();
        }

        private void Update()
        {
            PerformanceInstrumentation.Update();

            using (PerformanceInstrumentation.Measure("Plugin.Update"))
            {
                tickTimer += Time.deltaTime;
                if (tickTimer >= tickInterval)
                {
                    tickTimer -= tickInterval;
                    using (PerformanceInstrumentation.Measure("VirtualCrewManager.Tick"))
                        VirtualCrewManager.Instance.Tick();
                }

                using (PerformanceInstrumentation.Measure("VirtualCrewManager.TrimTick"))
                    VirtualCrewManager.Instance.TrimTick();
                using (PerformanceInstrumentation.Measure("CrewDebugObjects.Tick"))
                    CrewDebugObjects.Tick();
                using (PerformanceInstrumentation.Measure("CrewNavigationCoordinator.Tick"))
                    CrewNavigationCoordinator.Instance.Tick();
                using (PerformanceInstrumentation.Measure("CargoControllerPortCargoHotkey.Tick"))
                    CargoControllerPortCargoHotkey.Tick();
                using (PerformanceInstrumentation.Measure("PlayerWaitingState.Tick"))
                    PlayerWaitingState.Tick();

                if (ResetWindowPositions.Value.IsDown())
                    ResetAllWindowPositions();

                bool requestedVesselScan = _vesselScanRequested;
                if (BuildShipMap.Value.IsDown() || requestedVesselScan)
                {
                    _vesselScanRequested = false;
	                Console.WriteLine("====================");
	                Console.WriteLine(requestedVesselScan ? "Embark-triggered ship map scan!" : "Building ship map!");
	                Console.WriteLine("====================");
                    var context = CrewBoatContextResolver.ResolveAndLog();
                    if (context == null)
                    {
                        Console.WriteLine("CRITICAL ERROR: Could not resolve active vessel context!");
                        return;
                    }

                    Transform worldBoat = context.WorldBoat;
	            // Ok, now to learn about iteration. Grab each mast, get all sails on mast, give them a name, spray output.
	            string vesselKey = worldBoat.name.Replace("(Clone)", "").Trim();
                Console.WriteLine($"Vessel detected: {worldBoat.name} (Key: {vesselKey})");
	            VirtualCrewManager.Instance.BeginVesselMapScan(vesselKey);

                // Find the true boat root (the parent with BoatRefs) to find all sibling hardware
                BoatRefs rootRefs = context.TopBoat.GetComponent<BoatRefs>() ?? worldBoat.GetComponentInParent<BoatRefs>();
                if (rootRefs == null)
                {
                    Console.WriteLine("CRITICAL ERROR: Could not find BoatRefs for current boat!");
                    return;
                }
                Transform boatRoot = rootRefs.transform;

	            // Phase 1: Scan this specific boat hierarchy for all winches and build physical mapping
	            GPButtonRopeWinch[] winches = boatRoot.GetComponentsInChildren<GPButtonRopeWinch>();
                var reefWinchesBySail = new Dictionary<Sail, GPButtonRopeWinch>();
                var angleWinchesBySail = new Dictionary<Sail, List<GPButtonRopeWinch>>();

                int validSailWinches = 0;
                int anchorWinchesFound = 0;

	            foreach (GPButtonRopeWinch winch in winches)
	            {
                    if (winch.rope == null) continue; // Ignore broken templates left by mods

                    // Only capture winches where this vessel is the nearest boat parent 
                    // (prevents capturing winches from stowed/nested boats like dinghies).
                    if (winch.GetComponentInParent<BoatRefs>() != rootRefs) continue;

                    if (winch.rope is RopeControllerAnchor)
                    {
                        VirtualCrewManager.Instance.AnchorWinches.Add(winch);
                        anchorWinchesFound++;
                        continue;
                    }

                    Sail attachedSail = findAttachedSailForWinchIfExists(winch);
                    if (attachedSail != null)
                    {
                        validSailWinches++;
                        if (winch.rope is RopeControllerSailReef)
                        {
                            if (!reefWinchesBySail.ContainsKey(attachedSail))
                                reefWinchesBySail.Add(attachedSail, winch);
                            else
                                Console.WriteLine($"WARNING: Multiple reef winches found for sail {attachedSail.name}");
                        }
                        else
                        {
                            if (!angleWinchesBySail.ContainsKey(attachedSail))
                                angleWinchesBySail.Add(attachedSail, new List<GPButtonRopeWinch>());
                            angleWinchesBySail[attachedSail].Add(winch);
                        }
                    }
	            }
                Console.WriteLine($"Found {validSailWinches} active sail winches and {anchorWinchesFound} anchors on {boatRoot.name}.");


	            Mast[] mastList = worldBoat.GetComponentsInChildren<Mast>();

	            var mastNameDictionary = new Dictionary<string, Mast>();
	            var processedSails = new HashSet<Sail>();

	            Console.WriteLine("Found the following masts:");

	            foreach (Mast mast in mastList)
	            {
		            Console.WriteLine("-" + mast.name);
		            if (!mastNameDictionary.ContainsKey(mast.name))
		            {
			            mastNameDictionary.Add(mast.name, mast);
		            }
	            }
	            Console.WriteLine("-------------------------------------------");

	            foreach (Mast mast in mastList)
	            {
		            Console.WriteLine("Processing mast:" + mast.name);

                    // DIAGNOSTIC: Check if the mast "knows" its winches
                    Console.WriteLine($"DIAGNOSTIC: Mast Blueprint -> ReefWinches: {mast.reefWinch?.Length ?? -1}, MidWinches: {mast.midAngleWinch?.Length ?? -1}, LeftWinches: {mast.leftAngleWinch?.Length ?? -1}, RightWinches: {mast.rightAngleWinch?.Length ?? -1}");

		            Sail[] sails = mast.GetComponentsInChildren<Sail>(); //get all the sails attached to said mast

		            Console.WriteLine("Mast has the following sails:");
		            foreach (Sail sail in sails)
		            {
			            Console.WriteLine("-" + sail.name);
		            }

		            // There are some cases where a mast is just an "Extension" of another mast. We need
		            // to treat it as though it's part of the main mast.
		            string possibleExtensionName = mast.name + "_extension"; 
		            if (mastNameDictionary.ContainsKey(possibleExtensionName))
		            {
			            Console.WriteLine("Mast also has an extension. Adding those sails to be processed.");
			            var extensionSails = mastNameDictionary[possibleExtensionName].GetComponentsInChildren<Sail>();
			            foreach (Sail sail in extensionSails)
			            {
				            Console.WriteLine("-" + sail.name);
			            }

			            sails = sails.AddRangeToArray(extensionSails);
		            }

		            List<Sail> deferredSquares = new List<Sail>();

		            foreach (Sail sail in sails) {
			            if (processedSails.Contains(sail))
			            {
				            Console.WriteLine("Skipping already processed sail:" + sail.name);
				            continue;
			            }

			            processedSails.Add(sail);
			            Console.WriteLine(string.Format("Processing the combination of Mast: {0}, Sail: {1}", mast.name, sail.name));

			            GPButtonRopeWinch halyardCandidate = null;
			            GPButtonRopeWinch sheetCandidate = null;
			            GPButtonRopeWinch portSheetCandidate = null;
			            GPButtonRopeWinch starboardSheetCandidate = null;

			            SailConnections connections = sail.GetComponent<SailConnections>();
                        if (connections == null)
                        {
                            Console.WriteLine("CRITICAL: Sail has no SailConnections component: " + sail.name);
                            continue;
                        }

                        // Log what components we found on this sail
                        Console.WriteLine($"Sail Capabilities: Reef={connections.reefController != null}, Mid={connections.angleControllerMid != null}, Left={connections.angleControllerLeft != null}, Right={connections.angleControllerRight != null}");

                        // Get winches using physical mapping from Phase 1
                        reefWinchesBySail.TryGetValue(sail, out halyardCandidate);
                        angleWinchesBySail.TryGetValue(sail, out List<GPButtonRopeWinch> candidates);

                        if (candidates != null)
                        {
                            foreach (var w in candidates)
                            {
                                if (w.rope == connections.angleControllerMid) sheetCandidate = w;
                                if (w.rope == connections.angleControllerLeft) portSheetCandidate = w;
                                if (w.rope == connections.angleControllerRight) starboardSheetCandidate = w;
                            }
                        }

                        string nameLower = sail.name.ToLower();

			            // Classification Logic (Square > Dual-Sheet > Single-Sheet)
			            if (sail.squareSail || nameLower.Contains("square"))
			            {
				            Console.WriteLine("Classified as: SQUARE");

				            if (portSheetCandidate == null || starboardSheetCandidate == null)
				            {
					            // This is one of those squares that is ganged to another square. 
					            Console.WriteLine("This is a deferred square sail (no sheets found attached physically)");
					            deferredSquares.Add(sail);
				            }
				            else {
					            Console.WriteLine("This is a primary square sail");

					            DualSheetSail dual = new DualSheetSail(sail, halyardCandidate, portSheetCandidate, starboardSheetCandidate, DualSheetSail.DualSheetSailSubtype.Square, mast.name);
					            VirtualCrewManager.Instance.addSquareSail(dual);
					            Console.WriteLine("Successfully added Square sail to map");
					            Console.WriteLine("---");
				            }
			            }
			            else if (portSheetCandidate != null && starboardSheetCandidate != null) {
				            Console.WriteLine("Classified as: JIB/GENOA (Dual-Sheeted)");

				            DualSheetSail dual = new DualSheetSail(sail, halyardCandidate, portSheetCandidate, starboardSheetCandidate, DualSheetSail.DualSheetSailSubtype.Jib, mast.name);
				            VirtualCrewManager.Instance.addDualSheetSail(dual);
				            Console.WriteLine("Successfully added Dual-Sheet sail to map");
			            }                            
			            else if (sheetCandidate != null)
			            {
				            Console.WriteLine("Classified as: SIMPLE (Lateen/Gaff/Junk)");
				            SimpleSail simple = new SimpleSail(sail, halyardCandidate, sheetCandidate, mast.name);
				            VirtualCrewManager.Instance.addSail(simple);
				            Console.WriteLine("Successfully added Simple sail to map");
				            Console.WriteLine("---");
			            }
			            else {
				            Console.WriteLine("Could not classify sail by physical connections. Skipping.");
			            }

		            }

		            Console.WriteLine("All sails on this mast have been first-pass-processed.");
		            if (deferredSquares.Count > 0)
		            {
			            Console.WriteLine("There are " + deferredSquares.Count + " deferred squares that need to be second-pass-processed.");
		            }

		            foreach (Sail deferredSquareSail in deferredSquares)
		            {
			            Console.WriteLine("Attempting to add deferred square sail:" + deferredSquareSail.name);
			            
                        // Find the primary sail by traversing the mirror components
                        Sail primarySail = FindPrimarySquare(deferredSquareSail);
                        if (primarySail == null)
                        {
                            Console.WriteLine("WARNING: Could not find primary square for deferred sail: " + deferredSquareSail.name);
                            continue;
                        }

                        Console.WriteLine($"Found Primary Square link: {deferredSquareSail.name} -> {primarySail.name}");

                        reefWinchesBySail.TryGetValue(deferredSquareSail, out GPButtonRopeWinch halyardWinch);
                        angleWinchesBySail.TryGetValue(primarySail, out List<GPButtonRopeWinch> primarySheets);

                        GPButtonRopeWinch portSheetWinch = null;
                        GPButtonRopeWinch starbSheetWinch = null;

                        if (primarySheets != null)
                        {
                            SailConnections primaryConnections = primarySail.GetComponent<SailConnections>();
                            foreach (var w in primarySheets)
                            {
                                if (w.rope == primaryConnections.angleControllerLeft) portSheetWinch = w;
                                if (w.rope == primaryConnections.angleControllerRight) starbSheetWinch = w;
                            }
                        }
                        else
                        {
                            Console.WriteLine($"WARNING: Primary square {primarySail.name} found for {deferredSquareSail.name}, but primary square has no sheets in angleWinchesBySail!");
                        }

			            DualSheetSail dual = new DualSheetSail(deferredSquareSail, halyardWinch, portSheetWinch, starbSheetWinch, DualSheetSail.DualSheetSailSubtype.Square, mast.name);
			            VirtualCrewManager.Instance.addSquareSail(dual);
			            Console.WriteLine("Successfully added deferred Square sail to map:" + deferredSquareSail.name);
		            }

		            Console.WriteLine("-------------------------------------------");
	            }

	            CrewNavigationCoordinator.Instance.RebuildWorkstations();
	            CrewNavigationCoordinator.Instance.RefreshActorsForCurrentVessel();
                VirtualCrewManager.Instance.FinishVesselMapScan();
                _lastScannedVesselKey = vesselKey;
            }

            if (ScanItems.Value.IsDown())
            {
                CrewNavigationCoordinator.Instance.ForceRingLookoutBell();
            }
            }
        }

        private void ResetAllWindowPositions()
        {
            foreach (var window in GetComponents<IWindowPosition>())
            {
                var position = window.GetDefaultPosition();
                if (position == null || position.Length < 2)
                    continue;

                window.SetPosition(position[0], position[1], position.Length >= 3 ? position[2] : 0f);
            }
        }

        internal void RequestEmbarkedVesselScan()
        {
            var context = CrewBoatContextResolver.Resolve();
            if (context == null || !context.PlayerEmbarked || !context.WorldBoat)
                return;

            string vesselKey = context.WorldBoat.name.Replace("(Clone)", "").Trim();
            if (!string.IsNullOrEmpty(vesselKey) && vesselKey != _lastScannedVesselKey)
                _vesselScanRequested = true;
        }

        internal void RequestVesselScan()
        {
            _vesselScanRequested = true;
        }

        private static Sail FindPrimarySquare(Sail topsail)
        {
            var mirror = topsail.GetComponent<SquareTopsailAngleMirror>();
            if (mirror == null || mirror.sailBelow == null) return null;

            HingeJoint current = mirror.sailBelow;
            int safety = 0;
            while (current != null)
            {
                if (++safety >= 20) { Console.WriteLine($"WARNING: Exceeded maximum allowed sail stack depth (20) while searching for primary square for '{topsail.name}'. Stack may be circular or too deep."); return null; }
                var nextMirror = current.GetComponent<SquareTopsailAngleMirror>();
                if (nextMirror == null || nextMirror.sailBelow == null)
                {
                    return current.GetComponent<Sail>();
                }
                current = nextMirror.sailBelow;
            }
            return null;
        }

        private static T GetPrivateField<T>(object obj, string fieldName)
            => Traverse.Create(obj).Field(fieldName).GetValue<T>();

        private static GPButtonRopeWinch GetWinch(Dictionary<RopeController, GPButtonRopeWinch> dict, RopeController controller)
        {
            if (controller != null && dict.TryGetValue(controller, out var winch))
            {
                return winch;
            }
            return null;
        }

        private Sail findAttachedSailForWinchIfExists(GPButtonRopeWinch winch)
        {
            //Console.WriteLine("Checking winch " + winch.name);
            // Check for single-sheeted sails
            if (winch.rope is RopeControllerSailAngle)
            {
                //Console.WriteLine("Winch has associated rope type of " + winch.rope.name);
                return GetPrivateField<Sail>((RopeControllerSailAngle)winch.rope, "sail");
            }
            if (winch.rope is RopeControllerSailReef)
            {
                //Console.WriteLine("Winch has associated rope type of " + winch.rope.name);
                return GetPrivateField<Sail>((RopeControllerSailReef)winch.rope, "sail");
            }

            // Check for double-sheeted sails
            if (winch.rope is RopeControllerSailAngleJib)
            {
                //Console.WriteLine("Winch has associated rope type of " + winch.rope.name);
                return GetPrivateField<Sail>(((RopeControllerSailAngleJib)winch.rope).jibAngleMaster, "sail");
            }
            if(winch.rope is RopeControllerSailAngleSquare)
            {
                //Console.WriteLine("Winch has associated rope type of " + winch.rope.name);
                return GetPrivateField<Sail>(((RopeControllerSailAngleSquare)winch.rope).squareAngleMaster, "sail");
            }

            //Console.WriteLine("Could not determine associated sail");
            return null;
        }
    }
}
